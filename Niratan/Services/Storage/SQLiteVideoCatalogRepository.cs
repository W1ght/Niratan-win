using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Novels;
using Niratan.Services.Video;

namespace Niratan.Services.Storage;

internal sealed class SQLiteVideoCatalogRepository : IVideoCatalogRepository, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private const int BusyTimeoutMilliseconds = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly string _legacyCatalogPath;
    private readonly INiratanJsonFileStore _json;
    private readonly LegacyVideoCatalogReader _legacyReader;
    private readonly ILogger<SQLiteVideoCatalogRepository> _logger;
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;
    private bool _initialized;
    private bool _legacyReadOnly;
    private VideoCatalogSnapshot _lastSnapshot = VideoCatalogSnapshot.Empty();

    public SQLiteVideoCatalogRepository(
        INiratanJsonFileStore json,
        ILogger<SQLiteVideoCatalogRepository> logger)
        : this(
            Path.Combine(AppDataHelper.GetDataPath(), "video_library.sqlite3"),
            Path.Combine(AppDataHelper.GetDataPath(), "video_library.json"),
            json,
            logger)
    {
    }

    internal SQLiteVideoCatalogRepository(
        string databasePath,
        string legacyCatalogPath,
        INiratanJsonFileStore? json = null,
        ILogger<SQLiteVideoCatalogRepository>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyCatalogPath);
        _databasePath = Path.GetFullPath(databasePath);
        _legacyCatalogPath = Path.GetFullPath(legacyCatalogPath);
        _json = json ?? new NiratanJsonFileStore();
        _legacyReader = new LegacyVideoCatalogReader(_json);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteVideoCatalogRepository>.Instance;
        _worker = Task.Run(RunQueueAsync);
    }

    public Task<VideoCatalogInitializationResult> InitializeAsync(CancellationToken ct = default) =>
        EnqueueAsync(() => InitializeCoreAsync(ct), ct);

    public Task<VideoCatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            await EnsureInitializedCoreAsync(ct);
            if (_legacyReadOnly)
                return _lastSnapshot;
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return _lastSnapshot;
        }, ct);

    public Task<VideoProviderCacheEntry?> GetProviderCacheAsync(string cacheKey, CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            await EnsureInitializedCoreAsync(ct);
            if (_legacyReadOnly)
                return null;
            await using var connection = await OpenConnectionAsync(ct);
            var row = await connection.QuerySingleOrDefaultAsync<ProviderCacheRow>(
                "SELECT * FROM provider_cache WHERE cache_key=@Key;", new { Key = cacheKey });
            return row == null
                ? null
                : new VideoProviderCacheEntry(
                    row.cache_key,
                    row.provider_id,
                    row.etag,
                    ParseDate(row.last_modified),
                    row.payload ?? [],
                    row.content_type,
                    ParseDate(row.fetched_at) ?? DateTimeOffset.UnixEpoch,
                    ParseDate(row.expires_at) ?? DateTimeOffset.UnixEpoch);
        }, ct);

    public Task UpsertProviderCacheAsync(VideoProviderCacheEntry entry, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO provider_cache(cache_key,provider_id,etag,last_modified,payload,content_type,fetched_at,expires_at)
                VALUES(@Key,@Provider,@ETag,@LastModified,@Payload,@ContentType,@Fetched,@Expires)
                ON CONFLICT(cache_key) DO UPDATE SET etag=excluded.etag,last_modified=excluded.last_modified,
                    payload=excluded.payload,content_type=excluded.content_type,fetched_at=excluded.fetched_at,expires_at=excluded.expires_at;
                """,
                new
                {
                    Key = entry.CacheKey,
                    Provider = entry.ProviderId,
                    entry.ETag,
                    LastModified = entry.LastModified.HasValue ? ToDb(entry.LastModified.Value) : null,
                    entry.Payload,
                    entry.ContentType,
                    Fetched = ToDb(entry.FetchedAt),
                    Expires = ToDb(entry.ExpiresAt),
                }, transaction);
        }, ct);

    public Task ApplyArtworkAsync(
        Guid assetId,
        VideoMetadataMediaKind ownerKind,
        string providerId,
        string kind,
        string remoteUrl,
        string localPath,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct = default) =>
        UpsertArtworkCandidateAsync(
            assetId,
            ownerKind,
            new VideoArtworkCandidate(providerId, remoteUrl, kind, null, null, null, null),
            localPath,
            etag,
            lastModified,
            downloadAttempted: false,
            lastError: null,
            ct: ct);

    public Task UpsertArtworkCandidateAsync(
        Guid assetId,
        VideoMetadataMediaKind defaultOwnerKind,
        VideoArtworkCandidate candidate,
        string? localPath = null,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        bool downloadAttempted = false,
        string? lastError = null,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var nodeId = await ResolveArtworkOwnerNodeIdAsync(
                connection, transaction, assetId, candidate.OwnerKind ?? defaultOwnerKind,
                candidate.SeasonNumber, candidate.EpisodeNumber);
            if (nodeId == null)
                return;
            var existingId = await connection.ExecuteScalarAsync<string?>(
                """
                SELECT id FROM artwork
                WHERE node_id=@Node AND provider_id=@Provider AND kind=@Kind AND remote_url=@Url
                ORDER BY created_at,id LIMIT 1;
                """,
                new
                {
                    Node = nodeId,
                    Provider = candidate.ProviderId,
                    Kind = candidate.Kind,
                    Url = candidate.Url,
                }, transaction);
            var hasStablePreference = await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM artwork a
                WHERE a.node_id=@Node AND a.kind=@Kind
                  AND (a.selected=1 OR a.preferred=1 OR a.id=(
                      SELECT preferred_artwork_id FROM node_user_data WHERE node_id=@Node));
                """,
                new { Node = nodeId, Kind = candidate.Kind }, transaction) > 0;
            var makeDefault = !hasStablePreference;
            var now = ToDb(DateTimeOffset.UtcNow);
            if (existingId != null)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE artwork SET
                        local_path=COALESCE(@Path,local_path),
                        etag=COALESCE(@ETag,etag),
                        last_modified=COALESCE(@LastModified,last_modified),
                        language=COALESCE(@Language,language),
                        width=COALESCE(@Width,width),height=COALESCE(@Height,height),
                        attribution_url=COALESCE(@Attribution,attribution_url),
                        enabled=@Enabled,desired=@Desired,ordinal=@Ordinal,
                        download_attempts=download_attempts+@Attempted,
                        last_error=CASE
                            WHEN @Path IS NOT NULL THEN NULL
                            WHEN @Attempted=1 THEN @LastError
                            ELSE last_error END,
                        updated_at=@Now
                    WHERE id=@Id;
                    """,
                    new
                    {
                        Id = existingId,
                        Path = localPath,
                        ETag = etag,
                        LastModified = lastModified.HasValue ? ToDb(lastModified.Value) : null,
                        candidate.Language,
                        candidate.Width,
                        candidate.Height,
                        Attribution = candidate.AttributionUrl,
                        Enabled = candidate.IsEnabled ? 1 : 0,
                        Desired = candidate.IsDesired ? 1 : 0,
                        candidate.Ordinal,
                        Attempted = downloadAttempted ? 1 : 0,
                        LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError,
                        Now = now,
                    }, transaction);
                return;
            }

            await connection.ExecuteAsync(
                """
                INSERT INTO artwork(
                    id,node_id,provider_id,kind,remote_url,local_path,etag,last_modified,
                    language,width,height,attribution_url,enabled,desired,preferred,selected,
                    ordinal,download_attempts,last_error,created_at,updated_at)
                VALUES(
                    @Id,@Node,@Provider,@Kind,@Url,@Path,@ETag,@LastModified,
                    @Language,@Width,@Height,@Attribution,@Enabled,@Desired,@Preferred,@Selected,
                    @Ordinal,@Attempted,@LastError,@Now,@Now);
                """,
                new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Node = nodeId,
                    Provider = candidate.ProviderId,
                    Kind = candidate.Kind,
                    Url = candidate.Url,
                    Path = localPath,
                    ETag = etag,
                    LastModified = lastModified.HasValue ? ToDb(lastModified.Value) : null,
                    candidate.Language,
                    candidate.Width,
                    candidate.Height,
                    Attribution = candidate.AttributionUrl,
                    Enabled = candidate.IsEnabled ? 1 : 0,
                    Desired = candidate.IsDesired ? 1 : 0,
                    Preferred = makeDefault ? 1 : 0,
                    Selected = makeDefault ? 1 : 0,
                    candidate.Ordinal,
                    Attempted = downloadAttempted ? 1 : 0,
                    LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError,
                    Now = now,
                }, transaction);
        }, ct);

    private static async Task<string?> ResolveArtworkOwnerNodeIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        VideoMetadataMediaKind ownerKind,
        int? seasonNumber,
        int? episodeNumber)
    {
        var asset = assetId.ToString("D");
        if (ownerKind == VideoMetadataMediaKind.Movie)
        {
            return await connection.ExecuteScalarAsync<string?>(
                """
                SELECT n.id FROM catalog_nodes n
                JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset AND n.kind='movie' LIMIT 1;
                """,
                new { Asset = asset }, transaction);
        }

        var rootId = await connection.ExecuteScalarAsync<string?>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind,depth) AS (
                SELECT n.id,n.parent_id,n.kind,0 FROM catalog_nodes n
                JOIN node_assets na ON na.node_id=n.id WHERE na.asset_id=@Asset
                UNION ALL
                SELECT parent.id,parent.parent_id,parent.kind,child.depth+1
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT id FROM ancestry WHERE kind='series' ORDER BY depth DESC LIMIT 1;
            """,
            new { Asset = asset }, transaction);
        if (ownerKind is VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Anime)
        {
            return rootId ?? await connection.ExecuteScalarAsync<string?>(
                "SELECT node_id FROM node_assets WHERE asset_id=@Asset ORDER BY ordinal LIMIT 1;",
                new { Asset = asset }, transaction);
        }
        if (rootId == null)
            return null;

        if (ownerKind == VideoMetadataMediaKind.Season)
        {
            return await connection.ExecuteScalarAsync<string?>(
                """
                SELECT id FROM catalog_nodes
                WHERE parent_id=@Root AND kind='season'
                  AND (@Season IS NULL OR season_number=@Season)
                ORDER BY CASE WHEN season_number=@Season THEN 0 ELSE 1 END,season_number,id
                LIMIT 1;
                """,
                new { Root = rootId, Season = seasonNumber }, transaction);
        }

        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT episode.id
            FROM catalog_nodes episode
            LEFT JOIN catalog_nodes season ON season.id=episode.parent_id AND season.kind='season'
            WHERE episode.kind='episode'
              AND (episode.parent_id=@Root OR season.parent_id=@Root)
              AND (@Season IS NULL OR COALESCE(episode.season_number,season.season_number)=@Season)
              AND (@Episode IS NULL OR episode.episode_number=@Episode)
            ORDER BY
              CASE WHEN COALESCE(episode.season_number,season.season_number)=@Season THEN 0 ELSE 1 END,
              CASE WHEN episode.episode_number=@Episode THEN 0 ELSE 1 END,
              episode.id
            LIMIT 1;
            """,
            new { Root = rootId, Season = seasonNumber, Episode = episodeNumber }, transaction);
    }

    public Task UpsertAssetAsync(VideoCatalogAssetUpsert asset, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await UpsertAssetCoreAsync(connection, transaction, asset, null);
        }, ct);

    public Task UpdateAssetUserDataAsync(VideoCatalogUserDataUpdate update, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var assetId = await GetAssetIdAsync(connection, transaction, update.IdentityKey)
                ?? throw new KeyNotFoundException("Video asset was not found.");
            await connection.ExecuteAsync(
                """
                INSERT INTO asset_user_data(
                    asset_id, display_title, is_favorite, bound_subtitle_path, poster_path, profile_id, updated_at)
                VALUES(@AssetId, @DisplayTitle, COALESCE(@IsFavorite, 0), @Subtitle, @Poster, @Profile, @Now)
                ON CONFLICT(asset_id) DO UPDATE SET
                    display_title = excluded.display_title,
                    is_favorite = COALESCE(@IsFavorite, asset_user_data.is_favorite),
                    bound_subtitle_path = excluded.bound_subtitle_path,
                    poster_path = excluded.poster_path,
                    profile_id = excluded.profile_id,
                    updated_at = excluded.updated_at;
                """,
                new
                {
                    AssetId = assetId.ToString("D"),
                    update.DisplayTitle,
                    IsFavorite = update.IsFavorite.HasValue ? (update.IsFavorite.Value ? 1 : 0) : (int?)null,
                    Subtitle = NormalizeOptionalPath(update.BoundSubtitlePath),
                    Poster = NormalizeOptionalPath(update.PosterPath),
                    update.ProfileId,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
            await ReplaceTagsAsync(connection, transaction, assetId, update.Tags);
        }, ct);

    public Task SetAssetHiddenAsync(string identityKey, bool hidden, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "UPDATE media_assets SET is_hidden = @Hidden WHERE identity_key = @Identity COLLATE NOCASE;",
                new { Hidden = hidden ? 1 : 0, Identity = NormalizeIdentity(identityKey) }, transaction);
        }, ct);

    public Task MarkAssetsUnavailableAsync(
        IReadOnlyList<string> identityKeys,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            foreach (var identity in identityKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await connection.ExecuteAsync(
                    "UPDATE media_assets SET availability = 'unavailable' WHERE identity_key = @Identity COLLATE NOCASE;",
                    new { Identity = NormalizeIdentity(identity) }, transaction);
            }
        }, ct);

    public Task UpsertSourceAsync(VideoLibrarySource source, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var id = ParseGuid(source.Id);
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.FolderPath));
            await connection.ExecuteAsync(
                """
                INSERT INTO library_sources(
                    id, name, folder_path, normalized_folder_path, media_type, language, region,
                    scan_generation, created_at, last_scanned_at, last_error)
                VALUES(@Id, @Name, @Path, @Normalized, @MediaType, @Language, @Region, @Generation, @Created, @Scanned, @Error)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    folder_path = excluded.folder_path,
                    normalized_folder_path = excluded.normalized_folder_path,
                    media_type = excluded.media_type,
                    language = excluded.language,
                    region = excluded.region,
                    last_scanned_at = excluded.last_scanned_at,
                    last_error = excluded.last_error;
                """,
                new
                {
                    Id = id.ToString("D"),
                    source.Name,
                    Path = fullPath,
                    Normalized = fullPath.ToUpperInvariant(),
                    MediaType = ToDb(source.MediaType),
                    Language = string.IsNullOrWhiteSpace(source.Language) ? "ja-JP" : source.Language.Trim(),
                    Region = string.IsNullOrWhiteSpace(source.Region) ? "JP" : source.Region.Trim(),
                    Generation = Math.Max(0, source.ScanGeneration),
                    Created = ToDb(ToOffset(source.CreatedAt)),
                    Scanned = source.LastScannedAt.HasValue ? ToDb(ToOffset(source.LastScannedAt.Value)) : null,
                    Error = source.LastError,
                }, transaction);
            await connection.ExecuteAsync(
                "DELETE FROM source_provider_routes WHERE source_id=@Id;",
                new { Id = id.ToString("D") }, transaction);
            var ordinal = 0;
            foreach (var providerId in source.ProviderOrder
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO source_provider_routes(source_id,provider_id,ordinal,enabled) VALUES(@Id,@Provider,@Ordinal,1);",
                    new { Id = id.ToString("D"), Provider = providerId.Trim().ToLowerInvariant(), Ordinal = ordinal++ },
                    transaction);
            }
        }, ct);

    public Task UpdateSourceScanStateAsync(
        Guid sourceId,
        DateTimeOffset? lastScannedAt,
        string? error,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "UPDATE library_sources SET last_scanned_at=@Scanned, last_error=@Error WHERE id=@Id;",
                new
                {
                    Id = sourceId.ToString("D"),
                    Scanned = lastScannedAt.HasValue ? ToDb(lastScannedAt.Value) : null,
                    Error = error,
                }, transaction);
        }, ct);

    public Task<long> BeginSourceScanAsync(
        Guid sourceId,
        VideoCatalogJobKind kind,
        CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            var generation = await BeginSourceScanCoreAsync(
                sourceId, kind, expectedGeneration: null, ct);
            return generation
                   ?? throw new InvalidOperationException("An unconditional video scan could not be started.");
        }, ct);

    public Task<long?> TryBeginSourceScanAsync(
        Guid sourceId,
        VideoCatalogJobKind kind,
        long expectedGeneration,
        CancellationToken ct = default) =>
        EnqueueAsync(
            () => BeginSourceScanCoreAsync(sourceId, kind, expectedGeneration, ct),
            ct);

    private async Task<long?> BeginSourceScanCoreAsync(
        Guid sourceId,
        VideoCatalogJobKind kind,
        long? expectedGeneration,
        CancellationToken ct)
    {
        await EnsureWritableCoreAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var source = sourceId.ToString("D");
        var generation = expectedGeneration.HasValue
            ? await connection.ExecuteScalarAsync<long?>(
                """
                UPDATE library_sources
                SET scan_generation=scan_generation+1
                WHERE id=@Id AND scan_generation=@ExpectedGeneration
                RETURNING scan_generation;
                """,
                new { Id = source, ExpectedGeneration = expectedGeneration.Value },
                transaction)
            : await connection.ExecuteScalarAsync<long?>(
                """
                UPDATE library_sources
                SET scan_generation=scan_generation+1
                WHERE id=@Id
                RETURNING scan_generation;
                """,
                new { Id = source },
                transaction);
        if (!generation.HasValue)
        {
            var sourceExists = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM library_sources WHERE id=@Id;",
                new { Id = source },
                transaction) != 0;
            await transaction.RollbackAsync(ct);
            if (!sourceExists)
                throw new KeyNotFoundException("Video source was not found.");
            return null;
        }

        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            UPDATE catalog_jobs SET state='cancelled', updated_at=@Now
            WHERE source_id=@SourceId AND kind IN ('incremental_scan','full_scan')
              AND state IN ('queued','running','paused');
            INSERT INTO catalog_jobs(
                id, source_id, kind, state, generation, processed_count, total_count, created_at, updated_at)
            VALUES(@Id, @SourceId, @Kind, 'running', @Generation, 0, 0, @Now, @Now);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                SourceId = source,
                Kind = ToDb(kind),
                Generation = generation.Value,
                Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
        _lastSnapshot = await ReadSnapshotAsync(ct);
        return generation.Value;
    }

    public Task<bool> ApplyScanBatchAsync(VideoScanBatch batch, CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            await EnsureWritableCoreAsync(ct);
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var generation = await connection.ExecuteScalarAsync<long?>(
                "SELECT scan_generation FROM library_sources WHERE id=@Id;",
                new { Id = batch.SourceId.ToString("D") }, transaction);
            if (generation != batch.ExpectedGeneration)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
            var isFullScan = await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM catalog_jobs
                WHERE source_id=@Source AND generation=@Generation
                  AND kind='full_scan' AND state IN ('running','paused');
                """,
                new
                {
                    Source = batch.SourceId.ToString("D"),
                    Generation = batch.ExpectedGeneration,
                }, transaction) != 0;
            if (isFullScan)
            {
                await connection.ExecuteAsync(
                    """
                    DELETE FROM catalog_reset_sources WHERE source_id=@Source;
                    DELETE FROM catalog_reset_assets
                    WHERE asset_id IN (
                        SELECT asset_id FROM source_assets WHERE source_id=@Source);
                    """,
                    new { Source = batch.SourceId.ToString("D") }, transaction);
            }

            foreach (var item in batch.Assets)
            {
                var upsert = item.Asset with { SourceId = batch.SourceId };
                var assetId = await UpsertAssetCoreAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    upsert,
                    batch.ExpectedGeneration);
                var catalogResetPending = await connection.ExecuteScalarAsync<long>(
                    """
                    SELECT CASE WHEN
                        EXISTS(SELECT 1 FROM catalog_reset_assets
                               WHERE asset_id=@Asset)
                        OR EXISTS(
                            SELECT 1 FROM catalog_reset_sources
                            WHERE source_id=@Source)
                        THEN 1 ELSE 0 END;
                    """,
                    new
                    {
                        Asset = assetId.ToString("D"),
                        Source = batch.SourceId.ToString("D"),
                    }, transaction) != 0;
                if (catalogResetPending && !isFullScan)
                    continue;
                await ApplyParsedIdentityAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    assetId,
                    batch.SourceId,
                    item.ParsedIdentity,
                    item.LocalMetadata,
                    applyMetadata: !item.SkipMetadataProcessing,
                    rebuildHierarchy: item.RebuildHierarchy);
            }

            var now = ToDb(DateTimeOffset.UtcNow);
            if (!batch.IsFinal)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_jobs SET processed_count=processed_count+@Count,
                        total_count=COALESCE(@TotalCount, total_count), updated_at=@Now
                    WHERE source_id=@SourceId AND generation=@Generation
                      AND kind IN ('incremental_scan','full_scan') AND state='running';
                    """,
                    new
                    {
                        SourceId = batch.SourceId.ToString("D"),
                        Generation = batch.ExpectedGeneration,
                        Count = batch.Assets.Count,
                        TotalCount = batch.TotalCount,
                        Now = now,
                    }, transaction);
            }
            else if (batch.EnumerationCompleted)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE media_assets
                    SET availability='unavailable'
                    WHERE id IN (
                        SELECT sa.asset_id FROM source_assets sa
                        WHERE sa.source_id=@SourceId AND sa.last_seen_generation<>@Generation
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM source_assets other
                        WHERE other.asset_id=media_assets.id
                          AND other.source_id<>@SourceId
                          AND other.last_seen_generation=(
                              SELECT scan_generation FROM library_sources WHERE id=other.source_id)
                    );
                    UPDATE library_sources SET last_scanned_at=@Scanned, last_error=NULL WHERE id=@SourceId;
                    UPDATE catalog_jobs SET state='completed', processed_count=@Count,
                        total_count=@Count, updated_at=@Now, error=NULL
                    WHERE source_id=@SourceId AND generation=@Generation
                      AND kind IN ('incremental_scan','full_scan') AND state='running';
                    """,
                    new
                    {
                        SourceId = batch.SourceId.ToString("D"),
                        Generation = batch.ExpectedGeneration,
                        Scanned = ToDb(batch.ScannedAt),
                        Count = await connection.ExecuteScalarAsync<int>(
                            "SELECT COUNT(*) FROM source_assets WHERE source_id=@SourceId AND last_seen_generation=@Generation;",
                            new
                            {
                                SourceId = batch.SourceId.ToString("D"),
                                Generation = batch.ExpectedGeneration,
                            }, transaction),
                        Now = now,
                    }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE library_sources SET last_error=@Error WHERE id=@SourceId;
                    UPDATE catalog_jobs SET state='failed', processed_count=@Count,
                        updated_at=@Now, error=@Error
                    WHERE source_id=@SourceId AND generation=@Generation
                      AND kind IN ('incremental_scan','full_scan') AND state='running';
                    """,
                    new
                    {
                        SourceId = batch.SourceId.ToString("D"),
                        Generation = batch.ExpectedGeneration,
                        Error = batch.Error ?? "Video source enumeration did not complete.",
                        Count = batch.Assets.Count,
                        Now = now,
                    }, transaction);
            }
            if (batch.IsFinal && batch.EnumerationCompleted)
                await PruneEmptyAutomaticNodesAsync(connection, (SqliteTransaction)transaction);
            await transaction.CommitAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return true;
        }, ct);

    public Task<Guid> BeginMetadataRefreshAsync(
        Guid sourceId,
        int totalCount,
        CancellationToken ct = default) =>
        EnqueueAsync(async () =>
        {
            await EnsureWritableCoreAsync(ct);
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var generation = await connection.ExecuteScalarAsync<long?>(
                "SELECT scan_generation FROM library_sources WHERE id=@SourceId;",
                new { SourceId = sourceId.ToString("D") }, transaction)
                ?? throw new KeyNotFoundException("Video source was not found.");
            var id = Guid.NewGuid();
            var now = ToDb(DateTimeOffset.UtcNow);
            await connection.ExecuteAsync(
                """
                UPDATE catalog_jobs SET state='cancelled',updated_at=@Now
                WHERE source_id=@SourceId AND kind='metadata_refresh'
                  AND state IN ('queued','running','paused');
                INSERT INTO catalog_jobs(
                    id,source_id,kind,state,generation,processed_count,total_count,created_at,updated_at)
                VALUES(@Id,@SourceId,'metadata_refresh','running',@Generation,0,@Total,@Now,@Now);
                """,
                new
                {
                    Id = id.ToString("D"),
                    SourceId = sourceId.ToString("D"),
                    Generation = generation,
                    Total = Math.Max(0, totalCount),
                    Now = now,
                }, transaction);
            await transaction.CommitAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return id;
        }, ct);

    public Task UpdateMetadataRefreshAsync(
        Guid jobId,
        VideoCatalogJobState state,
        int processedCount,
        string? error,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                """
                UPDATE catalog_jobs SET state=@State,processed_count=@Processed,
                    error=@Error,updated_at=@Now
                WHERE id=@Id AND kind='metadata_refresh';
                """,
                new
                {
                    Id = jobId.ToString("D"),
                    State = state switch
                    {
                        VideoCatalogJobState.Queued => "queued",
                        VideoCatalogJobState.Running => "running",
                        VideoCatalogJobState.Paused => "paused",
                        VideoCatalogJobState.Completed => "completed",
                        VideoCatalogJobState.Cancelled => "cancelled",
                        VideoCatalogJobState.Interrupted => "interrupted",
                        _ => "failed",
                    },
                    Processed = Math.Max(0, processedCount),
                    Error = error,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }, ct);

    public Task SetSourceScanPausedAsync(Guid sourceId, bool paused, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "UPDATE catalog_jobs SET state=@State,updated_at=@Now WHERE source_id=@Source AND kind IN ('incremental_scan','full_scan') AND state IN ('running','paused');",
                new
                {
                    State = paused ? "paused" : "running",
                    Now = ToDb(DateTimeOffset.UtcNow),
                    Source = sourceId.ToString("D"),
                }, transaction);
        }, ct);

    public Task CancelSourceScanAsync(Guid sourceId, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var now = ToDb(DateTimeOffset.UtcNow);
            await connection.ExecuteAsync(
                """
                UPDATE library_sources SET scan_generation=scan_generation+1 WHERE id=@Id;
                UPDATE catalog_jobs SET state='cancelled', updated_at=@Now
                WHERE source_id=@Id AND kind IN ('incremental_scan','full_scan')
                  AND state IN ('queued','running','paused');
                """,
                new { Id = sourceId.ToString("D"), Now = now }, transaction);
        }, ct);

    public Task<bool> CancelSourceScanAsync(
        Guid sourceId,
        long expectedGeneration,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var source = sourceId.ToString("D");
            var cancelledGeneration = await connection.ExecuteScalarAsync<long?>(
                """
                UPDATE library_sources
                SET scan_generation=scan_generation+1
                WHERE id=@Id AND scan_generation=@ExpectedGeneration
                RETURNING scan_generation;
                """,
                new { Id = source, ExpectedGeneration = expectedGeneration },
                transaction);
            if (!cancelledGeneration.HasValue)
                return false;

            await connection.ExecuteAsync(
                """
                UPDATE catalog_jobs SET state='cancelled', updated_at=@Now
                WHERE source_id=@Id AND generation=@ExpectedGeneration
                  AND kind IN ('incremental_scan','full_scan')
                  AND state IN ('queued','running','paused');
                """,
                new
                {
                    Id = source,
                    ExpectedGeneration = expectedGeneration,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
            return true;
        }, ct);

    public Task RemoveSourceAsync(Guid sourceId, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var affected = (await connection.QueryAsync<string>(
                "SELECT asset_id FROM source_assets WHERE source_id=@Id;",
                new { Id = sourceId.ToString("D") }, transaction)).ToList();
            await connection.ExecuteAsync(
                "DELETE FROM library_sources WHERE id=@Id;",
                new { Id = sourceId.ToString("D") }, transaction);
            foreach (var assetId in affected)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE media_assets SET availability='unavailable'
                    WHERE id=@Id AND NOT EXISTS(SELECT 1 FROM source_assets WHERE asset_id=@Id);
                    """,
                    new { Id = assetId }, transaction);
            }
        }, ct);

    public Task UpsertCollectionAsync(VideoCollection collection, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var id = ParseGuid(collection.Id);
            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(
                """
                INSERT INTO collections(id, name, kind, manual_sort_order, created_at, updated_at)
                VALUES(@Id, @Name, @Kind, @Sort, @Created, @Updated)
                ON CONFLICT(id) DO UPDATE SET
                    name=excluded.name, kind=excluded.kind,
                    manual_sort_order=excluded.manual_sort_order, updated_at=excluded.updated_at;
                DELETE FROM collection_rules WHERE collection_id=@Id;
                """,
                new
                {
                    Id = id.ToString("D"),
                    collection.Name,
                    Kind = collection.Kind == VideoCollectionKind.Smart ? "smart" : "manual",
                    Sort = collection.ManualSortOrder,
                    Created = ToDb(ToOffset(collection.CreatedAt == default ? now.UtcDateTime : collection.CreatedAt)),
                    Updated = ToDb(ToOffset(collection.UpdatedAt == default ? now.UtcDateTime : collection.UpdatedAt)),
                }, transaction);
            var ordinal = 0;
            foreach (var rule in collection.SmartRules)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO collection_rules(id, collection_id, ordinal, rule_json) VALUES(@Id,@CollectionId,@Ordinal,@Json);",
                    new
                    {
                        Id = ParseGuid(rule.Id).ToString("D"),
                        CollectionId = id.ToString("D"),
                        Ordinal = ordinal++,
                        Json = JsonSerializer.Serialize(rule, JsonOptions),
                    }, transaction);
            }
            if (collection.Kind == VideoCollectionKind.Smart)
            {
                await connection.ExecuteAsync(
                    "DELETE FROM collection_assets WHERE collection_id=@Id;",
                    new { Id = id.ToString("D") }, transaction);
            }
        }, ct);

    public Task DeleteCollectionAsync(Guid collectionId, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "DELETE FROM collections WHERE id=@Id;",
                new { Id = collectionId.ToString("D") }, transaction);
        }, ct);

    public Task SetCollectionAssetsAsync(
        Guid collectionId,
        IReadOnlyList<string> identityKeys,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "DELETE FROM collection_assets WHERE collection_id=@Id;",
                new { Id = collectionId.ToString("D") }, transaction);
            var ordinal = 0;
            foreach (var identity in identityKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var assetId = await GetAssetIdAsync(connection, transaction, identity);
                if (!assetId.HasValue)
                    continue;
                await connection.ExecuteAsync(
                    "INSERT INTO collection_assets(collection_id, asset_id, ordinal) VALUES(@CollectionId,@AssetId,@Ordinal);",
                    new
                    {
                        CollectionId = collectionId.ToString("D"),
                        AssetId = assetId.Value.ToString("D"),
                        Ordinal = ordinal++,
                    }, transaction);
            }
        }, ct);

    public Task ReplaceMatchCandidatesAsync(
        Guid assetId,
        IReadOnlyList<VideoMatchCandidateSnapshot> candidates,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "DELETE FROM match_candidates WHERE asset_id=@AssetId;",
                new { AssetId = assetId.ToString("D") }, transaction);
            foreach (var candidate in candidates)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO match_candidates(
                        id, asset_id, provider_id, provider_item_id, title, year, score,
                        title_score, evidence, hard_conflict, created_at)
                    VALUES(@Id,@AssetId,@Provider,@ProviderItem,@Title,@Year,@Score,@TitleScore,@Evidence,@Conflict,@Created);
                    """,
                    new
                    {
                        Id = candidate.Id.ToString("D"),
                        AssetId = assetId.ToString("D"),
                        Provider = candidate.ProviderId,
                        ProviderItem = candidate.ProviderItemId,
                        candidate.Title,
                        candidate.Year,
                        candidate.Score,
                        candidate.TitleScore,
                        candidate.Evidence,
                        Conflict = candidate.HasHardConflict ? 1 : 0,
                        Created = ToDb(candidate.CreatedAt),
                    }, transaction);
            }
        }, ct);

    public Task<bool> ApplyMetadataMatchAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        VideoMetadataDetails? details,
        bool lockIdentity,
        bool preserveExistingHierarchy,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            var assetRow = await connection.QuerySingleOrDefaultAsync<AssetRow>(
                "SELECT * FROM media_assets WHERE id=@Id;",
                new { Id = assetId.ToString("D") }, transaction)
                ?? throw new KeyNotFoundException("Video asset was not found.");
            var currentNodes = (await connection.QueryAsync<string>(
                "SELECT node_id FROM node_assets WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction)).ToList();
            var metadata = (details ?? new VideoMetadataDetails(
                candidate.ProviderId,
                candidate.ProviderItemId,
                candidate.MediaKind,
                candidate.Title,
                candidate.OriginalTitle,
                null,
                null,
                candidate.Year,
                candidate.SeasonNumber,
                candidate.EpisodeNumber,
                candidate.AbsoluteEpisodeNumber,
                candidate.Aliases,
                [],
                [],
                candidate.ExternalIds,
                candidate.SourceUrl,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(30))).WithInitializedCollections();
            // The accepted candidate owns catalog identity. A richer provider may
            // supply the descriptive payload, but it must remain a cross-reference
            // instead of replacing an explicit AniDB AID (Shoko's AnimeSeries key).
            metadata = metadata with
            {
                ExternalIds = metadata.ExternalIds
                    .SetItems(candidate.ExternalIds)
                    .SetItem(candidate.ProviderId, candidate.ProviderItemId),
            };
            var identityProviderId = candidate.ProviderId;
            var identityProviderItemId = candidate.ProviderItemId;
            var allowLockedIdentityReplacement = lockIdentity && !preserveExistingHierarchy;

            var isExplicitMovieSource = await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM source_assets sa
                JOIN library_sources s ON s.id=sa.source_id
                WHERE sa.asset_id=@Asset AND s.media_type='movie';
                """,
                new { Asset = assetId.ToString("D") }, transaction) > 0;
            Guid? structuredSeries = null;
            var hasAutomaticRebindRisk = false;
            if (preserveExistingHierarchy)
            {
                structuredSeries = await FindBoundAncestorNodeIdAsync(
                    connection, transaction, assetId, "series");
                var hasEpisodicHierarchy = await connection.ExecuteScalarAsync<long>(
                    """
                    WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                        SELECT n.id,n.parent_id,n.kind
                        FROM catalog_nodes n
                        JOIN node_assets na ON na.node_id=n.id
                        WHERE na.asset_id=@Asset
                        UNION ALL
                        SELECT parent.id,parent.parent_id,parent.kind
                        FROM catalog_nodes parent
                        JOIN ancestry child ON child.parent_id=parent.id
                    )
                    SELECT COUNT(*) FROM ancestry WHERE kind IN ('series','season','episode');
                    """,
                    new { Asset = assetId.ToString("D") }, transaction) > 0;
                var hasBoundMovie = await connection.ExecuteScalarAsync<long>(
                    """
                    SELECT COUNT(*) FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset AND n.kind='movie';
                    """,
                    new { Asset = assetId.ToString("D") }, transaction) > 0;
                hasAutomaticRebindRisk = !structuredSeries.HasValue
                                         && (hasEpisodicHierarchy || hasBoundMovie);
                if (hasEpisodicHierarchy && metadata.MediaKind == VideoMetadataMediaKind.Movie)
                {
                    var rebuild = isExplicitMovieSource
                        ? await GetHierarchyRebuildDecisionAsync(connection, transaction, assetId)
                        : HierarchyRebuildDecision.Denied;
                    var otherHierarchyAssets = isExplicitMovieSource
                        ? await CountOtherHierarchyAssetsAsync(connection, transaction, assetId)
                        : 1;
                    if (!rebuild.CanRebuild
                        || rebuild.PreservedSeriesId.HasValue
                        || otherHierarchyAssets != 0)
                    {
                        // Keep candidates intact so an unsafe automatic Movie result remains in
                        // Needs Review. Only an explicit Movie source with an unprotected,
                        // single-asset legacy hierarchy may be corrected automatically.
                        return false;
                    }
                }
            }

            var lockedTarget = await connection.QuerySingleOrDefaultAsync<LockedNodeRow>(
                """
                WITH RECURSIVE ancestry(id,parent_id,kind,identity_locked) AS (
                    SELECT n.id,n.parent_id,n.kind,n.identity_locked
                    FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset
                    UNION
                    SELECT parent.id,parent.parent_id,parent.kind,parent.identity_locked
                    FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
                )
                SELECT a.id,a.kind FROM ancestry a
                JOIN external_ids e ON e.node_id=a.id
                WHERE e.provider_id=@Provider AND e.external_id=@ExternalId
                  AND e.is_identity_locked=1
                ORDER BY CASE a.kind WHEN 'series' THEN 0 WHEN 'movie' THEN 0 WHEN 'season' THEN 1 ELSE 2 END
                LIMIT 1;
                """,
                new
                {
                    Asset = assetId.ToString("D"),
                    Provider = identityProviderId,
                    ExternalId = identityProviderItemId,
                }, transaction);
            if (lockedTarget != null
                && IsMetadataKindCompatibleWithNode(lockedTarget.kind, metadata.MediaKind))
            {
                var lockedTargetId = Guid.Parse(lockedTarget.id);
                var identityProvidersToLock = await ResolveIdentityProvidersToLockAsync(
                    connection, transaction, lockedTargetId, identityProviderId,
                    lockIdentity, preserveExistingHierarchy);
                await ApplyNodeMetadataAsync(
                    connection,
                    transaction,
                    lockedTargetId,
                    lockedTarget.kind,
                    metadata,
                    identityProvidersToLock,
                    allowLockedIdentityReplacement);
                await connection.ExecuteAsync(
                    "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                    new { Asset = assetId.ToString("D") }, transaction);
                await connection.ExecuteAsync(
                    "DELETE FROM catalog_reset_assets WHERE asset_id=@Asset;",
                    new { Asset = assetId.ToString("D") }, transaction);
                return true;
            }

            if (preserveExistingHierarchy
                && metadata.MediaKind != VideoMetadataMediaKind.Movie
                && hasAutomaticRebindRisk)
            {
                var rebuildDecision = await GetHierarchyRebuildDecisionAsync(
                    connection, transaction, assetId);
                if (!rebuildDecision.CanRebuild)
                    return false;
            }

            // Series/anime matches enrich the scanner-owned series identity. They must not
            // rebuild an already structured local Series -> Season -> Episode hierarchy: series
            // providers normally return no season number, which previously flattened every
            // episode under the series and made regular videos collide with PV/menu assets.
            if (preserveExistingHierarchy
                && (metadata.MediaKind is VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Anime))
            {
                if (structuredSeries.HasValue)
                {
                    var canonicalSeries = await FindCanonicalSeriesNodeAsync(
                        connection,
                        transaction,
                        assetId,
                        structuredSeries.Value,
                        identityProviderId,
                        identityProviderItemId);
                    if (canonicalSeries.HasValue && canonicalSeries.Value != structuredSeries.Value
                        && await CanMergeAutomaticSeriesAsync(
                            connection, transaction, structuredSeries.Value) == 1)
                    {
                        await MergeAutomaticSeriesHierarchyAsync(
                            connection, transaction, structuredSeries.Value, canonicalSeries.Value);
                        structuredSeries = canonicalSeries;
                    }

                    var identityProvidersToLock = await ResolveIdentityProvidersToLockAsync(
                        connection, transaction, structuredSeries.Value, identityProviderId,
                        lockIdentity, preserveExistingHierarchy);
                    await ApplyNodeMetadataAsync(
                        connection,
                        transaction,
                        structuredSeries.Value,
                        "series",
                        metadata,
                        identityProvidersToLock,
                        allowLockedIdentityReplacement);
                    await connection.ExecuteAsync(
                        "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                        new { Asset = assetId.ToString("D") }, transaction);
                    await connection.ExecuteAsync(
                        "DELETE FROM catalog_reset_assets WHERE asset_id=@Asset;",
                        new { Asset = assetId.ToString("D") }, transaction);
                    return true;
                }
            }

            var targetNodes = new List<Guid>();
            if (metadata.MediaKind == VideoMetadataMediaKind.Movie)
            {
                var reuseCurrentNode = false;
                Guid nodeId;
                if (currentNodes.Count == 1)
                {
                    reuseCurrentNode = await connection.ExecuteScalarAsync<long>(
                        """
                        SELECT CASE WHEN
                            (SELECT COUNT(*) FROM node_assets WHERE node_id=@Node)=1
                            AND NOT EXISTS(SELECT 1 FROM catalog_nodes WHERE parent_id=@Node)
                            THEN 1 ELSE 0 END;
                        """,
                        new { Node = currentNodes[0] }, transaction) != 0;
                }
                nodeId = reuseCurrentNode
                    ? Guid.Parse(currentNodes[0])
                    : Guid.NewGuid();
                if (!reuseCurrentNode)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO catalog_nodes(id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                        VALUES(@Id,'movie',@Title,0,@Locked,@Now,@Now);
                        """,
                        new
                        {
                            Id = nodeId.ToString("D"),
                            metadata.Title,
                            Locked = allowLockedIdentityReplacement ? 1 : 0,
                            Now = ToDb(DateTimeOffset.UtcNow),
                        }, transaction);
                }
                if (reuseCurrentNode)
                {
                    await connection.ExecuteAsync(
                        "UPDATE catalog_nodes SET parent_id=NULL WHERE id=@Node;",
                        new { Node = nodeId.ToString("D") }, transaction);
                }
                var identityProvidersToLock = await ResolveIdentityProvidersToLockAsync(
                    connection, transaction, nodeId, identityProviderId,
                    lockIdentity, preserveExistingHierarchy);
                await ApplyNodeMetadataAsync(
                    connection, transaction, nodeId, "movie", metadata,
                    identityProvidersToLock, allowLockedIdentityReplacement);
                targetNodes.Add(nodeId);
            }
            else
            {
                var episodeStart = assetRow.episode_start
                                   ?? metadata.EpisodeNumber
                                   ?? metadata.AbsoluteEpisodeNumber;
                Guid? reusableUnmatchedNodeId = null;
                if (currentNodes.Count == 1)
                {
                    var reusable = await connection.ExecuteScalarAsync<string?>(
                        """
                        SELECT n.id FROM catalog_nodes n
                        WHERE n.id=@Node AND n.kind='unmatched'
                          AND (SELECT COUNT(*) FROM node_assets na WHERE na.node_id=n.id)=1;
                        """,
                        new { Node = currentNodes[0] }, transaction);
                    if (Guid.TryParse(reusable, out var reusableId))
                        reusableUnmatchedNodeId = reusableId;
                }
                var seriesId = await FindOrCreateSeriesNodeAsync(
                    connection, transaction, assetId, metadata,
                    identityProviderId, identityProviderItemId,
                    episodeStart.HasValue ? null : reusableUnmatchedNodeId,
                    lockIdentity, preserveExistingHierarchy, allowLockedIdentityReplacement);
                if (preserveExistingHierarchy
                    && metadata.MediaKind is VideoMetadataMediaKind.Episode
                        or VideoMetadataMediaKind.Series
                        or VideoMetadataMediaKind.Anime)
                {
                    var canonicalSeries = await FindCanonicalSeriesNodeAsync(
                        connection,
                        transaction,
                        assetId,
                        seriesId,
                        identityProviderId,
                        identityProviderItemId);
                    if (canonicalSeries.HasValue && canonicalSeries.Value != seriesId
                        && await CanMergeAutomaticSeriesAsync(
                            connection, transaction, seriesId) == 1)
                    {
                        await MergeAutomaticSeriesHierarchyAsync(
                            connection, transaction, seriesId, canonicalSeries.Value);
                        seriesId = canonicalSeries.Value;
                    }
                }
                var currentEpisodeTitle = await connection.ExecuteScalarAsync<string?>(
                    """
                    SELECT n.primary_title FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset AND n.kind='episode'
                    ORDER BY na.ordinal LIMIT 1;
                    """,
                    new { Asset = assetId.ToString("D") }, transaction);
                if (!episodeStart.HasValue)
                {
                    targetNodes.Add(seriesId);
                }
                else
                {
                    Guid? seasonId = null;
                    if (metadata.SeasonNumber.HasValue)
                        seasonId = await FindOrCreateSeasonNodeAsync(connection, transaction, seriesId, metadata.SeasonNumber.Value);
                    var episodeId = await FindOrCreateEpisodeNodeAsync(
                        connection,
                        transaction,
                        seriesId,
                        seasonId,
                        metadata,
                        episodeStart.Value,
                        !string.IsNullOrWhiteSpace(currentEpisodeTitle)
                            ? currentEpisodeTitle
                            : assetRow.episode_end is { } end && end != episodeStart.Value
                                ? $"Episodes {episodeStart.Value}-{end}"
                                : $"Episode {episodeStart.Value}",
                        reusableUnmatchedNodeId,
                        lockIdentity,
                        preserveExistingHierarchy,
                        allowLockedIdentityReplacement,
                        applyMetadata: metadata.MediaKind == VideoMetadataMediaKind.Episode);
                    targetNodes.Add(episodeId);
                }
            }

            await connection.ExecuteAsync(
                "DELETE FROM node_assets WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            var ordinal = 0;
            foreach (var nodeId in targetNodes)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal) VALUES(@Node,@Asset,1,@Ordinal);",
                    new
                    {
                        Node = nodeId.ToString("D"),
                        Asset = assetId.ToString("D"),
                        Ordinal = ordinal++,
                    }, transaction);
            }
            // All detached nodes go through the same provenance-aware pruning path. A former
            // unmatched node can already own local NFO fields, selected local artwork, locked
            // identities, or user data; deleting it here would bypass those protections.
            await PruneEmptyAutomaticNodesAsync(connection, transaction);
            await connection.ExecuteAsync(
                "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            await connection.ExecuteAsync(
                "DELETE FROM catalog_reset_assets WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            return true;
        }, ct);

    public Task ApplyAniDbIdentityAsync(
        Guid assetId,
        VideoAniDbIdentityProjection projection,
        CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projection.GroupId);
            var assetExists = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM media_assets WHERE id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction) != 0;
            if (!assetExists)
                throw new KeyNotFoundException("Video asset was not found.");

            var aid = projection.AnimeId.ToString(CultureInfo.InvariantCulture);
            var seriesId = await FindSeriesNodeByExternalIdAsync(
                connection, transaction, "anidb", aid);
            if (!seriesId.HasValue)
            {
                // Reuse the scanner-owned series for the first confirmed AID when it is
                // still unclaimed. Once an AID is locked, a different AID always gets a
                // distinct series node, even if both files originally shared a folder.
                var currentSeries = await FindBoundAncestorNodeIdAsync(
                    connection, transaction, assetId, "series");
                var hasConflictingAniDbIdentity = currentSeries.HasValue
                    && await connection.ExecuteScalarAsync<long>(
                        """
                        SELECT COUNT(*) FROM external_ids
                        WHERE node_id=@Node AND provider_id='anidb' AND external_id<>@Aid;
                        """,
                        new { Node = currentSeries.Value.ToString("D"), Aid = aid }, transaction) != 0;
                // A scanner series can cover many files before AniDB proves their AIDs.
                // Claiming that shared root for the first matched file would implicitly put
                // every still-unmatched sibling under the matched AID. Reuse is safe only
                // when the entire current hierarchy belongs to this asset alone.
                var hasOtherHierarchyAssets = currentSeries.HasValue
                    && await CountOtherSeriesAssetsAsync(
                        connection, transaction, currentSeries.Value, assetId) != 0;
                seriesId = currentSeries.HasValue
                           && !hasConflictingAniDbIdentity
                           && !hasOtherHierarchyAssets
                    ? currentSeries
                    : Guid.NewGuid();
                if (seriesId != currentSeries)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO catalog_nodes(
                            id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                        VALUES(@Id,'series',@Title,0,1,@Now,@Now);
                        """,
                        new
                        {
                            Id = seriesId.Value.ToString("D"),
                            Title = projection.SeriesMetadata.Title,
                            Now = ToDb(DateTimeOffset.UtcNow),
                        }, transaction);
                }
            }

            var seriesMetadata = projection.SeriesMetadata.WithInitializedCollections() with
            {
                ProviderId = "anidb",
                ProviderItemId = aid,
                MediaKind = VideoMetadataMediaKind.Anime,
                SeasonNumber = null,
                EpisodeNumber = null,
                AbsoluteEpisodeNumber = null,
                ExternalIds = projection.SeriesMetadata.ExternalIds
                    .Remove("anidb-file")
                    .Remove("anidb-episode")
                    .SetItem("anidb", aid)
                    .SetItem("anidb-group", projection.GroupId),
            };
            await ApplyNodeMetadataAsync(
                connection,
                transaction,
                seriesId.Value,
                "series",
                seriesMetadata,
                ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "anidb"),
                allowLockedIdentityReplacement: false);
            var seriesByAnime = new Dictionary<int, (Guid SeriesId, string GroupId, VideoMetadataDetails Metadata)>
            {
                [projection.AnimeId] = (seriesId.Value, projection.GroupId, seriesMetadata),
            };

            var oldEpisodeIds = (await connection.QueryAsync<string>(
                """
                SELECT n.id FROM catalog_nodes n
                JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset AND n.kind='episode'
                ORDER BY na.ordinal,n.id;
                """,
                new { Asset = assetId.ToString("D") }, transaction)).ToList();
            var targetEpisodeIds = new List<Guid>();
            foreach (var episode in projection.Episodes
                         .OrderBy(item => item.Ordinal)
                         .ThenBy(item => item.EpisodeId))
            {
                var episodeAnimeId = episode.AnimeId > 0 ? episode.AnimeId : projection.AnimeId;
                if (!seriesByAnime.TryGetValue(episodeAnimeId, out var episodeOwner))
                {
                    if (episode.AnimeMetadata == null
                        || string.IsNullOrWhiteSpace(episode.AnimeGroupId))
                        throw new InvalidOperationException(
                            $"AniDB episode {episode.EpisodeId} has no metadata for owning AID {episodeAnimeId}.");
                    var episodeAid = episodeAnimeId.ToString(CultureInfo.InvariantCulture);
                    var ownerSeriesId = await FindSeriesNodeByExternalIdAsync(
                        connection, transaction, "anidb", episodeAid);
                    if (!ownerSeriesId.HasValue)
                    {
                        ownerSeriesId = Guid.NewGuid();
                        await connection.ExecuteAsync(
                            """
                            INSERT INTO catalog_nodes(
                                id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                            VALUES(@Id,'series',@Title,0,1,@Now,@Now);
                            """,
                            new
                            {
                                Id = ownerSeriesId.Value.ToString("D"),
                                episode.AnimeMetadata.Title,
                                Now = ToDb(DateTimeOffset.UtcNow),
                            }, transaction);
                    }
                    var ownerMetadata = episode.AnimeMetadata.WithInitializedCollections() with
                    {
                        ProviderId = "anidb",
                        ProviderItemId = episodeAid,
                        MediaKind = VideoMetadataMediaKind.Anime,
                        SeasonNumber = null,
                        EpisodeNumber = null,
                        AbsoluteEpisodeNumber = null,
                        ExternalIds = episode.AnimeMetadata.ExternalIds
                            .Remove("anidb-file")
                            .Remove("anidb-episode")
                            .SetItem("anidb", episodeAid)
                            .SetItem("anidb-group", episode.AnimeGroupId),
                    };
                    await ApplyNodeMetadataAsync(
                        connection,
                        transaction,
                        ownerSeriesId.Value,
                        "series",
                        ownerMetadata,
                        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "anidb"),
                        allowLockedIdentityReplacement: false);
                    episodeOwner = (ownerSeriesId.Value, episode.AnimeGroupId, ownerMetadata);
                    seriesByAnime.Add(episodeAnimeId, episodeOwner);
                }

                var eid = episode.EpisodeId.ToString(CultureInfo.InvariantCulture);
                var existingEpisode = await connection.ExecuteScalarAsync<string?>(
                    """
                    SELECT e.node_id FROM external_ids e
                    JOIN catalog_nodes n ON n.id=e.node_id
                    WHERE n.kind='episode' AND e.provider_id='anidb-episode'
                      AND e.external_id=@Eid
                    LIMIT 1;
                    """,
                    new { Eid = eid }, transaction);
                Guid episodeId;
                if (Guid.TryParse(existingEpisode, out var parsedEpisodeId))
                {
                    episodeId = parsedEpisodeId;
                }
                else
                {
                    Guid? reusableEpisodeId = null;
                    if (targetEpisodeIds.Count == 0 && oldEpisodeIds.Count == 1)
                    {
                        var candidateId = Guid.Parse(oldEpisodeIds[0]);
                        var hasLockedDifferentEid = await connection.ExecuteScalarAsync<long>(
                            """
                            SELECT COUNT(*) FROM external_ids
                            WHERE node_id=@Node AND provider_id='anidb-episode'
                              AND external_id<>@Eid;
                            """,
                            new { Node = candidateId.ToString("D"), Eid = eid }, transaction) != 0;
                        if (!hasLockedDifferentEid)
                            reusableEpisodeId = candidateId;
                    }

                    episodeId = reusableEpisodeId ?? Guid.NewGuid();
                    if (!reusableEpisodeId.HasValue)
                    {
                        await connection.ExecuteAsync(
                            """
                            INSERT INTO catalog_nodes(
                                id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                            VALUES(@Id,'episode',@Title,@Special,1,@Now,@Now);
                            """,
                            new
                            {
                                Id = episodeId.ToString("D"),
                                episode.Title,
                                Special = episode.SeasonNumber == 0 ? 1 : 0,
                                Now = ToDb(DateTimeOffset.UtcNow),
                            }, transaction);
                    }
                }

                var seasonId = await FindOrCreateSeasonNodeAsync(
                    connection, transaction, episodeOwner.SeriesId, episode.SeasonNumber);
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_nodes SET parent_id=@Parent,kind='episode',season_number=@Season,
                        episode_number=@Episode,is_special=@Special,updated_at=@Now
                    WHERE id=@Id;
                    """,
                    new
                    {
                        Id = episodeId.ToString("D"),
                        Parent = seasonId.ToString("D"),
                        Season = episode.SeasonNumber,
                        Episode = episode.EpisodeNumber,
                        Special = episode.SeasonNumber == 0 ? 1 : 0,
                        Now = ToDb(DateTimeOffset.UtcNow),
                    }, transaction);

                var episodeMetadata = new VideoMetadataDetails(
                    "anidb-episode",
                    eid,
                    VideoMetadataMediaKind.Episode,
                    episode.Title,
                    episode.OriginalTitle,
                    null,
                    episode.Overview,
                    episode.AirDate?.Year ?? episodeOwner.Metadata.Year,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    episode.SeasonNumber == 1 ? episode.EpisodeNumber : null,
                    [],
                    [],
                    [],
                    ImmutableDictionary<string, string>.Empty
                        .Add("anidb", episodeAnimeId.ToString(CultureInfo.InvariantCulture))
                        .Add("anidb-episode", eid)
                        .Add("anidb-group", episodeOwner.GroupId),
                    $"https://anidb.net/episode/{eid}",
                    episodeOwner.Metadata.FetchedAt,
                    episodeOwner.Metadata.ExpiresAt).WithInitializedCollections();
                await ApplyNodeMetadataAsync(
                    connection,
                    transaction,
                    episodeId,
                    "episode",
                    episodeMetadata,
                    ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "anidb-episode"),
                    allowLockedIdentityReplacement: false);
                targetEpisodeIds.Add(episodeId);
            }

            await connection.ExecuteAsync(
                "DELETE FROM node_assets WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            if (targetEpisodeIds.Count == 0)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                    VALUES(@Node,@Asset,1,0);
                    """,
                    new { Node = seriesId.Value.ToString("D"), Asset = assetId.ToString("D") }, transaction);
            }
            else
            {
                for (var index = 0; index < targetEpisodeIds.Count; index++)
                {
                    await connection.ExecuteAsync(
                        """
                        INSERT OR IGNORE INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                        VALUES(@Node,@Asset,@Preferred,@Ordinal);
                        """,
                        new
                        {
                            Node = targetEpisodeIds[index].ToString("D"),
                            Asset = assetId.ToString("D"),
                            Preferred = index == 0 ? 1 : 0,
                            Ordinal = index,
                        }, transaction);
                }
            }

            await connection.ExecuteAsync(
                "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            await connection.ExecuteAsync(
                "DELETE FROM catalog_reset_assets WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
            await PruneEmptyAutomaticNodesAsync(connection, transaction);
        }, ct);

    public Task UpdateMetadataRefreshCountsAsync(
        Guid jobId,
        int matchedCount,
        int needsReviewCount,
        CancellationToken ct = default,
        int failedCount = 0) =>
        WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(
                "UPDATE catalog_jobs SET matched_count=@Matched, needs_review_count=@NeedsReview, failed_count=@Failed, updated_at=@Now WHERE id=@Id AND kind='metadata_refresh';",
                new
                {
                    Id = jobId.ToString("D"),
                    Matched = Math.Max(0, matchedCount),
                    NeedsReview = Math.Max(0, needsReviewCount),
                    Failed = Math.Max(0, failedCount),
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }, ct);

    public Task ClearRemoteMetadataAsync(Guid sourceId, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            // Re-scraping is allowed to rebuild provider metadata, but must not touch source
            // media, playback/user data, local fields, or locked identities. Include ancestors
            // because series/season metadata can be owned by a parent of the source asset node.
            await connection.ExecuteAsync(
                """
                CREATE TEMP TABLE IF NOT EXISTS clear_remote_metadata_nodes(id TEXT PRIMARY KEY);
                DELETE FROM clear_remote_metadata_nodes;
                WITH RECURSIVE bound(id) AS (
                    SELECT DISTINCT na.node_id
                    FROM node_assets na
                    JOIN source_assets sa ON sa.asset_id=na.asset_id
                    WHERE sa.source_id=@Source
                    UNION
                    SELECT parent.parent_id
                    FROM catalog_nodes parent
                    JOIN bound child ON child.id=parent.id
                    WHERE parent.parent_id IS NOT NULL
                )
                INSERT OR IGNORE INTO clear_remote_metadata_nodes(id)
                SELECT id FROM bound;

                DELETE FROM metadata_snapshots
                WHERE node_id IN (SELECT id FROM clear_remote_metadata_nodes);
                DELETE FROM metadata_field_values
                WHERE node_id IN (SELECT id FROM clear_remote_metadata_nodes)
                  AND provider_id<>'local' AND is_locked=0;
                DELETE FROM artwork
                WHERE node_id IN (SELECT id FROM clear_remote_metadata_nodes)
                  AND provider_id<>'local';
                DELETE FROM external_ids
                WHERE node_id IN (SELECT id FROM clear_remote_metadata_nodes)
                  AND is_identity_locked=0;
                DELETE FROM catalog_aliases
                WHERE node_id IN (SELECT id FROM clear_remote_metadata_nodes)
                  AND provider_id NOT IN ('filename','local');
                DELETE FROM match_candidates
                WHERE asset_id IN (
                    SELECT asset_id FROM source_assets WHERE source_id=@Source
                );
                DELETE FROM catalog_reset_assets
                WHERE asset_id IN (
                    SELECT asset_id FROM source_assets WHERE source_id=@Source
                );
                DELETE FROM catalog_reset_sources WHERE source_id=@Source;
                DROP TABLE clear_remote_metadata_nodes;
                """,
                new { Source = sourceId.ToString("D") }, transaction);
        }, ct);

    public Task ClearAllScrapeRecordsAsync(CancellationToken ct = default) =>
        ClearAllScrapeRecordsAsync(Array.Empty<VideoManualAniDbIdentity>(), ct);

    public Task ClearAllScrapeRecordsAsync(
        IReadOnlyCollection<VideoManualAniDbIdentity> manualAniDbIdentities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manualAniDbIdentities);
        return WriteAsync(
            (connection, transaction) =>
                ResetCatalogToUnmatchedAfterScrapeClearAsync(connection, transaction),
            ct);
    }

    private Task ClearAllScrapeRecordsLegacyAsync(
        IReadOnlyCollection<VideoManualAniDbIdentity> manualAniDbIdentities,
        CancellationToken ct) =>
        WriteAsync(async (connection, transaction) =>
        {
            // This is an explicit, user-confirmed reset of every catalog projection.
            // Source media, source membership, asset user data, playback, collections,
            // tags and credentials live outside this delete set. Local sidecars remain
            // untouched on disk and can be imported again by a later scan.
            await connection.ExecuteAsync(
                """
                CREATE TEMP TABLE clear_manual_anidb_identities(
                    asset_id TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    external_id TEXT NOT NULL,
                    PRIMARY KEY(asset_id,provider_id,external_id));
                CREATE TEMP TABLE clear_manual_anidb_external_ids(
                    node_id TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    external_id TEXT NOT NULL,
                    PRIMARY KEY(node_id,provider_id,external_id));
                CREATE TEMP TABLE clear_removed_automatic_anidb_lock_nodes(
                    id TEXT PRIMARY KEY);
                """,
                transaction: transaction);
            var manualIdentityRows = manualAniDbIdentities
                .SelectMany(identity => identity.AnimeIds
                    .Select(animeId => new
                    {
                        Asset = identity.AssetId.ToString("D"),
                        Provider = "anidb",
                        ExternalId = animeId.ToString(CultureInfo.InvariantCulture),
                    })
                    .Concat(identity.EpisodeIds.Select(episodeId => new
                    {
                        Asset = identity.AssetId.ToString("D"),
                        Provider = "anidb-episode",
                        ExternalId = episodeId.ToString(CultureInfo.InvariantCulture),
                    })))
                .ToArray();
            if (manualIdentityRows.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT OR IGNORE INTO clear_manual_anidb_identities(
                        asset_id,provider_id,external_id)
                    VALUES(@Asset,@Provider,@ExternalId);
                    """,
                    manualIdentityRows, transaction);
                await connection.ExecuteAsync(
                    """
                    WITH RECURSIVE manual_ancestry(asset_id,id,parent_id) AS (
                        SELECT identity.asset_id,node.id,node.parent_id
                        FROM (SELECT DISTINCT asset_id FROM clear_manual_anidb_identities) identity
                        JOIN node_assets binding ON binding.asset_id=identity.asset_id
                        JOIN catalog_nodes node ON node.id=binding.node_id
                        UNION
                        SELECT child.asset_id,parent.id,parent.parent_id
                        FROM catalog_nodes parent
                        JOIN manual_ancestry child ON child.parent_id=parent.id
                    )
                    INSERT OR IGNORE INTO clear_manual_anidb_external_ids(
                        node_id,provider_id,external_id)
                    SELECT DISTINCT ancestry.id,lower(external.provider_id),external.external_id
                    FROM manual_ancestry ancestry
                    JOIN external_ids external ON external.node_id=ancestry.id
                    JOIN clear_manual_anidb_identities identity
                      ON identity.asset_id=ancestry.asset_id
                     AND identity.provider_id=lower(external.provider_id)
                     AND identity.external_id=external.external_id;
                    """,
                    transaction: transaction);
            }
            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO clear_removed_automatic_anidb_lock_nodes(id)
                SELECT DISTINCT external.node_id
                FROM external_ids external
                WHERE external.provider_id IN ('anidb','anidb-episode')
                  AND external.is_identity_locked=1
                  AND NOT EXISTS(
                      SELECT 1 FROM clear_manual_anidb_external_ids manual
                      WHERE manual.node_id=external.node_id
                        AND manual.provider_id=lower(external.provider_id)
                        AND manual.external_id=external.external_id);
                """,
                transaction: transaction);
            var localFields = (await connection.QueryAsync<SnapshotLocalFieldRow>(
                    """
                    SELECT node_id,field,value FROM metadata_field_values
                    WHERE provider_id='local' AND value IS NOT NULL AND TRIM(value)<>'';
                    """,
                    transaction: transaction))
                .GroupBy(row => row.node_id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToDictionary(
                        row => row.field,
                        row => row.value,
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            var preservedFields = (await connection.QueryAsync<PreservedFieldRow>(
                    """
                    SELECT node_id,field,value,provider_id,priority,is_locked,updated_at
                    FROM metadata_field_values
                    WHERE (provider_id='local' OR is_locked=1)
                      AND value IS NOT NULL AND TRIM(value)<>''
                    ORDER BY is_locked DESC,priority DESC,updated_at DESC,provider_id;
                    """,
                    transaction: transaction))
                .GroupBy(row => row.node_id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .GroupBy(row => row.field, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            fields => fields.Key,
                            fields => fields.First().value,
                            StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            await connection.ExecuteAsync(
                """
                UPDATE node_user_data
                SET preferred_artwork_id=NULL,updated_at=@Now
                WHERE preferred_artwork_id IN(
                    SELECT id FROM artwork WHERE provider_id<>'local');
                DELETE FROM node_user_data
                WHERE is_favorite=0 AND preferred_asset_id IS NULL
                  AND preferred_artwork_id IS NULL;

                DELETE FROM metadata_snapshots;
                DELETE FROM metadata_field_values
                WHERE provider_id<>'local' AND is_locked=0;
                DELETE FROM artwork WHERE provider_id<>'local';
                DELETE FROM external_ids
                WHERE provider_id IN ('anidb','anidb-episode')
                  AND NOT EXISTS(
                      SELECT 1 FROM clear_manual_anidb_external_ids manual
                      WHERE manual.node_id=external_ids.node_id
                        AND manual.provider_id=lower(external_ids.provider_id)
                        AND manual.external_id=external_ids.external_id);
                DELETE FROM external_ids WHERE is_identity_locked=0;
                DELETE FROM catalog_aliases WHERE provider_id NOT IN ('filename','local');
                DELETE FROM match_candidates;
                DELETE FROM tmdb_episode_xrefs;
                DELETE FROM tmdb_show_xrefs;
                DELETE FROM tmdb_orderings;
                DELETE FROM provider_cache;
                DELETE FROM catalog_jobs WHERE kind='metadata_refresh';
                """,
                new { Now = ToDb(DateTimeOffset.UtcNow) },
                transaction);

            // Snapshot only locks that survived the automatic scrape deletion.
            // A Local sidecar reusing an AniDB provider must not inherit the lock
            // from the automatic AID/EID row that was just removed.
            var externalIdentityLocks = (await connection.QueryAsync<ExternalIdRow>(
                    """
                    SELECT node_id,provider_id,external_id,is_identity_locked
                    FROM external_ids;
                    """,
                    transaction: transaction))
                .ToDictionary(
                    row => $"{row.node_id}\0{row.provider_id}",
                    row => row.is_identity_locked,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in localFields)
            {
                foreach (var externalId in DeserializeLocalExternalIds(
                             pair.Value.GetValueOrDefault("externalIds")))
                {
                    var providerId = externalId.Key.ToLowerInvariant();
                    var identityWasLocked = externalIdentityLocks.GetValueOrDefault(
                        $"{pair.Key}\0{providerId}");
                    await connection.ExecuteAsync(
                        """
                        INSERT INTO external_ids(
                            node_id,provider_id,external_id,is_identity_locked)
                        VALUES(@Node,@Provider,@ExternalId,@Locked)
                        ON CONFLICT(node_id,provider_id) DO UPDATE SET
                            external_id=CASE WHEN external_ids.is_identity_locked=1
                                THEN external_ids.external_id ELSE excluded.external_id END,
                            is_identity_locked=MAX(external_ids.is_identity_locked,excluded.is_identity_locked);
                        """,
                        new
                        {
                            Node = pair.Key,
                            Provider = providerId,
                            ExternalId = externalId.Value,
                            Locked = identityWasLocked,
                        },
                        transaction);
                }
            }

            await connection.ExecuteAsync(
                """
                UPDATE catalog_nodes
                SET identity_locked=CASE WHEN
                    EXISTS(SELECT 1 FROM external_ids external
                           WHERE external.node_id=catalog_nodes.id
                             AND external.is_identity_locked=1)
                    OR EXISTS(SELECT 1 FROM metadata_field_values field
                              WHERE field.node_id=catalog_nodes.id AND field.is_locked=1)
                    THEN 1 ELSE 0 END
                WHERE id IN (SELECT id FROM clear_removed_automatic_anidb_lock_nodes);
                """,
                transaction: transaction);

            var nodes = (await connection.QueryAsync<NodeRow>(
                "SELECT * FROM catalog_nodes ORDER BY created_at,id;",
                transaction: transaction)).ToArray();
            var filenameAliases = (await connection.QueryAsync<AliasRow>(
                    """
                    SELECT * FROM catalog_aliases
                    WHERE provider_id='filename' ORDER BY alias;
                    """,
                    transaction: transaction))
                .GroupBy(row => row.node_id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => row.alias).FirstOrDefault(),
                    StringComparer.OrdinalIgnoreCase);
            var now = ToDb(DateTimeOffset.UtcNow);
            foreach (var node in nodes)
            {
                preservedFields.TryGetValue(node.id, out var fields);
                filenameAliases.TryGetValue(node.id, out var filenameTitle);
                var assetTitle = await connection.ExecuteScalarAsync<string?>(
                    """
                    WITH RECURSIVE descendants(id) AS (
                        SELECT @Node
                        UNION
                        SELECT child.id FROM catalog_nodes child
                        JOIN descendants parent ON child.parent_id=parent.id
                    )
                    SELECT asset.title FROM descendants node
                    JOIN node_assets binding ON binding.node_id=node.id
                    JOIN media_assets asset ON asset.id=binding.asset_id
                    ORDER BY binding.is_preferred DESC,binding.ordinal,asset.imported_at
                    LIMIT 1;
                    """,
                    new { Node = node.id },
                    transaction);
                var structuralTitle = node.kind switch
                {
                    "season" when node.season_number == 0 => "Specials",
                    "season" when node.season_number.HasValue => $"Season {node.season_number.Value}",
                    "episode" when node.episode_number.HasValue => $"Episode {node.episode_number.Value}",
                    _ => null,
                };
                var title = fields?.GetValueOrDefault("title")
                            ?? filenameTitle
                            ?? structuralTitle
                            ?? assetTitle
                            ?? node.primary_title;
                var year = int.TryParse(
                    fields?.GetValueOrDefault("year"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedYear)
                    ? parsedYear
                    : (int?)null;
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_nodes
                    SET primary_title=@Title,original_title=@OriginalTitle,
                        subtitle=@Subtitle,overview=@Overview,year=@Year,updated_at=@Now
                    WHERE id=@Node;
                    """,
                    new
                    {
                        Node = node.id,
                        Title = title,
                        OriginalTitle = fields?.GetValueOrDefault("originalTitle"),
                        Subtitle = fields?.GetValueOrDefault("subtitle"),
                        Overview = fields?.GetValueOrDefault("overview"),
                        Year = year,
                        Now = now,
                    },
                    transaction);
            }

            await ResetCatalogToUnmatchedAfterScrapeClearAsync(connection, transaction);
            await connection.ExecuteAsync(
                """
                DROP TABLE clear_removed_automatic_anidb_lock_nodes;
                DROP TABLE clear_manual_anidb_external_ids;
                DROP TABLE clear_manual_anidb_identities;
                """,
                transaction: transaction);
        }, ct);

    private static async Task ResetCatalogToUnmatchedAfterScrapeClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            CREATE TEMP TABLE clear_reset_asset_ancestry(
                asset_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                depth INTEGER NOT NULL,
                PRIMARY KEY(asset_id,node_id));
            WITH RECURSIVE ancestry(asset_id,node_id,parent_id,depth) AS (
                SELECT binding.asset_id,node.id,node.parent_id,0
                FROM node_assets binding
                JOIN catalog_nodes node ON node.id=binding.node_id
                UNION ALL
                SELECT child.asset_id,parent.id,parent.parent_id,child.depth+1
                FROM ancestry child
                JOIN catalog_nodes parent ON parent.id=child.parent_id
            )
            INSERT OR IGNORE INTO clear_reset_asset_ancestry(asset_id,node_id,depth)
            SELECT asset_id,node_id,depth FROM ancestry;

            CREATE TEMP TABLE clear_reset_favorite_assets(
                asset_id TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO clear_reset_favorite_assets(asset_id)
            SELECT DISTINCT ancestry.asset_id
            FROM clear_reset_asset_ancestry ancestry
            JOIN node_user_data user ON user.node_id=ancestry.node_id
            WHERE user.is_favorite=1;

            INSERT OR IGNORE INTO asset_user_data(asset_id,is_favorite,updated_at)
            SELECT asset_id,1,@Now FROM clear_reset_favorite_assets;
            UPDATE asset_user_data
            SET is_favorite=1,updated_at=@Now
            WHERE asset_id IN (SELECT asset_id FROM clear_reset_favorite_assets);

            UPDATE library_sources SET scan_generation=scan_generation+1;
            UPDATE catalog_jobs
            SET state='cancelled',updated_at=@Now,
                error='Superseded by scrape catalog reset.'
            WHERE kind IN ('incremental_scan','full_scan')
              AND state IN ('queued','running','paused');

            DELETE FROM catalog_nodes;
            DELETE FROM match_candidates;
            DELETE FROM tmdb_episode_xrefs;
            DELETE FROM tmdb_orderings;
            DELETE FROM tmdb_show_xrefs;
            DELETE FROM provider_cache;
            DELETE FROM catalog_jobs WHERE kind='metadata_refresh';
            DELETE FROM catalog_reset_assets;
            INSERT INTO catalog_reset_assets(asset_id,reset_at)
            SELECT id,@Now FROM media_assets;
            DELETE FROM catalog_reset_sources;
            INSERT INTO catalog_reset_sources(source_id,reset_at)
            SELECT id,@Now FROM library_sources;

            CREATE TEMP TABLE clear_reset_nodes(
                asset_id TEXT PRIMARY KEY,
                node_id TEXT NOT NULL UNIQUE);
            """,
            new { Now = now },
            transaction);

        var assets = (await connection.QueryAsync<ScrapeResetAssetRow>(
            """
            SELECT asset.id AS asset_id,
                   COALESCE(NULLIF(TRIM(user.display_title),''),asset.title) AS title
            FROM media_assets asset
            LEFT JOIN asset_user_data user ON user.asset_id=asset.id
            ORDER BY asset.id;
            """,
            transaction: transaction)).ToArray();
        var resetNodes = assets.Select(asset => new
        {
            Asset = asset.asset_id,
            Node = Guid.NewGuid().ToString("D"),
            Title = asset.title,
            Now = now,
        }).ToArray();
        if (resetNodes.Length > 0)
        {
            foreach (var resetNode in resetNodes)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO catalog_nodes(
                        id,parent_id,kind,primary_title,original_title,subtitle,overview,year,
                        season_number,episode_number,absolute_episode_number,is_special,
                        identity_locked,created_at,updated_at)
                    VALUES(@Node,NULL,'unmatched',@Title,NULL,NULL,NULL,NULL,
                           NULL,NULL,NULL,0,0,@Now,@Now);
                    INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                    VALUES(@Node,@Asset,1,0);
                    INSERT INTO clear_reset_nodes(asset_id,node_id)
                    VALUES(@Asset,@Node);
                    """,
                    resetNode,
                    transaction);
            }
        }

        await connection.ExecuteAsync(
            """
            DROP TABLE clear_reset_nodes;
            DROP TABLE clear_reset_favorite_assets;
            DROP TABLE clear_reset_asset_ancestry;
            """,
            new { Now = now },
            transaction);
    }

    private static async Task RebuildScannerTopologyAfterScrapeClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var memberships = (await connection.QueryAsync<ScrapeResetAssetRow>(
                """
                SELECT asset.id AS asset_id,asset.kind,asset.location,asset.title,
                       source.id AS source_id,source.folder_path,source.media_type
                FROM media_assets asset
                LEFT JOIN source_assets membership ON membership.asset_id=asset.id
                LEFT JOIN library_sources source ON source.id=membership.source_id
                ORDER BY asset.id,LENGTH(COALESCE(source.normalized_folder_path,'')) DESC;
                """,
                transaction: transaction))
            .GroupBy(row => row.asset_id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var parser = new VideoFileNameParser();
        var parsedByAsset = new Dictionary<string, ParsedVideoIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceGroup in memberships.Where(row =>
                     string.Equals(row.kind, "local", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(row.source_id)
                     && !string.IsNullOrWhiteSpace(row.folder_path))
                 .GroupBy(row => row.source_id!, StringComparer.OrdinalIgnoreCase))
        {
            var source = sourceGroup.First();
            var paths = sourceGroup.Select(row => Path.GetFullPath(row.location)).ToArray();
            var parsed = VideoScanBundleClassifier.Parse(
                paths,
                source.folder_path!,
                ParseMediaType(source.media_type ?? "auto"),
                parser);
            foreach (var row in sourceGroup)
            {
                if (parsed.TryGetValue(Path.GetFullPath(row.location), out var identity))
                    parsedByAsset[row.asset_id] = identity;
            }
        }

        foreach (var row in memberships)
        {
            var assetId = Guid.Parse(row.asset_id);
            if (parsedByAsset.TryGetValue(row.asset_id, out var parsed)
                && Guid.TryParse(row.source_id, out var sourceId))
            {
                await RebuildLocalAssetTopologyAfterScrapeClearAsync(
                    connection, transaction, assetId, sourceId, parsed);
            }
            else
            {
                await ResetAssetToUnmatchedAfterScrapeClearAsync(
                    connection, transaction, assetId, row.title);
            }
        }
    }

    private static async Task RebuildLocalAssetTopologyAfterScrapeClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        Guid sourceId,
        ParsedVideoIdentity parsed)
    {
        var currentNodes = (await connection.QueryAsync<BoundNodeRow>(
            """
            SELECT node.id,node.kind FROM catalog_nodes node
            JOIN node_assets binding ON binding.node_id=node.id
            WHERE binding.asset_id=@Asset ORDER BY binding.ordinal,node.id;
            """,
            new { Asset = assetId.ToString("D") }, transaction)).ToList();
        if (currentNodes.Count == 0)
            return;

        parsed = await OverlayPersistedLocalStructureAsync(
            connection, transaction, assetId, parsed);

        // A manually/explicitly locked child is authoritative. Automatic TMDB scaffolds may be
        // rebuilt around a protected series, but an EID/movie/season lock must keep its binding.
        var hasLockedChild = await connection.ExecuteScalarAsync<long>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind,identity_locked) AS (
                SELECT node.id,node.parent_id,node.kind,node.identity_locked
                FROM catalog_nodes node
                JOIN node_assets binding ON binding.node_id=node.id
                WHERE binding.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id,parent.kind,parent.identity_locked
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT COUNT(*) FROM ancestry node
            WHERE node.kind<>'series'
              AND (node.identity_locked=1
                   OR EXISTS(SELECT 1 FROM external_ids external
                             WHERE external.node_id=node.id AND external.is_identity_locked=1)
                   OR EXISTS(SELECT 1 FROM metadata_field_values field
                             WHERE field.node_id=node.id AND field.is_locked=1));
            """,
            new { Asset = assetId.ToString("D") }, transaction) != 0;
        if (hasLockedChild)
            return;

        var preservedSeries = await connection.ExecuteScalarAsync<string?>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                SELECT node.id,node.parent_id,node.kind
                FROM catalog_nodes node
                JOIN node_assets binding ON binding.node_id=node.id
                WHERE binding.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id,parent.kind
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT series.id FROM ancestry current
            JOIN catalog_nodes series ON series.id=current.id AND series.kind='series'
            WHERE series.identity_locked=1
               OR EXISTS(SELECT 1 FROM external_ids external
                         WHERE external.node_id=series.id AND external.is_identity_locked=1)
               OR EXISTS(SELECT 1 FROM metadata_field_values field
                         WHERE field.node_id=series.id
                           AND (field.provider_id='local' OR field.is_locked=1))
               OR EXISTS(SELECT 1 FROM artwork art
                         WHERE art.node_id=series.id AND art.provider_id='local')
               OR EXISTS(SELECT 1 FROM node_user_data user WHERE user.node_id=series.id)
               OR EXISTS(SELECT 1 FROM catalog_aliases alias
                         WHERE alias.node_id=series.id AND alias.provider_id IN ('filename','local'))
            ORDER BY series.identity_locked DESC,
                     EXISTS(SELECT 1 FROM external_ids external
                            WHERE external.node_id=series.id AND external.is_identity_locked=1) DESC,
                     EXISTS(SELECT 1 FROM metadata_field_values field
                            WHERE field.node_id=series.id AND field.provider_id='local') DESC,
                     EXISTS(SELECT 1 FROM catalog_aliases alias
                            WHERE alias.node_id=series.id AND alias.provider_id='filename') DESC
            LIMIT 1;
            """,
            new { Asset = assetId.ToString("D") }, transaction);
        var preservedSeriesId = Guid.TryParse(preservedSeries, out var parsedSeriesId)
            ? parsedSeriesId
            : (Guid?)null;
        var episodeStart = parsed.EpisodeStart ?? parsed.AbsoluteEpisodeNumber;
        var isUnnumberedSupplemental = parsed.SpecialKind != ParsedVideoSpecialKind.None
                                       && !episodeStart.HasValue;
        if (!episodeStart.HasValue && !isUnnumberedSupplemental)
        {
            if (preservedSeriesId.HasValue
                && await IsNodeIdentityLockedAsync(connection, transaction, preservedSeriesId.Value))
                return;
            await ResetAssetToUnmatchedAfterScrapeClearAsync(
                connection, transaction, assetId, parsed.NormalizedTitle);
            return;
        }

        var reusableEpisode = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT episode.id FROM catalog_nodes episode
            JOIN node_assets binding ON binding.node_id=episode.id
            WHERE binding.asset_id=@Asset AND episode.kind='episode'
              AND (EXISTS(SELECT 1 FROM metadata_field_values field
                          WHERE field.node_id=episode.id AND field.provider_id='local')
                   OR EXISTS(SELECT 1 FROM artwork art
                             WHERE art.node_id=episode.id AND art.provider_id='local')
                   OR EXISTS(SELECT 1 FROM node_user_data user WHERE user.node_id=episode.id)
                   OR EXISTS(SELECT 1 FROM catalog_aliases alias
                             WHERE alias.node_id=episode.id
                               AND alias.provider_id IN ('filename','local')))
            ORDER BY binding.is_preferred DESC,binding.ordinal,episode.created_at
            LIMIT 1;
            """,
            new { Asset = assetId.ToString("D") }, transaction);
        Guid? reusableEpisodeId = Guid.TryParse(reusableEpisode, out var parsedEpisodeId)
            ? parsedEpisodeId
            : null;
        if (reusableEpisodeId.HasValue)
        {
            preservedSeriesId ??= await FindOrCreateScannerSeriesForResetAsync(
                connection, transaction, sourceId, parsed);
            var seasonNumber = parsed.SpecialKind != ParsedVideoSpecialKind.None
                ? 0
                : parsed.SeasonNumber;
            Guid? seasonId = null;
            if (seasonNumber.HasValue)
            {
                var reusableSeason = await connection.ExecuteScalarAsync<string?>(
                    """
                    WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                        SELECT node.id,node.parent_id,node.kind
                        FROM catalog_nodes node
                        JOIN node_assets binding ON binding.node_id=node.id
                        WHERE binding.asset_id=@Asset
                        UNION
                        SELECT parent.id,parent.parent_id,parent.kind
                        FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
                    )
                    SELECT season.id FROM ancestry current
                    JOIN catalog_nodes season ON season.id=current.id AND season.kind='season'
                    WHERE EXISTS(SELECT 1 FROM metadata_field_values field
                                 WHERE field.node_id=season.id AND field.provider_id='local')
                       OR EXISTS(SELECT 1 FROM artwork art
                                 WHERE art.node_id=season.id AND art.provider_id='local')
                       OR EXISTS(SELECT 1 FROM node_user_data user WHERE user.node_id=season.id)
                    LIMIT 1;
                    """,
                    new { Asset = assetId.ToString("D") }, transaction);
                if (Guid.TryParse(reusableSeason, out var parsedSeasonId))
                {
                    seasonId = parsedSeasonId;
                    await connection.ExecuteAsync(
                        """
                        UPDATE catalog_nodes SET parent_id=@Parent,season_number=@Season,
                            is_special=@Special,updated_at=@Now WHERE id=@Node;
                        """,
                        new
                        {
                            Node = parsedSeasonId.ToString("D"),
                            Parent = preservedSeriesId.Value.ToString("D"),
                            Season = seasonNumber.Value,
                            Special = seasonNumber.Value == 0 ? 1 : 0,
                            Now = ToDb(DateTimeOffset.UtcNow),
                        }, transaction);
                }
                seasonId ??= await FindOrCreateSeasonNodeAsync(
                    connection, transaction, preservedSeriesId.Value, seasonNumber.Value);
            }
            var parentId = seasonId ?? preservedSeriesId.Value;
            await connection.ExecuteAsync(
                """
                UPDATE catalog_nodes SET parent_id=@Parent,kind='episode',
                    season_number=@Season,episode_number=@Episode,
                    absolute_episode_number=@Absolute,is_special=@Special,updated_at=@Now
                WHERE id=@Node;
                """,
                new
                {
                    Node = reusableEpisodeId.Value.ToString("D"),
                    Parent = parentId.ToString("D"),
                    Season = seasonNumber,
                    Episode = episodeStart,
                    Absolute = parsed.AbsoluteEpisodeNumber,
                    Special = parsed.SpecialKind == ParsedVideoSpecialKind.None ? 0 : 1,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }
        else
        {
            var unmatchedId = Guid.NewGuid();
            await connection.ExecuteAsync(
                """
                DELETE FROM node_assets WHERE asset_id=@Asset;
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES(@Node,NULL,'unmatched',@Title,0,0,@Now,@Now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES(@Node,@Asset,1,0);
                """,
                new
                {
                    Node = unmatchedId.ToString("D"),
                    Asset = assetId.ToString("D"),
                    Title = parsed.NormalizedTitle,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
            currentNodes = [new BoundNodeRow { id = unmatchedId.ToString("D"), kind = "unmatched" }];
        }

        await PromoteLocalEpisodeHierarchyAsync(
            connection,
            transaction,
            assetId,
            sourceId,
            currentNodes,
            parsed,
            null,
            episodeStart,
            preservedSeriesId);
    }

    private static async Task<ParsedVideoIdentity> OverlayPersistedLocalStructureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        ParsedVideoIdentity parsed)
    {
        var rows = (await connection.QueryAsync<LocalStructuralNodeRow>(
            """
            WITH RECURSIVE ancestry(id,parent_id) AS (
                SELECT node.id,node.parent_id
                FROM catalog_nodes node
                JOIN node_assets binding ON binding.node_id=node.id
                WHERE binding.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT node.id,node.kind,node.season_number,node.episode_number,
                   node.absolute_episode_number,node.is_special,
                   (SELECT field.value FROM metadata_field_values field
                    WHERE field.node_id=node.id AND field.provider_id='local'
                      AND field.field='localScope' LIMIT 1) AS local_scope,
                   (SELECT field.value FROM metadata_field_values field
                    WHERE field.node_id=node.id AND field.provider_id='local'
                      AND field.field='seasonNumber' LIMIT 1) AS local_season_number,
                   (SELECT field.value FROM metadata_field_values field
                    WHERE field.node_id=node.id AND field.provider_id='local'
                      AND field.field='episodeNumber' LIMIT 1) AS local_episode_number,
                   (SELECT field.value FROM metadata_field_values field
                    WHERE field.node_id=node.id AND field.provider_id='local'
                      AND field.field='absoluteEpisodeNumber' LIMIT 1) AS local_absolute_episode_number,
                   (SELECT field.value FROM metadata_field_values field
                    WHERE field.node_id=node.id AND field.provider_id='local'
                      AND field.field='isSpecial' LIMIT 1) AS local_is_special,
                   CASE WHEN EXISTS(
                       SELECT 1 FROM metadata_field_values field
                       WHERE field.node_id=node.id AND field.provider_id='local')
                     OR EXISTS(
                       SELECT 1 FROM artwork art
                       WHERE art.node_id=node.id AND art.provider_id='local')
                   THEN 1 ELSE 0 END AS has_local_provenance
            FROM ancestry current JOIN catalog_nodes node ON node.id=current.id
            ORDER BY CASE node.kind WHEN 'episode' THEN 0 WHEN 'season' THEN 1 ELSE 2 END,
                     node.created_at,node.id;
            """,
            new { Asset = assetId.ToString("D") }, transaction)).ToArray();

        static bool HasCompatibleLocalProvenance(LocalStructuralNodeRow row, string scope) =>
            row.has_local_provenance != 0
            && (string.IsNullOrWhiteSpace(row.local_scope)
                || string.Equals(row.local_scope, scope, StringComparison.OrdinalIgnoreCase));
        static int? ParseNumber(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        static bool? ParseBoolean(string? value)
        {
            if (bool.TryParse(value, out var boolean))
                return boolean;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return number != 0;
            return null;
        }

        var episode = rows.FirstOrDefault(row =>
            string.Equals(row.kind, "episode", StringComparison.OrdinalIgnoreCase)
            && HasCompatibleLocalProvenance(row, "episode"));
        var season = rows.FirstOrDefault(row =>
            string.Equals(row.kind, "season", StringComparison.OrdinalIgnoreCase)
            && HasCompatibleLocalProvenance(row, "season"));
        if (episode == null && season == null)
            return parsed;

        // New catalogs use localScope as a provenance marker and only explicit Local structural
        // fields may override the filename. Current node scalars are a one-time fallback solely
        // for legacy Local rows that predate the marker; otherwise they may be remote structure.
        var episodeUsesLegacyScalars = episode != null
                                       && string.IsNullOrWhiteSpace(episode.local_scope);
        var seasonUsesLegacyScalars = season != null
                                      && string.IsNullOrWhiteSpace(season.local_scope);
        var localSeasonNumber = ParseNumber(episode?.local_season_number)
                                ?? ParseNumber(season?.local_season_number)
                                ?? (episodeUsesLegacyScalars ? episode?.season_number : null)
                                ?? (seasonUsesLegacyScalars ? season?.season_number : null);
        var explicitEpisodeNumber = ParseNumber(episode?.local_episode_number);
        var explicitAbsoluteNumber = ParseNumber(episode?.local_absolute_episode_number);
        var localEpisodeNumber = explicitEpisodeNumber
                                 ?? explicitAbsoluteNumber
                                 ?? (episodeUsesLegacyScalars ? episode?.episode_number : null)
                                 ?? (episodeUsesLegacyScalars ? episode?.absolute_episode_number : null);
        var localAbsoluteNumber = explicitAbsoluteNumber
                                  ?? (episodeUsesLegacyScalars ? episode?.absolute_episode_number : null)
                                  ?? parsed.AbsoluteEpisodeNumber;
        var localSpecial = ParseBoolean(episode?.local_is_special)
                           ?? ParseBoolean(season?.local_is_special)
                           ?? (localSeasonNumber.HasValue ? localSeasonNumber.Value == 0 : (bool?)null)
                           ?? (episodeUsesLegacyScalars
                               ? episode!.is_special != 0
                               : (bool?)null)
                           ?? (seasonUsesLegacyScalars
                               ? season!.is_special != 0
                               : (bool?)null);
        var isSpecial = localSpecial ?? parsed.SpecialKind != ParsedVideoSpecialKind.None;
        var episodeStart = localEpisodeNumber ?? parsed.EpisodeStart ?? parsed.AbsoluteEpisodeNumber;
        var structureOverridesEpisode = localEpisodeNumber.HasValue;

        return parsed with
        {
            SeasonNumber = isSpecial ? 0 : localSeasonNumber ?? parsed.SeasonNumber,
            EpisodeStart = episodeStart,
            EpisodeEnd = structureOverridesEpisode ? episodeStart : parsed.EpisodeEnd,
            AbsoluteEpisodeNumber = localAbsoluteNumber,
            SpecialKind = isSpecial
                ? parsed.SpecialKind == ParsedVideoSpecialKind.None
                    ? ParsedVideoSpecialKind.Special
                    : parsed.SpecialKind
                : ParsedVideoSpecialKind.None,
            IsMultiEpisode = !structureOverridesEpisode && parsed.IsMultiEpisode,
            HasEpisodeEvidence = episodeStart.HasValue || isSpecial,
        };
    }

    private static async Task<Guid> FindOrCreateScannerSeriesForResetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceId,
        ParsedVideoIdentity parsed)
    {
        var normalizedTitle = NormalizeTitle(parsed.NormalizedTitle);
        var existing = await connection.ExecuteScalarAsync<string?>(
            """
            WITH RECURSIVE descendants(root_id,node_id) AS (
                SELECT id,id FROM catalog_nodes WHERE kind='series'
                UNION ALL
                SELECT tree.root_id,child.id FROM descendants tree
                JOIN catalog_nodes child ON child.parent_id=tree.node_id
            )
            SELECT series.id FROM catalog_nodes series
            JOIN catalog_aliases alias ON alias.node_id=series.id
            JOIN descendants tree ON tree.root_id=series.id
            JOIN node_assets binding ON binding.node_id=tree.node_id
            JOIN source_assets membership ON membership.asset_id=binding.asset_id
            WHERE series.kind='series' AND alias.provider_id IN ('filename','local')
              AND alias.normalized_alias=@Normalized AND membership.source_id=@Source
              AND (series.year IS NULL OR @Year IS NULL OR series.year=@Year)
            ORDER BY series.identity_locked DESC,series.created_at LIMIT 1;
            """,
            new
            {
                Normalized = normalizedTitle,
                Source = sourceId.ToString("D"),
                parsed.Year,
            }, transaction);
        if (Guid.TryParse(existing, out var existingId))
            return existingId;

        var seriesId = Guid.NewGuid();
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            INSERT INTO catalog_nodes(
                id,parent_id,kind,primary_title,year,is_special,identity_locked,created_at,updated_at)
            VALUES(@Node,NULL,'series',@Title,@Year,0,@Locked,@Now,@Now);
            INSERT INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
            VALUES(@Node,'filename',@Title,@Normalized);
            """,
            new
            {
                Node = seriesId.ToString("D"),
                Title = parsed.NormalizedTitle,
                parsed.Year,
                Locked = parsed.ExternalIds.Count > 0 ? 1 : 0,
                Normalized = normalizedTitle,
                Now = now,
            }, transaction);
        foreach (var pair in parsed.ExternalIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,1);
                """,
                new
                {
                    Node = seriesId.ToString("D"),
                    Provider = pair.Key.ToLowerInvariant(),
                    ExternalId = pair.Value,
                }, transaction);
        }
        return seriesId;
    }

    private static async Task<bool> IsNodeIdentityLockedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId) => await connection.ExecuteScalarAsync<long>(
        """
        SELECT COUNT(*) FROM catalog_nodes node
        WHERE node.id=@Node
          AND (node.identity_locked=1
               OR EXISTS(SELECT 1 FROM external_ids external
                         WHERE external.node_id=node.id AND external.is_identity_locked=1)
               OR EXISTS(SELECT 1 FROM metadata_field_values field
                         WHERE field.node_id=node.id AND field.is_locked=1));
        """,
        new { Node = nodeId.ToString("D") }, transaction) != 0;

    private static async Task ResetAssetToUnmatchedAfterScrapeClearAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        string title)
    {
        var hasLockedNode = await connection.ExecuteScalarAsync<long>(
            """
            WITH RECURSIVE ancestry(id,parent_id,identity_locked) AS (
                SELECT node.id,node.parent_id,node.identity_locked
                FROM catalog_nodes node JOIN node_assets binding ON binding.node_id=node.id
                WHERE binding.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id,parent.identity_locked
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT COUNT(*) FROM ancestry node
            WHERE node.identity_locked=1
               OR EXISTS(SELECT 1 FROM external_ids external
                         WHERE external.node_id=node.id AND external.is_identity_locked=1)
               OR EXISTS(SELECT 1 FROM metadata_field_values field
                         WHERE field.node_id=node.id AND field.is_locked=1);
            """,
            new { Asset = assetId.ToString("D") }, transaction) != 0;
        if (hasLockedNode)
            return;

        var reusable = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT node.id FROM catalog_nodes node
            JOIN node_assets binding ON binding.node_id=node.id
            WHERE binding.asset_id=@Asset
              AND (EXISTS(SELECT 1 FROM metadata_field_values field
                          WHERE field.node_id=node.id AND field.provider_id='local')
                   OR EXISTS(SELECT 1 FROM artwork art
                             WHERE art.node_id=node.id AND art.provider_id='local')
                   OR EXISTS(SELECT 1 FROM node_user_data user WHERE user.node_id=node.id)
                   OR EXISTS(SELECT 1 FROM catalog_aliases alias
                             WHERE alias.node_id=node.id
                               AND alias.provider_id IN ('filename','local')))
            ORDER BY binding.is_preferred DESC,binding.ordinal,node.created_at LIMIT 1;
            """,
            new { Asset = assetId.ToString("D") }, transaction);
        var nodeId = Guid.TryParse(reusable, out var reusableId) ? reusableId : Guid.NewGuid();
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            "DELETE FROM node_assets WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
        if (reusable == null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES(@Node,NULL,'unmatched',@Title,0,0,@Now,@Now);
                """,
                new { Node = nodeId.ToString("D"), Title = title, Now = now }, transaction);
        }
        else
        {
            await connection.ExecuteAsync(
                """
                UPDATE catalog_nodes SET parent_id=NULL,kind='unmatched',
                    season_number=NULL,episode_number=NULL,absolute_episode_number=NULL,
                    is_special=0,updated_at=@Now WHERE id=@Node;
                """,
                new { Node = nodeId.ToString("D"), Now = now }, transaction);
        }
        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
            VALUES(@Node,'filename',@Title,@Normalized);
            INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
            VALUES(@Node,@Asset,1,0);
            """,
            new
            {
                Node = nodeId.ToString("D"),
                Asset = assetId.ToString("D"),
                Title = title,
                Normalized = NormalizeTitle(title),
            }, transaction);
    }

    private static async Task<ImmutableHashSet<string>> ResolveIdentityProvidersToLockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        string selectedProviderId,
        bool lockIdentity,
        bool preserveExistingHierarchy)
    {
        var empty = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
        if (!lockIdentity)
            return empty;
        if (!preserveExistingHierarchy)
            return empty.Add(selectedProviderId);

        var providers = await connection.QueryAsync<string>(
            """
            SELECT provider_id FROM external_ids
            WHERE node_id=@Node AND is_identity_locked=1;
            """,
            new { Node = nodeId.ToString("D") }, transaction);
        return providers.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMetadataKindCompatibleWithNode(
        string nodeKind,
        VideoMetadataMediaKind metadataKind) => metadataKind switch
    {
        VideoMetadataMediaKind.Movie => string.Equals(nodeKind, "movie", StringComparison.OrdinalIgnoreCase),
        VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Anime =>
            string.Equals(nodeKind, "series", StringComparison.OrdinalIgnoreCase),
        VideoMetadataMediaKind.Season => string.Equals(nodeKind, "season", StringComparison.OrdinalIgnoreCase),
        VideoMetadataMediaKind.Episode => string.Equals(nodeKind, "episode", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static async Task<Guid?> FindBoundAncestorNodeIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        string kind)
    {
        var id = await connection.ExecuteScalarAsync<string?>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                SELECT n.id,n.parent_id,n.kind
                FROM catalog_nodes n
                JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset
                UNION ALL
                SELECT parent.id,parent.parent_id,parent.kind
                FROM catalog_nodes parent
                JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT id FROM ancestry WHERE kind=@Kind LIMIT 1;
            """,
            new { Asset = assetId.ToString("D"), Kind = kind }, transaction);
        return Guid.TryParse(id, out var parsed) ? parsed : null;
    }

    private static Task<long> CountOtherHierarchyAssetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId) =>
        connection.ExecuteScalarAsync<long>(
            """
            WITH RECURSIVE ancestry(id,parent_id) AS (
                SELECT n.id,n.parent_id
                FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            ), roots(id) AS (
                SELECT id FROM ancestry WHERE parent_id IS NULL
            ), descendants(id) AS (
                SELECT id FROM roots
                UNION ALL
                SELECT child.id FROM catalog_nodes child
                JOIN descendants parent ON child.parent_id=parent.id
            )
            SELECT COUNT(DISTINCT na.asset_id)
            FROM descendants d JOIN node_assets na ON na.node_id=d.id
            WHERE na.asset_id<>@Asset;
            """,
            new { Asset = assetId.ToString("D") }, transaction);

    private static Task<long> CountOtherSeriesAssetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId,
        Guid assetId) =>
        connection.ExecuteScalarAsync<long>(
            """
            WITH RECURSIVE descendants(id) AS (
                SELECT id FROM catalog_nodes WHERE id=@Series AND kind='series'
                UNION ALL
                SELECT child.id FROM catalog_nodes child
                JOIN descendants parent ON child.parent_id=parent.id
            )
            SELECT COUNT(DISTINCT na.asset_id)
            FROM descendants d JOIN node_assets na ON na.node_id=d.id
            WHERE na.asset_id<>@Asset;
            """,
            new
            {
                Series = seriesId.ToString("D"),
                Asset = assetId.ToString("D"),
            }, transaction);

    private static async Task<Guid> FindOrCreateSeriesNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        VideoMetadataDetails metadata,
        string identityProviderId,
        string identityProviderItemId,
        Guid? reusableUnmatchedNodeId,
        bool lockIdentity,
        bool preserveExistingHierarchy,
        bool allowLockedIdentityReplacement)
    {
        var existing = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT e.node_id FROM external_ids e JOIN catalog_nodes n ON n.id=e.node_id
            WHERE n.kind='series' AND e.provider_id=@Provider AND e.external_id=@ExternalId LIMIT 1;
            """,
            new { Provider = identityProviderId, ExternalId = identityProviderItemId }, transaction);
        if (existing == null)
        {
            existing = await connection.ExecuteScalarAsync<string?>(
                """
                WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                    SELECT n.id,n.parent_id,n.kind
                    FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset
                    UNION ALL
                    SELECT parent.id,parent.parent_id,parent.kind
                    FROM catalog_nodes parent
                    JOIN ancestry child ON child.parent_id=parent.id
                )
                SELECT id FROM ancestry WHERE kind='series' LIMIT 1;
                """,
                new { Asset = assetId.ToString("D") }, transaction);
        }
        var id = Guid.TryParse(existing, out var parsed)
            ? parsed
            : reusableUnmatchedNodeId ?? Guid.NewGuid();
        if (existing == null)
        {
            if (reusableUnmatchedNodeId.HasValue)
            {
                await connection.ExecuteAsync(
                    "UPDATE catalog_nodes SET parent_id=NULL WHERE id=@Id;",
                    new { Id = id.ToString("D") }, transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO catalog_nodes(id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                    VALUES(@Id,'series',@Title,0,@Locked,@Now,@Now);
                    """,
                    new
                    {
                        Id = id.ToString("D"),
                        metadata.Title,
                        Locked = allowLockedIdentityReplacement ? 1 : 0,
                        Now = ToDb(DateTimeOffset.UtcNow),
                    }, transaction);
            }
        }
        var identityProvidersToLock = await ResolveIdentityProvidersToLockAsync(
            connection, transaction, id, identityProviderId,
            lockIdentity, preserveExistingHierarchy);
        await ApplyNodeMetadataAsync(
            connection, transaction, id, "series", metadata,
            identityProvidersToLock, allowLockedIdentityReplacement);
        return id;
    }

    private static async Task<Guid?> FindSeriesNodeByExternalIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string providerId,
        string providerItemId)
    {
        var value = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT e.node_id
            FROM external_ids e
            JOIN catalog_nodes n ON n.id=e.node_id
            WHERE n.kind='series' AND e.provider_id=@Provider AND e.external_id=@ExternalId
            ORDER BY n.identity_locked DESC, n.updated_at DESC
            LIMIT 1;
            """,
            new { Provider = providerId, ExternalId = providerItemId }, transaction);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private static async Task<Guid?> FindCanonicalSeriesNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        Guid currentSeriesId,
        string providerId,
        string providerItemId)
    {
        var direct = await FindSeriesNodeByExternalIdAsync(
            connection, transaction, providerId, providerItemId);
        if (direct.HasValue && direct.Value != currentSeriesId)
            return direct;

        var currentAliases = (await connection.QueryAsync<string>(
            """
            SELECT DISTINCT a.normalized_alias
            FROM catalog_aliases a
            JOIN node_assets na ON na.node_id=a.node_id
            WHERE na.asset_id=@Asset;
            """,
            new { Asset = assetId.ToString("D") }, transaction))
            .Select(NormalizeEpisodeAlias)
            .Where(alias => alias.Length >= 8)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentAliases.Count == 0)
            return null;

        var candidates = (await connection.QueryAsync<SeriesAliasRow>(
            """
            WITH RECURSIVE descendants(root_id,node_id) AS (
                SELECT id,id FROM catalog_nodes WHERE kind='series'
                UNION ALL
                SELECT d.root_id,child.id
                FROM descendants d
                JOIN catalog_nodes child ON child.parent_id=d.node_id
            )
            SELECT root.id AS root_id, alias.normalized_alias
            FROM descendants d
            JOIN catalog_nodes root ON root.id=d.root_id
            JOIN catalog_aliases alias ON alias.node_id=d.node_id
            JOIN external_ids tmdb ON tmdb.node_id=root.id AND tmdb.provider_id='tmdb'
            WHERE root.kind='series' AND root.id<>@Current
              AND d.node_id<>root.id;
            """,
            new { Current = currentSeriesId.ToString("D") }, transaction))
            .Select(row => (row.root_id, normalizedAlias: NormalizeEpisodeAlias(row.normalized_alias)))
            .Where(row => row.normalizedAlias.Length >= 8)
            .ToList();

        var match = candidates
            .Where(row => currentAliases.Contains(row.normalizedAlias))
            .GroupBy(row => row.root_id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        return Guid.TryParse(match, out var canonical) ? canonical : null;
    }

    private static string NormalizeEpisodeAlias(string value) =>
        Regex.Replace(value, "(?:S\\d{1,3}E\\d{1,4}|E\\d{1,4}|\\d{4})", "", RegexOptions.IgnoreCase);

    private sealed record SeriesAliasRow(string root_id, string normalized_alias);

    private static Task<long> CanMergeAutomaticSeriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId) =>
        connection.ExecuteScalarAsync<long>(
            """
            SELECT CASE WHEN n.identity_locked=0
                             AND NOT EXISTS(
                                 SELECT 1 FROM external_ids e
                                 WHERE e.node_id=n.id AND e.is_identity_locked=1)
                        THEN 1 ELSE 0 END
            FROM catalog_nodes n
            WHERE n.id=@Id AND n.kind='series';
            """,
            new { Id = seriesId.ToString("D") }, transaction);

    private static async Task MergeAutomaticSeriesHierarchyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceSeriesId,
        Guid targetSeriesId)
    {
        if (sourceSeriesId == targetSeriesId)
            return;

        var sourceChildren = (await connection.QueryAsync<HierarchyChildRow>(
            """
            SELECT id,kind,season_number,episode_number,absolute_episode_number
            FROM catalog_nodes
            WHERE parent_id=@Parent
            ORDER BY CASE kind WHEN 'season' THEN 0 ELSE 1 END, season_number, episode_number, id;
            """,
            new { Parent = sourceSeriesId.ToString("D") }, transaction)).ToList();

        foreach (var child in sourceChildren)
        {
            if (string.Equals(child.kind, "season", StringComparison.OrdinalIgnoreCase))
            {
                await MergeAutomaticSeasonHierarchyAsync(
                    connection, transaction, Guid.Parse(child.id), targetSeriesId, child.season_number);
            }
            else if (string.Equals(child.kind, "episode", StringComparison.OrdinalIgnoreCase))
            {
                var targetParent = await FindOrCreateMergeSeasonNodeAsync(
                    connection, transaction, targetSeriesId, child.season_number);
                await MergeAutomaticEpisodeNodeAsync(
                    connection, transaction, Guid.Parse(child.id), targetParent,
                    child.episode_number, child.absolute_episode_number);
            }
        }

        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO node_assets(node_id,asset_id,is_preferred,ordinal)
            SELECT @Target,asset_id,is_preferred,ordinal FROM node_assets WHERE node_id=@Source;
            DELETE FROM node_assets WHERE node_id=@Source;
            """,
            new { Source = sourceSeriesId.ToString("D"), Target = targetSeriesId.ToString("D") }, transaction);
        await PruneEmptyAutomaticNodesAsync(connection, transaction);
    }

    private static async Task MergeAutomaticSeasonHierarchyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceSeasonId,
        Guid targetSeriesId,
        long? seasonNumber)
    {
        if (!seasonNumber.HasValue)
        {
            await connection.ExecuteAsync(
                "UPDATE catalog_nodes SET parent_id=@Parent WHERE id=@Id;",
                new { Id = sourceSeasonId.ToString("D"), Parent = targetSeriesId.ToString("D") }, transaction);
            return;
        }

        var targetSeason = await FindOrCreateMergeSeasonNodeAsync(
            connection, transaction, targetSeriesId, seasonNumber);
        var episodes = (await connection.QueryAsync<HierarchyChildRow>(
            """
            SELECT id,kind,season_number,episode_number,absolute_episode_number
            FROM catalog_nodes WHERE parent_id=@Parent AND kind='episode'
            ORDER BY episode_number,id;
            """,
            new { Parent = sourceSeasonId.ToString("D") }, transaction)).ToList();
        foreach (var episode in episodes)
        {
            await MergeAutomaticEpisodeNodeAsync(
                connection, transaction, Guid.Parse(episode.id), targetSeason,
                episode.episode_number, episode.absolute_episode_number);
        }

        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO node_assets(node_id,asset_id,is_preferred,ordinal)
            SELECT @Target,asset_id,is_preferred,ordinal FROM node_assets WHERE node_id=@Source;
            DELETE FROM node_assets WHERE node_id=@Source;
            UPDATE catalog_nodes SET parent_id=@Target WHERE id=@Source
              AND NOT EXISTS(SELECT 1 FROM catalog_nodes WHERE parent_id=@Source);
            """,
            new { Source = sourceSeasonId.ToString("D"), Target = targetSeason.ToString("D") }, transaction);
    }

    private static async Task<Guid> FindOrCreateMergeSeasonNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId,
        long? seasonNumber)
    {
        var season = seasonNumber ?? 0;
        var value = await connection.ExecuteScalarAsync<string?>(
            "SELECT id FROM catalog_nodes WHERE parent_id=@Parent AND kind='season' AND season_number=@Season LIMIT 1;",
            new { Parent = seriesId.ToString("D"), Season = season }, transaction);
        if (Guid.TryParse(value, out var id))
            return id;

        id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO catalog_nodes(
                id,parent_id,kind,primary_title,season_number,is_special,identity_locked,created_at,updated_at)
            VALUES(@Id,@Parent,'season',@Title,@Season,@Special,0,@Now,@Now);
            """,
            new
            {
                Id = id.ToString("D"),
                Parent = seriesId.ToString("D"),
                Title = season == 0 ? "Specials" : $"Season {season}",
                Season = season,
                Special = season == 0 ? 1 : 0,
                Now = ToDb(DateTimeOffset.UtcNow),
            }, transaction);
        return id;
    }

    private static async Task MergeAutomaticEpisodeNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sourceEpisodeId,
        Guid targetParentId,
        long? episodeNumber,
        long? absoluteEpisodeNumber)
    {
        if (!episodeNumber.HasValue)
        {
            await connection.ExecuteAsync(
                "UPDATE catalog_nodes SET parent_id=@Parent WHERE id=@Id;",
                new { Id = sourceEpisodeId.ToString("D"), Parent = targetParentId.ToString("D") }, transaction);
            return;
        }

        var target = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT id FROM catalog_nodes
            WHERE parent_id=@Parent AND kind='episode' AND episode_number=@Episode
              AND COALESCE(absolute_episode_number,-1)=COALESCE(@Absolute,-1)
            LIMIT 1;
            """,
            new
            {
                Parent = targetParentId.ToString("D"),
                Episode = episodeNumber,
                Absolute = absoluteEpisodeNumber,
            }, transaction);
        if (!Guid.TryParse(target, out var targetId))
        {
            await connection.ExecuteAsync(
                "UPDATE catalog_nodes SET parent_id=@Parent WHERE id=@Id;",
                new { Id = sourceEpisodeId.ToString("D"), Parent = targetParentId.ToString("D") }, transaction);
            return;
        }

        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO node_assets(node_id,asset_id,is_preferred,ordinal)
            SELECT @Target,asset_id,is_preferred,ordinal FROM node_assets WHERE node_id=@Source;
            DELETE FROM node_assets WHERE node_id=@Source;
            """,
            new { Source = sourceEpisodeId.ToString("D"), Target = targetId.ToString("D") }, transaction);
    }

    private sealed record HierarchyChildRow(
        string id,
        string kind,
        long? season_number,
        long? episode_number,
        long? absolute_episode_number);

    private static async Task<Guid> FindOrCreateSeasonNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId,
        int seasonNumber)
    {
        var existing = await connection.ExecuteScalarAsync<string?>(
            "SELECT id FROM catalog_nodes WHERE parent_id=@Parent AND kind='season' AND season_number=@Season;",
            new { Parent = seriesId.ToString("D"), Season = seasonNumber }, transaction);
        if (Guid.TryParse(existing, out var id))
            return id;
        id = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO catalog_nodes(
                id,parent_id,kind,primary_title,season_number,is_special,identity_locked,created_at,updated_at)
            VALUES(@Id,@Parent,'season',@Title,@Season,@Special,0,@Now,@Now);
            """,
            new
            {
                Id = id.ToString("D"),
                Parent = seriesId.ToString("D"),
                Title = seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}",
                Season = seasonNumber,
                Special = seasonNumber == 0 ? 1 : 0,
                Now = ToDb(DateTimeOffset.UtcNow),
            }, transaction);
        return id;
    }

    private static async Task<Guid> FindOrCreateEpisodeNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesId,
        Guid? seasonId,
        VideoMetadataDetails metadata,
        int episodeNumber,
        string title,
        Guid? reusableUnmatchedNodeId,
        bool lockIdentity,
        bool preserveExistingHierarchy,
        bool allowLockedIdentityReplacement,
        bool applyMetadata)
    {
        var parent = seasonId ?? seriesId;
        var existing = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT id FROM catalog_nodes
            WHERE parent_id=@Parent AND kind='episode' AND episode_number=@Episode
              AND COALESCE(absolute_episode_number,-1)=COALESCE(@Absolute,-1) LIMIT 1;
            """,
            new
            {
                Parent = parent.ToString("D"),
                Episode = episodeNumber,
                Absolute = metadata.AbsoluteEpisodeNumber,
            }, transaction);
        var id = Guid.TryParse(existing, out var parsed)
            ? parsed
            : reusableUnmatchedNodeId ?? Guid.NewGuid();
        if (existing == null)
        {
            var values = new
            {
                Id = id.ToString("D"),
                Parent = parent.ToString("D"),
                Title = title,
                Original = applyMetadata ? metadata.OriginalTitle : null,
                Subtitle = applyMetadata ? metadata.Subtitle : null,
                Overview = applyMetadata ? metadata.Overview : null,
                Year = applyMetadata ? metadata.Year : null,
                Season = metadata.SeasonNumber,
                Episode = episodeNumber,
                Absolute = metadata.AbsoluteEpisodeNumber,
                Special = metadata.SeasonNumber == 0 ? 1 : 0,
                Locked = applyMetadata && allowLockedIdentityReplacement ? 1 : 0,
                Now = ToDb(DateTimeOffset.UtcNow),
            };
            if (reusableUnmatchedNodeId.HasValue)
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_nodes SET parent_id=@Parent,kind='episode',
                        primary_title=CASE WHEN EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=@Id AND f.field='title' AND f.provider_id='local')
                            THEN primary_title ELSE @Title END,
                        original_title=COALESCE(@Original,original_title),
                        subtitle=COALESCE(@Subtitle,subtitle),overview=COALESCE(@Overview,overview),
                        year=COALESCE(@Year,year),season_number=@Season,episode_number=@Episode,
                        absolute_episode_number=@Absolute,is_special=@Special,
                        identity_locked=MAX(identity_locked,@Locked),updated_at=@Now
                    WHERE id=@Id;
                    """,
                    values, transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO catalog_nodes(
                        id,parent_id,kind,primary_title,original_title,subtitle,overview,year,
                        season_number,episode_number,absolute_episode_number,is_special,identity_locked,created_at,updated_at)
                    VALUES(@Id,@Parent,'episode',@Title,@Original,@Subtitle,@Overview,@Year,
                        @Season,@Episode,@Absolute,@Special,@Locked,@Now,@Now);
                    """,
                    values, transaction);
            }
        }
        if (applyMetadata)
        {
            var identityProvidersToLock = await ResolveIdentityProvidersToLockAsync(
                connection, transaction, id, metadata.ProviderId,
                lockIdentity, preserveExistingHierarchy);
            await ApplyNodeMetadataAsync(
                connection,
                transaction,
                id,
                "episode",
                metadata with { EpisodeNumber = episodeNumber },
                identityProvidersToLock,
                allowLockedIdentityReplacement);
        }
        return id;
    }

    private static async Task ApplyNodeMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        string kind,
        VideoMetadataDetails metadata,
        ImmutableHashSet<string> identityProvidersToLock,
        bool allowLockedIdentityReplacement)
    {
        metadata = MetadataForNodeKind(metadata, kind);
        var protectedFields = (await connection.QueryAsync<MetadataFieldValueRow>(
                """
                SELECT field,value,priority,is_locked,updated_at
                FROM metadata_field_values
                WHERE node_id=@Node AND value IS NOT NULL AND TRIM(value)<>''
                  AND (provider_id='local' OR is_locked=1)
                  AND field IN ('title','originalTitle','subtitle','overview','year');
                """,
                new { Node = nodeId.ToString("D") }, transaction))
            .GroupBy(field => field.field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(field => field.is_locked)
                    .ThenByDescending(field => field.priority)
                    .ThenByDescending(field => field.updated_at, StringComparer.Ordinal)
                    .First().value,
                StringComparer.OrdinalIgnoreCase);
        var title = protectedFields.GetValueOrDefault("title") ?? metadata.Title;
        var originalTitle = protectedFields.GetValueOrDefault("originalTitle") ?? metadata.OriginalTitle;
        var subtitle = protectedFields.GetValueOrDefault("subtitle") ?? metadata.Subtitle;
        var overview = protectedFields.GetValueOrDefault("overview") ?? metadata.Overview;
        var year = int.TryParse(
            protectedFields.GetValueOrDefault("year"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var protectedYear)
            ? protectedYear
            : metadata.Year;
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            UPDATE catalog_nodes SET kind=@Kind,primary_title=@Title,
                original_title=COALESCE(@Original,original_title),subtitle=COALESCE(@Subtitle,subtitle),
                overview=COALESCE(@Overview,overview),year=COALESCE(@Year,year),
                season_number=CASE WHEN @Kind IN ('series','movie') THEN NULL
                                   ELSE COALESCE(@Season,season_number) END,
                episode_number=CASE WHEN @Kind IN ('series','season','movie') THEN NULL
                                    ELSE COALESCE(@Episode,episode_number) END,
                absolute_episode_number=CASE WHEN @Kind IN ('series','season','movie') THEN NULL
                                             ELSE COALESCE(@Absolute,absolute_episode_number) END,
                is_special=CASE WHEN @Kind IN ('series','movie') THEN 0 ELSE is_special END,
                identity_locked=MAX(identity_locked,@Locked),updated_at=@Now WHERE id=@Node;
            """,
            new
            {
                Node = nodeId.ToString("D"), Kind = kind, Title = title,
                Original = originalTitle, Subtitle = subtitle, Overview = overview, Year = year,
                Season = metadata.SeasonNumber, Episode = metadata.EpisodeNumber,
                Absolute = metadata.AbsoluteEpisodeNumber,
                Locked = identityProvidersToLock.Count > 0 ? 1 : 0,
                Now = now,
            }, transaction);
        var ids = metadata.ExternalIds.SetItem(metadata.ProviderId, metadata.ProviderItemId);
        foreach (var pair in ids)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,@Locked)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET
                    external_id=CASE
                        WHEN external_ids.is_identity_locked=1 AND @ReplaceLocked=0
                        THEN external_ids.external_id ELSE excluded.external_id END,
                    is_identity_locked=MAX(external_ids.is_identity_locked,excluded.is_identity_locked);
                """,
                new
                {
                    Node = nodeId.ToString("D"), Provider = pair.Key, ExternalId = pair.Value,
                    Locked = identityProvidersToLock.Contains(pair.Key) ? 1 : 0,
                    ReplaceLocked = allowLockedIdentityReplacement
                                    && identityProvidersToLock.Contains(pair.Key) ? 1 : 0,
                }, transaction);
        }
        foreach (var alias in metadata.Aliases.Add(metadata.Title).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        {
            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
                VALUES(@Node,@Provider,@Alias,@Normalized);
                """,
                new
                {
                    Node = nodeId.ToString("D"), Provider = metadata.ProviderId,
                    Alias = alias, Normalized = NormalizeTitle(alias),
                }, transaction);
        }
        await connection.ExecuteAsync(
            """
            INSERT INTO metadata_snapshots(
                id,node_id,provider_id,provider_item_id,payload_json,source_url,fetched_at,expires_at)
            VALUES(@Id,@Node,@Provider,@ProviderItem,@Payload,@Source,@Fetched,@Expires)
            ON CONFLICT(node_id,provider_id) DO UPDATE SET provider_item_id=excluded.provider_item_id,
                payload_json=excluded.payload_json,source_url=excluded.source_url,
                fetched_at=excluded.fetched_at,expires_at=excluded.expires_at,last_error=NULL;
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"), Node = nodeId.ToString("D"),
                Provider = metadata.ProviderId, ProviderItem = metadata.ProviderItemId,
                Payload = JsonSerializer.Serialize(metadata, JsonOptions), Source = metadata.SourceUrl,
                Fetched = ToDb(metadata.FetchedAt), Expires = ToDb(metadata.ExpiresAt),
            }, transaction);
        foreach (var field in new Dictionary<string, string?>
                 {
                     ["title"] = metadata.Title,
                     ["originalTitle"] = metadata.OriginalTitle,
                     ["subtitle"] = metadata.Subtitle,
                     ["overview"] = metadata.Overview,
                     ["year"] = metadata.Year?.ToString(CultureInfo.InvariantCulture),
                     ["genres"] = metadata.Genres.Length == 0 ? null : string.Join(", ", metadata.Genres),
                     ["actors"] = metadata.Actors.Length == 0 ? null : string.Join(", ", metadata.Actors),
                 })
        {
            if (string.IsNullOrWhiteSpace(field.Value))
                continue;
            await connection.ExecuteAsync(
                """
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES(@Node,@Field,@Value,@Provider,200,0,@Now)
                ON CONFLICT(node_id,field,provider_id) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;
                """,
                new
                {
                    Node = nodeId.ToString("D"), Field = field.Key, Value = field.Value,
                    Provider = metadata.ProviderId, Now = now,
                }, transaction);
        }
        if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase))
        {
            await PersistTypedTmdbCrossReferencesAsync(
                connection,
                transaction,
                nodeId,
                metadata);
        }
    }

    private static VideoMetadataDetails MetadataForNodeKind(
        VideoMetadataDetails metadata,
        string kind) => kind switch
    {
        "series" or "movie" => metadata with
        {
            SeasonNumber = null,
            EpisodeNumber = null,
            AbsoluteEpisodeNumber = null,
        },
        "season" => metadata with
        {
            EpisodeNumber = null,
            AbsoluteEpisodeNumber = null,
        },
        _ => metadata,
    };

    private async Task<VideoCatalogInitializationResult> InitializeCoreAsync(CancellationToken ct)
    {
        if (_initialized)
            return new VideoCatalogInitializationResult(_lastSnapshot.Mode, _lastSnapshot, _lastSnapshot.PersistentError);

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        if (File.Exists(_databasePath))
        {
            await ValidateExistingDatabaseAsync(ct);
            await ApplyCompatibilityRepairsAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            _initialized = true;
            return new VideoCatalogInitializationResult(VideoCatalogMode.Sqlite, _lastSnapshot);
        }

        await using var migrationLock = await AcquireMigrationLockAsync(ct);
        if (File.Exists(_databasePath))
        {
            await ValidateExistingDatabaseAsync(ct);
            await ApplyCompatibilityRepairsAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            _initialized = true;
            return new VideoCatalogInitializationResult(VideoCatalogMode.Sqlite, _lastSnapshot);
        }

        try
        {
            var legacy = await _legacyReader.ReadAsync(_legacyCatalogPath, ct);
            await MigrateLegacyAsync(legacy, ct);
            // A freshly created schema already contains all current behavior fixes. Record/apply
            // compatibility work before the first scan so the next launch cannot invalidate the
            // metadata cache and force a second full hierarchy reparse.
            await ApplyCompatibilityRepairsAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            _initialized = true;
            return new VideoCatalogInitializationResult(
                VideoCatalogMode.Sqlite,
                _lastSnapshot,
                LegacyCatalogSha256: legacy.Sha256);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Video catalog migration failed; keeping the legacy catalog read-only");
            _legacyReadOnly = true;
            _initialized = true;
            var legacyDocument = await TryReadLegacyLenientAsync(ct);
            _lastSnapshot = ProjectLegacySnapshot(legacyDocument, ex.Message);
            return new VideoCatalogInitializationResult(
                VideoCatalogMode.LegacyReadOnly,
                _lastSnapshot,
                ex.Message);
        }
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeCoreAsync(ct);
    }

    private async Task EnsureWritableCoreAsync(CancellationToken ct)
    {
        await EnsureInitializedCoreAsync(ct);
        if (_legacyReadOnly)
            throw new InvalidOperationException("The video catalog is read-only until migration succeeds.");
    }

    private Task WriteAsync(
        Func<SqliteConnection, SqliteTransaction, Task> operation,
        CancellationToken ct) =>
        EnqueueAsync(async () =>
        {
            await EnsureWritableCoreAsync(ct);
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await operation(connection, (SqliteTransaction)transaction);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return true;
        }, ct);

    private Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation,
        CancellationToken ct) =>
        EnqueueAsync(async () =>
        {
            await EnsureWritableCoreAsync(ct);
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            T result;
            try
            {
                result = await operation(connection, (SqliteTransaction)transaction);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return result;
        }, ct);

    private async Task MigrateLegacyAsync(
        LegacyVideoCatalogReadResult legacy,
        CancellationToken ct)
    {
        var tempPath = _databasePath + ".migrating." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var connection = await OpenConnectionAsync(tempPath, ct, enableWal: false))
            {
                await CreateSchemaAsync(connection, ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                try
                {
                    await ImportLegacyAsync(connection, (SqliteTransaction)transaction, legacy, ct);
                    await connection.ExecuteAsync($"PRAGMA user_version={SchemaVersion};", transaction: transaction);
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }

                var quickCheck = await connection.ExecuteScalarAsync<string>("PRAGMA quick_check;");
                if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Migrated video catalog failed quick_check: {quickCheck}");
                var foreignKeyErrors = (await connection.QueryAsync("PRAGMA foreign_key_check;")).AsList();
                if (foreignKeyErrors.Count != 0)
                    throw new InvalidDataException("Migrated video catalog failed foreign-key validation.");
            }

            // Microsoft.Data.Sqlite may retain a pooled handle after the creating
            // connection is disposed. Release it before the Windows atomic rename.
            SqliteConnection.ClearAllPools();
            File.Move(tempPath, _databasePath);
            await using var finalConnection = await OpenConnectionAsync(ct);
            await finalConnection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task ImportLegacyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LegacyVideoCatalogReadResult legacy,
        CancellationToken ct)
    {
        var document = legacy.Document;
        foreach (var source in document.Sources)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            await connection.ExecuteAsync(
                """
                INSERT INTO library_sources(
                    id,name,folder_path,normalized_folder_path,media_type,language,region,
                    scan_generation,created_at,last_scanned_at,last_error)
                VALUES(@Id,@Name,@Path,@Normalized,'auto','ja-JP','JP',0,@Created,@Scanned,@Error);
                """,
                new
                {
                    Id = source.Id.ToString("D"),
                    source.Name,
                    Path = path,
                    Normalized = path.ToUpperInvariant(),
                    Created = ToDb(source.CreatedAt ?? DateTimeOffset.UnixEpoch),
                    Scanned = source.LastScannedAt.HasValue ? ToDb(source.LastScannedAt.Value) : null,
                    Error = source.LastError,
                }, transaction);
        }

        var assetByIdentity = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Items)
        {
            var identity = NormalizeIdentity(item.Path);
            var metadata = GetLegacyMetadata(document, identity);
            var upsert = new VideoCatalogAssetUpsert(
                identity,
                VideoMediaAssetKind.LocalFile,
                identity,
                item.Title,
                item.ParentFolder,
                item.FileSize,
                item.ModifiedAt,
                item.ImportedAt ?? item.LastSeenAt,
                item.LastSeenAt,
                File.Exists(identity) ? VideoMediaAvailability.Available : VideoMediaAvailability.Unavailable,
                item.SourceID == Guid.Empty ? null : item.SourceID,
                BoundSubtitlePath: metadata?.BoundSubtitlePath,
                PosterPath: metadata?.PosterPath,
                ProfileId: metadata?.ProfileID,
                Tags: metadata == null ? null : string.Join(", ", metadata.Tags),
                IsFavorite: metadata?.IsFavorite == true);
            var assetId = await UpsertAssetCoreAsync(connection, transaction, upsert, null);
            assetByIdentity[identity] = assetId;
            await ApplyLegacyDisplayTitleAsync(connection, transaction, assetId, metadata?.DisplayTitle);
        }

        foreach (var remote in document.RemoteItems)
        {
            var identity = $"remote://{remote.Identity.ProviderID}/{remote.Identity.RemoteID}";
            var metadata = GetLegacyMetadata(document, identity);
            var upsert = new VideoCatalogAssetUpsert(
                identity,
                VideoMediaAssetKind.RemoteResource,
                identity,
                remote.Identity.Title,
                remote.Identity.ProviderID,
                0,
                null,
                remote.AddedAt,
                remote.LastResolvedAt,
                VideoMediaAvailability.Unknown,
                ProviderId: remote.Identity.ProviderID,
                RemoteId: remote.Identity.RemoteID,
                OriginalUrl: remote.Identity.OriginalURL,
                CanonicalUrl: remote.Identity.CanonicalURL,
                RemoteThumbnailUrl: remote.Identity.ThumbnailURL,
                RemoteSubtitleLanguage: remote.SubtitleLanguage,
                DurationSeconds: remote.Identity.Duration,
                BoundSubtitlePath: metadata?.BoundSubtitlePath,
                PosterPath: metadata?.PosterPath,
                ProfileId: metadata?.ProfileID,
                Tags: metadata == null ? null : string.Join(", ", metadata.Tags),
                IsFavorite: metadata?.IsFavorite == true);
            var assetId = await UpsertAssetCoreAsync(connection, transaction, upsert, null);
            assetByIdentity[identity] = assetId;
            await ApplyLegacyDisplayTitleAsync(connection, transaction, assetId, metadata?.DisplayTitle);
        }

        foreach (var pair in document.ItemMetadataByPath)
        {
            var identity = NormalizeIdentity(pair.Key);
            if (assetByIdentity.ContainsKey(identity))
                continue;
            var isRemote = RemoteVideoIdentity.IsPersistenceKey(identity);
            var assetId = await UpsertAssetCoreAsync(
                connection,
                transaction,
                new VideoCatalogAssetUpsert(
                    identity,
                    isRemote ? VideoMediaAssetKind.RemoteResource : VideoMediaAssetKind.LocalFile,
                    identity,
                    pair.Value.DisplayTitle ?? Path.GetFileNameWithoutExtension(identity),
                    isRemote ? "Remote" : Path.GetFileName(Path.GetDirectoryName(identity)) ?? string.Empty,
                    0,
                    null,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    VideoMediaAvailability.Unavailable,
                    BoundSubtitlePath: pair.Value.BoundSubtitlePath,
                    PosterPath: pair.Value.PosterPath,
                    ProfileId: pair.Value.ProfileID,
                    Tags: string.Join(", ", pair.Value.Tags),
                    IsFavorite: pair.Value.IsFavorite),
                null);
            assetByIdentity[identity] = assetId;
            await ApplyLegacyDisplayTitleAsync(connection, transaction, assetId, pair.Value.DisplayTitle);
        }

        foreach (var orphanIdentity in document.Collections
                     .SelectMany(collection => collection.ItemPaths)
                     .Select(NormalizeIdentity)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (assetByIdentity.ContainsKey(orphanIdentity))
                continue;
            var isRemote = RemoteVideoIdentity.IsPersistenceKey(orphanIdentity);
            var assetId = await UpsertAssetCoreAsync(
                connection,
                transaction,
                new VideoCatalogAssetUpsert(
                    orphanIdentity,
                    isRemote ? VideoMediaAssetKind.RemoteResource : VideoMediaAssetKind.LocalFile,
                    orphanIdentity,
                    isRemote ? orphanIdentity : Path.GetFileNameWithoutExtension(orphanIdentity),
                    isRemote ? "Remote" : Path.GetFileName(Path.GetDirectoryName(orphanIdentity)) ?? string.Empty,
                    0,
                    null,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    VideoMediaAvailability.Unavailable),
                null);
            assetByIdentity[orphanIdentity] = assetId;
        }

        foreach (var collection in document.Collections)
        {
            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(
                """
                INSERT INTO collections(id,name,kind,manual_sort_order,created_at,updated_at)
                VALUES(@Id,@Name,@Kind,0,@Now,@Now);
                """,
                new
                {
                    Id = collection.Id.ToString("D"),
                    collection.Name,
                    Kind = string.Equals(collection.Kind, "smart", StringComparison.OrdinalIgnoreCase) ? "smart" : "manual",
                    Now = ToDb(now),
                }, transaction);
            var ruleOrdinal = 0;
            foreach (var rule in collection.SmartRules)
            {
                var model = new VideoSmartRule
                {
                    Id = rule.Id.ToString("D"),
                    Field = Enum.TryParse<VideoSmartRuleField>(rule.Field, true, out var field)
                        ? field : VideoSmartRuleField.FileName,
                    Match = Enum.TryParse<VideoSmartRuleMatch>(rule.Match, true, out var match)
                        ? match : VideoSmartRuleMatch.Contains,
                    Value = rule.Value,
                };
                await connection.ExecuteAsync(
                    "INSERT INTO collection_rules(id,collection_id,ordinal,rule_json) VALUES(@Id,@Collection,@Ordinal,@Json);",
                    new
                    {
                        Id = rule.Id.ToString("D"),
                        Collection = collection.Id.ToString("D"),
                        Ordinal = ruleOrdinal++,
                        Json = JsonSerializer.Serialize(model, JsonOptions),
                    }, transaction);
            }

            var pathMembers = collection.ItemPaths.Select(NormalizeIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var metadataMembers = document.ItemMetadataByPath
                .Where(pair => pair.Value.CollectionIDs.Contains(collection.Id))
                .Select(pair => NormalizeIdentity(pair.Key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mismatch = pathMembers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            mismatch.SymmetricExceptWith(metadataMembers);
            if (mismatch.Count > 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO migration_audit(id,category,details_json,created_at) VALUES(@Id,'collection_membership_mismatch',@Details,@Now);",
                    new
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Details = JsonSerializer.Serialize(new { collectionId = collection.Id, identities = mismatch }, JsonOptions),
                        Now = ToDb(DateTimeOffset.UtcNow),
                    }, transaction);
            }
            var members = pathMembers.Concat(metadataMembers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ordinal = 0;
            foreach (var identity in members)
            {
                if (!assetByIdentity.TryGetValue(identity, out var assetId))
                    continue;
                await connection.ExecuteAsync(
                    "INSERT INTO collection_assets(collection_id,asset_id,ordinal) VALUES(@Collection,@Asset,@Ordinal);",
                    new
                    {
                        Collection = collection.Id.ToString("D"),
                        Asset = assetId.ToString("D"),
                        Ordinal = ordinal++,
                    }, transaction);
            }
        }

        var importedSourceCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM library_sources;", transaction: transaction);
        var importedAssetCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM media_assets;", transaction: transaction);
        var importedCollectionCount = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM collections;", transaction: transaction);
        if (importedSourceCount != document.Sources.Count
            || importedAssetCount != assetByIdentity.Count
            || importedCollectionCount != document.Collections.Count)
        {
            throw new InvalidDataException("Legacy video catalog migration count verification failed.");
        }

        var counts = JsonSerializer.Serialize(new
        {
            sources = document.Sources.Count,
            localAssets = document.Items.Count,
            remoteAssets = document.RemoteItems.Count,
            assets = assetByIdentity.Count,
            collections = document.Collections.Count,
        }, JsonOptions);
        await connection.ExecuteAsync(
            """
            INSERT INTO migration_ledger(
                id, schema_version, legacy_path, legacy_sha256, counts_json, completed_at)
            VALUES(@Id,@Version,@Path,@Sha,@Counts,@Completed);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                Version = SchemaVersion,
                Path = _legacyCatalogPath,
                Sha = legacy.Sha256,
                Counts = counts,
                Completed = ToDb(DateTimeOffset.UtcNow),
            }, transaction);
    }

    private async Task<Guid> UpsertAssetCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VideoCatalogAssetUpsert asset,
        long? scanGeneration)
    {
        var identity = NormalizeIdentity(asset.IdentityKey);
        var existing = await GetAssetIdAsync(connection, transaction, identity);
        var assetId = existing ?? Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            INSERT INTO media_assets(
                id,identity_key,kind,location,title,parent_folder,file_size,modified_at,
                imported_at,last_seen_at,availability,episode_start,episode_end,provider_id,
                remote_id,original_url,canonical_url,remote_thumbnail_url,remote_subtitle_language,
                duration_seconds,is_hidden)
            VALUES(@Id,@Identity,@Kind,@Location,@Title,@Parent,@Size,@Modified,@Imported,@Seen,
                @Availability,@EpisodeStart,@EpisodeEnd,@Provider,@RemoteId,@OriginalUrl,@CanonicalUrl,
                @Thumbnail,@SubtitleLanguage,@Duration,0)
            ON CONFLICT(identity_key) DO UPDATE SET
                kind=excluded.kind, location=excluded.location, title=excluded.title,
                parent_folder=excluded.parent_folder, file_size=excluded.file_size,
                modified_at=excluded.modified_at, last_seen_at=excluded.last_seen_at,
                availability=excluded.availability, episode_start=excluded.episode_start,
                episode_end=excluded.episode_end, provider_id=COALESCE(excluded.provider_id,media_assets.provider_id),
                remote_id=COALESCE(excluded.remote_id,media_assets.remote_id),
                original_url=COALESCE(excluded.original_url,media_assets.original_url),
                canonical_url=COALESCE(excluded.canonical_url,media_assets.canonical_url),
                remote_thumbnail_url=COALESCE(excluded.remote_thumbnail_url,media_assets.remote_thumbnail_url),
                remote_subtitle_language=COALESCE(excluded.remote_subtitle_language,media_assets.remote_subtitle_language),
                duration_seconds=COALESCE(excluded.duration_seconds,media_assets.duration_seconds), is_hidden=0;
            """,
            new
            {
                Id = assetId.ToString("D"),
                Identity = identity,
                Kind = asset.Kind == VideoMediaAssetKind.RemoteResource ? "remote" : "local",
                Location = asset.Kind == VideoMediaAssetKind.LocalFile ? Path.GetFullPath(asset.Location) : asset.Location,
                asset.Title,
                Parent = asset.ParentFolder ?? string.Empty,
                Size = Math.Max(0, asset.FileSize),
                Modified = asset.ModifiedAt.HasValue ? ToDb(asset.ModifiedAt.Value) : null,
                Imported = ToDb(asset.ImportedAt),
                Seen = ToDb(asset.LastSeenAt),
                Availability = ToDb(asset.Availability),
                asset.EpisodeStart,
                asset.EpisodeEnd,
                Provider = asset.ProviderId,
                asset.RemoteId,
                asset.OriginalUrl,
                asset.CanonicalUrl,
                Thumbnail = asset.RemoteThumbnailUrl,
                SubtitleLanguage = asset.RemoteSubtitleLanguage,
                Duration = asset.DurationSeconds,
            }, transaction);

        if (asset.SourceId.HasValue && asset.SourceId != Guid.Empty)
        {
            var sourceExists = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM library_sources WHERE id=@Id;",
                new { Id = asset.SourceId.Value.ToString("D") }, transaction) > 0;
            if (sourceExists)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO source_assets(source_id,asset_id,last_seen_generation)
                    VALUES(@Source,@Asset,@Generation)
                    ON CONFLICT(source_id,asset_id) DO UPDATE SET last_seen_generation=excluded.last_seen_generation;
                    """,
                    new
                    {
                        Source = asset.SourceId.Value.ToString("D"),
                        Asset = assetId.ToString("D"),
                        Generation = scanGeneration ?? 0,
                    }, transaction);
            }
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO asset_user_data(
                asset_id,display_title,is_favorite,bound_subtitle_path,poster_path,profile_id,updated_at)
            VALUES(@Asset,NULL,@Favorite,@Subtitle,@Poster,@Profile,@Now)
            ON CONFLICT(asset_id) DO UPDATE SET
                is_favorite=MAX(asset_user_data.is_favorite,excluded.is_favorite),
                bound_subtitle_path=COALESCE(asset_user_data.bound_subtitle_path,excluded.bound_subtitle_path),
                poster_path=COALESCE(asset_user_data.poster_path,excluded.poster_path),
                profile_id=COALESCE(asset_user_data.profile_id,excluded.profile_id),
                updated_at=excluded.updated_at;
            """,
            new
            {
                Asset = assetId.ToString("D"),
                Favorite = asset.IsFavorite ? 1 : 0,
                Subtitle = NormalizeOptionalPath(asset.BoundSubtitlePath),
                Poster = NormalizeOptionalPath(asset.PosterPath),
                Profile = asset.ProfileId,
                Now = ToDb(DateTimeOffset.UtcNow),
            }, transaction);
        if (!string.IsNullOrWhiteSpace(asset.Tags))
        {
            await ReplaceTagsAsync(
                connection,
                transaction,
                assetId,
                asset.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        var hasNode = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM node_assets WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction) > 0;
        if (!hasNode)
        {
            var nodeId = Guid.NewGuid();
            await connection.ExecuteAsync(
                """
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES(@Id,NULL,'unmatched',@Title,0,0,@Now,@Now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES(@Id,@Asset,1,0);
                """,
                new
                {
                    Id = nodeId.ToString("D"),
                    Asset = assetId.ToString("D"),
                    asset.Title,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }
        return assetId;
    }

    private static async Task ApplyParsedIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        Guid sourceId,
        ParsedVideoIdentity parsed,
        LocalVideoMetadata? local,
        bool applyMetadata,
        bool rebuildHierarchy)
    {
        var sourceMediaType = await connection.ExecuteScalarAsync<string?>(
            "SELECT media_type FROM library_sources WHERE id=@Source;",
            new { Source = sourceId.ToString("D") }, transaction);
        var isMovieSource = string.Equals(sourceMediaType, "movie", StringComparison.OrdinalIgnoreCase);
        if (isMovieSource)
        {
            parsed = parsed with
            {
                SeasonNumber = null,
                EpisodeStart = null,
                EpisodeEnd = null,
                AbsoluteEpisodeNumber = null,
                SpecialKind = ParsedVideoSpecialKind.None,
                IsMultiEpisode = false,
                HasEpisodeEvidence = false,
                EpisodeTitle = null,
            };
            if (local != null)
            {
                local = local with
                {
                    SeasonNumber = null,
                    EpisodeNumber = null,
                    AbsoluteEpisodeNumber = null,
                };
            }
        }
        await connection.ExecuteAsync(
            "UPDATE media_assets SET episode_start=@Start, episode_end=@End WHERE id=@Asset;",
            new { Start = parsed.EpisodeStart, End = parsed.EpisodeEnd, Asset = assetId.ToString("D") }, transaction);
        var currentNodes = (await connection.QueryAsync<BoundNodeRow>(
            """
            SELECT n.id,n.kind FROM catalog_nodes n
            JOIN node_assets na ON na.node_id=n.id
            WHERE na.asset_id=@Asset ORDER BY na.ordinal;
            """,
            new { Asset = assetId.ToString("D") }, transaction)).ToList();
        var nodeRows = currentNodes;
        if (nodeRows.Count == 0)
            return;

        if (isMovieSource && rebuildHierarchy)
        {
            var hasLegacyEpisodicHierarchy = await connection.ExecuteScalarAsync<long>(
                """
                WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                    SELECT n.id,n.parent_id,n.kind
                    FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset
                    UNION
                    SELECT parent.id,parent.parent_id,parent.kind
                    FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
                )
                SELECT COUNT(*) FROM ancestry WHERE kind IN ('series','season','episode');
                """,
                new { Asset = assetId.ToString("D") }, transaction) > 0;
            if (hasLegacyEpisodicHierarchy)
            {
                var movieRebuildDecision = await GetHierarchyRebuildDecisionAsync(
                    connection, transaction, assetId);
                var otherHierarchyAssets = await CountOtherHierarchyAssetsAsync(
                    connection, transaction, assetId);
                var canRepair = nodeRows.Count == 1
                                && movieRebuildDecision.CanRebuild
                                && !movieRebuildDecision.PreservedSeriesId.HasValue
                                && otherHierarchyAssets == 0;
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_jobs
                    SET state='cancelled',
                        error='Superseded by explicit Movie hierarchy repair.',
                        updated_at=@Now
                    WHERE source_id=@Source AND kind='metadata_refresh' AND state='completed';
                    """,
                    new { Source = sourceId.ToString("D"), Now = ToDb(DateTimeOffset.UtcNow) },
                    transaction);
                if (!canRepair)
                    return;

                var reusableNodeId = nodeRows[0].id;
                await connection.ExecuteAsync(
                    """
                    UPDATE catalog_nodes SET parent_id=NULL,kind='unmatched',primary_title=@Title,
                        original_title=NULL,subtitle=NULL,overview=NULL,year=@Year,
                        season_number=NULL,episode_number=NULL,absolute_episode_number=NULL,
                        is_special=0,updated_at=@Now
                    WHERE id=@Node;
                    DELETE FROM metadata_snapshots WHERE node_id=@Node;
                    DELETE FROM metadata_field_values
                    WHERE node_id=@Node AND provider_id<>'local' AND is_locked=0;
                    DELETE FROM artwork WHERE node_id=@Node AND provider_id<>'local';
                    DELETE FROM external_ids WHERE node_id=@Node AND is_identity_locked=0;
                    DELETE FROM catalog_aliases WHERE node_id=@Node;
                    DELETE FROM match_candidates WHERE asset_id=@Asset;
                    """,
                    new
                    {
                        Node = reusableNodeId,
                        Asset = assetId.ToString("D"),
                        Title = parsed.NormalizedTitle,
                        parsed.Year,
                        Now = ToDb(DateTimeOffset.UtcNow),
                    }, transaction);
                await PruneEmptyAutomaticNodesAsync(connection, transaction);
                nodeRows = (await connection.QueryAsync<BoundNodeRow>(
                    """
                    SELECT n.id,n.kind FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset ORDER BY na.ordinal;
                    """,
                    new { Asset = assetId.ToString("D") }, transaction)).ToList();
            }
        }

        var episodeStart = local?.EpisodeNumber
                           ?? local?.AbsoluteEpisodeNumber
                           ?? parsed.EpisodeStart
                           ?? parsed.AbsoluteEpisodeNumber;
        var isUnnumberedSupplemental = parsed.SpecialKind != ParsedVideoSpecialKind.None
                                       && !episodeStart.HasValue;
        var unmatchedOnly = nodeRows.All(node =>
            string.Equals(node.kind, "unmatched", StringComparison.OrdinalIgnoreCase));
        var rebuild = !unmatchedOnly && rebuildHierarchy
            ? await GetHierarchyRebuildDecisionAsync(connection, transaction, assetId)
            : HierarchyRebuildDecision.Denied;
        if ((episodeStart.HasValue || isUnnumberedSupplemental)
            && (unmatchedOnly
                || rebuild.CanRebuild))
        {
            await PromoteLocalEpisodeHierarchyAsync(
                connection, transaction, assetId, sourceId, nodeRows, parsed, local,
                episodeStart, rebuild.PreservedSeriesId);
            return;
        }

        if (applyMetadata && !unmatchedOnly && local != null)
        {
            foreach (var episodeNode in nodeRows.Where(node =>
                         string.Equals(node.kind, "episode", StringComparison.OrdinalIgnoreCase)))
            {
                await RefreshAutomaticEpisodeFromLocalMetadataAsync(
                    connection, transaction, episodeNode.id, local);
            }
            foreach (var movieNode in nodeRows.Where(node =>
                         string.Equals(node.kind, "movie", StringComparison.OrdinalIgnoreCase)))
            {
                await ApplyLocalMetadataAsync(
                    connection, transaction, movieNode.id, local, LocalVideoMetadataScope.Movie);
            }
            var seriesId = await FindBoundAncestorNodeIdAsync(
                connection, transaction, assetId, "series");
            if (seriesId.HasValue)
            {
                await ApplyLocalMetadataAsync(
                    connection, transaction, seriesId.Value.ToString("D"), local,
                    LocalVideoMetadataScope.Container);
            }
            var seasonId = await FindBoundAncestorNodeIdAsync(
                connection, transaction, assetId, "season");
            if (seasonId.HasValue)
            {
                await ApplyLocalMetadataAsync(
                    connection, transaction, seasonId.Value.ToString("D"), local,
                    LocalVideoMetadataScope.Season);
            }
        }

        if (!applyMetadata || nodeRows.Any(node => !string.Equals(node.kind, "unmatched", StringComparison.OrdinalIgnoreCase)))
            return;

        var nodeId = nodeRows[0].id;
        await connection.ExecuteAsync(
            """
            UPDATE catalog_nodes SET
                primary_title=@Title,
                original_title=COALESCE(@OriginalTitle,original_title),
                overview=COALESCE(@Overview,overview),
                year=COALESCE(@Year,year),
                season_number=COALESCE(@Season,season_number),
                episode_number=COALESCE(@Episode,episode_number),
                absolute_episode_number=COALESCE(@AbsoluteEpisode,absolute_episode_number),
                is_special=@Special, updated_at=@Now
            WHERE id=@Node;
            DELETE FROM catalog_aliases WHERE node_id=@Node AND provider_id='filename';
            INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
            VALUES(@Node,'filename',@Title,@NormalizedTitle);
            """,
            new
            {
                Node = nodeId,
                Title = local?.Title ?? parsed.NormalizedTitle,
                OriginalTitle = local?.OriginalTitle,
                Overview = local?.Overview,
                Year = local?.Year ?? parsed.Year,
                Season = local?.SeasonNumber ?? parsed.SeasonNumber,
                Episode = local?.EpisodeNumber ?? parsed.EpisodeStart,
                AbsoluteEpisode = local?.AbsoluteEpisodeNumber ?? parsed.AbsoluteEpisodeNumber,
                Special = parsed.SpecialKind == ParsedVideoSpecialKind.None ? 0 : 1,
                Now = ToDb(DateTimeOffset.UtcNow),
                NormalizedTitle = NormalizeTitle(parsed.NormalizedTitle),
            }, transaction);
        foreach (var pair in parsed.ExternalIds.Concat(local?.ExternalIds ?? ImmutableDictionary<string, string>.Empty))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,@Locked)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET
                    external_id=CASE WHEN external_ids.is_identity_locked=1
                                     THEN external_ids.external_id ELSE excluded.external_id END,
                    is_identity_locked=MAX(external_ids.is_identity_locked,excluded.is_identity_locked);
                """,
                new
                {
                    Node = nodeId,
                    Provider = pair.Key.ToLowerInvariant(),
                    ExternalId = pair.Value,
                    Locked = parsed.ExternalIds.ContainsKey(pair.Key) ? 1 : 0,
                }, transaction);
        }
        if (local != null)
        {
            var scope = isMovieSource || local.MovieMetadata != null
                ? LocalVideoMetadataScope.Movie
                : local.EpisodeMetadata != null
                    ? LocalVideoMetadataScope.Episode
                    : LocalVideoMetadataScope.Container;
            await ApplyLocalMetadataAsync(connection, transaction, nodeId, local, scope);
        }
    }

    private static async Task RefreshAutomaticEpisodeFromLocalMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        LocalVideoMetadata local)
    {
        await ApplyLocalMetadataAsync(
            connection, transaction, nodeId, local, LocalVideoMetadataScope.Episode);
    }

    private sealed record HierarchyRebuildDecision(bool CanRebuild, Guid? PreservedSeriesId)
    {
        public static HierarchyRebuildDecision Denied { get; } = new(false, null);
    }

    private static async Task<HierarchyRebuildDecision> GetHierarchyRebuildDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId)
    {
        var boundKinds = (await connection.QueryAsync<string>(
            "SELECT n.kind FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id WHERE na.asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction)).ToList();
        if (boundKinds.Count == 0
            || boundKinds.Any(kind => kind is not ("unmatched" or "episode" or "season" or "series" or "movie")))
            return HierarchyRebuildDecision.Denied;
        if (boundKinds.Any(kind => string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase)))
        {
            var hasSeriesAncestor = await connection.ExecuteScalarAsync<long>(
                """
                WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                    SELECT n.id,n.parent_id,n.kind
                    FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset
                    UNION
                    SELECT parent.id,parent.parent_id,parent.kind
                    FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
                )
                SELECT COUNT(*) FROM ancestry WHERE kind='series';
                """,
                new { Asset = assetId.ToString("D") }, transaction);
            if (hasSeriesAncestor == 0)
                return HierarchyRebuildDecision.Denied;
        }
        var protectedChild = await connection.ExecuteScalarAsync<long>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind,identity_locked) AS (
                SELECT n.id,n.parent_id,n.kind,n.identity_locked
                FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id,parent.kind,parent.identity_locked
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT COUNT(*) FROM ancestry a
            WHERE a.kind IN ('episode','season','movie')
              AND (a.identity_locked=1
                   OR EXISTS(SELECT 1 FROM external_ids e WHERE e.node_id=a.id AND e.is_identity_locked=1)
                   OR EXISTS(SELECT 1 FROM metadata_field_values f
                             WHERE f.node_id=a.id AND (f.is_locked=1 OR f.provider_id='local'))
                   OR EXISTS(SELECT 1 FROM artwork art
                             WHERE art.node_id=a.id AND art.provider_id='local')
                   OR EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=a.id));
            """,
            new { Asset = assetId.ToString("D") }, transaction);
        if (protectedChild != 0)
            return HierarchyRebuildDecision.Denied;

        var currentSeries = await connection.QuerySingleOrDefaultAsync<RebuildSeriesRow>(
            """
            WITH RECURSIVE ancestry(id,parent_id,kind) AS (
                SELECT n.id,n.parent_id,n.kind
                FROM catalog_nodes n JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset
                UNION
                SELECT parent.id,parent.parent_id,parent.kind
                FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
            )
            SELECT s.id,
                   CASE WHEN s.identity_locked=1
                          OR EXISTS(SELECT 1 FROM external_ids e
                                    WHERE e.node_id=s.id AND e.is_identity_locked=1)
                          OR EXISTS(SELECT 1 FROM metadata_field_values f
                                    WHERE f.node_id=s.id AND (f.is_locked=1 OR f.provider_id='local'))
                          OR EXISTS(SELECT 1 FROM artwork art
                                    WHERE art.node_id=s.id AND art.provider_id='local')
                          OR EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=s.id)
                        THEN 1 ELSE 0 END AS preserve
            FROM ancestry a JOIN catalog_nodes s ON s.id=a.id
            WHERE a.kind='series' LIMIT 1;
            """,
            new { Asset = assetId.ToString("D") }, transaction);
        return new HierarchyRebuildDecision(
            true,
            currentSeries is { preserve: not 0 } && Guid.TryParse(currentSeries.id, out var seriesId)
                ? seriesId
                : null);
    }

    private static async Task PromoteLocalEpisodeHierarchyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        Guid sourceId,
        IReadOnlyList<BoundNodeRow> previousNodes,
        ParsedVideoIdentity parsed,
        LocalVideoMetadata? local,
        int? episodeStart,
        Guid? preservedSeriesId)
    {
        var seriesLocal = local?.ForScope(LocalVideoMetadataScope.Container);
        var seasonLocal = local?.ForScope(LocalVideoMetadataScope.Season);
        var episodeLocal = local?.ForScope(LocalVideoMetadataScope.Episode);
        var parsedSeriesTitle = string.IsNullOrWhiteSpace(parsed.NormalizedTitle)
            ? parsed.FolderTitle ?? parsed.OriginalName
            : parsed.NormalizedTitle;
        var seriesTitle = string.IsNullOrWhiteSpace(seriesLocal?.Title)
            ? parsedSeriesTitle
            : seriesLocal.Title;
        var normalizedTitle = NormalizeTitle(parsedSeriesTitle);
        var year = seriesLocal?.Year ?? local?.Year ?? parsed.Year;
        var existingSeries = preservedSeriesId?.ToString("D")
            ?? await connection.ExecuteScalarAsync<string?>(
                """
                WITH RECURSIVE descendants(root_id,node_id) AS (
                    SELECT id,id FROM catalog_nodes WHERE kind='series'
                    UNION ALL
                    SELECT d.root_id,child.id FROM descendants d
                    JOIN catalog_nodes child ON child.parent_id=d.node_id
                )
                SELECT series.id FROM catalog_nodes series
                JOIN catalog_aliases alias ON alias.node_id=series.id
                JOIN descendants d ON d.root_id=series.id
                JOIN node_assets na ON na.node_id=d.node_id
                JOIN source_assets sa ON sa.asset_id=na.asset_id
                WHERE series.kind='series'
                  AND alias.normalized_alias=@NormalizedTitle
                  AND (series.year IS NULL OR @Year IS NULL OR series.year=@Year)
                  AND sa.source_id=@SourceId
                ORDER BY series.identity_locked DESC,
                         EXISTS(SELECT 1 FROM metadata_snapshots m WHERE m.node_id=series.id) DESC,
                         series.created_at
                LIMIT 1;
                """,
                new
                {
                    NormalizedTitle = normalizedTitle,
                    Year = year,
                    SourceId = sourceId.ToString("D"),
                }, transaction);
        var seriesId = Guid.TryParse(existingSeries, out var parsedSeriesId)
            ? parsedSeriesId
            : Guid.NewGuid();
        var now = ToDb(DateTimeOffset.UtcNow);
        if (existingSeries == null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,original_title,year,is_special,identity_locked,created_at,updated_at)
                VALUES(@Id,NULL,'series',@Title,@OriginalTitle,@Year,0,@Locked,@Now,@Now);
                """,
                new
                {
                    Id = seriesId.ToString("D"),
                    Title = seriesTitle,
                    OriginalTitle = seriesLocal?.OriginalTitle,
                    Year = year,
                    Locked = parsed.ExternalIds.Count > 0 ? 1 : 0,
                    Now = now,
                }, transaction);
        }
        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
            VALUES(@Node,'filename',@Alias,@NormalizedAlias);
            """,
            new
            {
                Node = seriesId.ToString("D"),
                Alias = parsedSeriesTitle,
                NormalizedAlias = normalizedTitle,
            }, transaction);
        foreach (var pair in parsed.ExternalIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,1)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET
                    external_id=CASE WHEN external_ids.is_identity_locked=1
                                     THEN external_ids.external_id ELSE excluded.external_id END,
                    is_identity_locked=1;
                """,
                new
                {
                    Node = seriesId.ToString("D"),
                    Provider = pair.Key.ToLowerInvariant(),
                    ExternalId = pair.Value,
                }, transaction);
        }
        if (local != null)
        {
            await ApplyLocalMetadataAsync(
                connection, transaction, seriesId.ToString("D"), local,
                LocalVideoMetadataScope.Container);
        }

        var seasonNumber = parsed.SpecialKind != ParsedVideoSpecialKind.None
            ? 0
            : seasonLocal?.SeasonNumber ?? episodeLocal?.SeasonNumber
              ?? local?.SeasonNumber ?? parsed.SeasonNumber;
        Guid? seasonId = seasonNumber.HasValue
            ? await FindOrCreateSeasonNodeAsync(connection, transaction, seriesId, seasonNumber.Value)
            : null;
        if (local != null && seasonId.HasValue)
        {
            await ApplyLocalMetadataAsync(
                connection, transaction, seasonId.Value.ToString("D"), local,
                LocalVideoMetadataScope.Season);
        }
        var parentId = seasonId ?? seriesId;
        // Jellyfin models S01E01-E02 as one logical episode entry backed by one media
        // asset. The ending number stays on media_assets; expanding the range into multiple
        // nodes duplicated the same file in details and playback queues. Supplemental assets
        // such as trailers and featurettes remain unnumbered: scan order is not an episode ID.
        var absoluteNumber = episodeLocal?.AbsoluteEpisodeNumber
                             ?? local?.AbsoluteEpisodeNumber
                             ?? parsed.AbsoluteEpisodeNumber;
        var episodeTitle = !string.IsNullOrWhiteSpace(episodeLocal?.Title)
            ? episodeLocal.Title
            : !string.IsNullOrWhiteSpace(parsed.EpisodeTitle)
                ? parsed.EpisodeTitle
                : episodeStart.HasValue && parsed.IsMultiEpisode
                    ? $"Episodes {episodeStart}-{parsed.EpisodeEnd}"
                    : episodeStart.HasValue
                        ? $"Episode {episodeStart}"
                        : parsed.OriginalName;
        var existingEpisode = episodeStart.HasValue
            ? await connection.ExecuteScalarAsync<string?>(
                """
                SELECT id FROM catalog_nodes
                WHERE parent_id=@Parent AND kind='episode' AND episode_number=@Episode
                  AND COALESCE(absolute_episode_number,-1)=COALESCE(@Absolute,-1)
                LIMIT 1;
                """,
                new
                {
                    Parent = parentId.ToString("D"),
                    Episode = episodeStart,
                    Absolute = absoluteNumber,
                }, transaction)
            : await connection.ExecuteScalarAsync<string?>(
                """
                SELECT n.id FROM catalog_nodes n
                JOIN node_assets na ON na.node_id=n.id
                WHERE na.asset_id=@Asset AND n.parent_id=@Parent AND n.kind='episode'
                  AND n.episode_number IS NULL AND n.is_special=1
                LIMIT 1;
                """,
                new
                {
                    Asset = assetId.ToString("D"),
                    Parent = parentId.ToString("D"),
                }, transaction);
        var episodeId = Guid.TryParse(existingEpisode, out var parsedEpisodeId)
            ? parsedEpisodeId
            : Guid.NewGuid();
        if (existingEpisode == null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,original_title,overview,year,season_number,
                    episode_number,absolute_episode_number,is_special,identity_locked,created_at,updated_at)
                VALUES(@Id,@Parent,'episode',@Title,@OriginalTitle,@Overview,@Year,@Season,
                    @Episode,@Absolute,@Special,0,@Now,@Now);
                """,
                new
                {
                    Id = episodeId.ToString("D"),
                    Parent = parentId.ToString("D"),
                    Title = episodeTitle,
                    OriginalTitle = episodeLocal?.OriginalTitle,
                    Overview = episodeLocal?.Overview,
                    Year = episodeLocal?.Year ?? year,
                    Season = seasonNumber,
                    Episode = episodeStart,
                    Absolute = absoluteNumber,
                    Special = parsed.SpecialKind == ParsedVideoSpecialKind.None ? 0 : 1,
                    Now = now,
                }, transaction);
        }
        else
        {
            // Compatibility reparses may reuse the surviving logical episode. Refresh only
            // automatic fields: explicit/user locks and provider-resolved titles remain authoritative.
            await connection.ExecuteAsync(
                """
                UPDATE catalog_nodes SET
                    primary_title=CASE
                        WHEN NOT EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=catalog_nodes.id AND f.field='title' AND f.is_locked=1)
                         AND (@HasLocalTitle=1 OR NOT EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=catalog_nodes.id AND f.field='title'))
                        THEN @Title ELSE primary_title END,
                    original_title=CASE
                        WHEN @OriginalTitle IS NOT NULL AND NOT EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=catalog_nodes.id AND f.field='originalTitle' AND f.is_locked=1)
                        THEN @OriginalTitle ELSE original_title END,
                    overview=CASE
                        WHEN @Overview IS NOT NULL AND NOT EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=catalog_nodes.id AND f.field='overview' AND f.is_locked=1)
                        THEN @Overview ELSE overview END,
                    year=CASE
                        WHEN @Year IS NOT NULL AND NOT EXISTS(
                            SELECT 1 FROM metadata_field_values f
                            WHERE f.node_id=catalog_nodes.id AND f.field='year' AND f.is_locked=1)
                        THEN @Year ELSE year END,
                    season_number=@Season,
                    episode_number=@Episode,
                    absolute_episode_number=@Absolute,
                    is_special=@Special,
                    updated_at=@Now
                WHERE id=@Id AND identity_locked=0
                  AND NOT EXISTS(SELECT 1 FROM external_ids e
                                 WHERE e.node_id=catalog_nodes.id AND e.is_identity_locked=1)
                  AND NOT EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=catalog_nodes.id);
                """,
                new
                {
                    Id = episodeId.ToString("D"),
                    Title = episodeTitle,
                    HasLocalTitle = string.IsNullOrWhiteSpace(episodeLocal?.Title) ? 0 : 1,
                    OriginalTitle = episodeLocal?.OriginalTitle,
                    Overview = episodeLocal?.Overview,
                    Year = episodeLocal?.Year ?? year,
                    Season = seasonNumber,
                    Episode = episodeStart,
                    Absolute = absoluteNumber,
                    Special = parsed.SpecialKind == ParsedVideoSpecialKind.None ? 0 : 1,
                    Now = now,
                }, transaction);
        }
        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
            VALUES(@Node,'filename',@Alias,@NormalizedAlias);
            """,
            new
            {
                Node = episodeId.ToString("D"),
                Alias = parsed.OriginalName,
                NormalizedAlias = NormalizeTitle(parsed.OriginalName),
            }, transaction);
        if (local != null)
        {
            await ApplyLocalMetadataAsync(
                connection, transaction, episodeId.ToString("D"), local,
                LocalVideoMetadataScope.Episode);
        }

        await connection.ExecuteAsync(
            "DELETE FROM node_assets WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal) VALUES(@Node,@Asset,1,0);",
            new { Node = episodeId.ToString("D"), Asset = assetId.ToString("D") }, transaction);
        foreach (var previous in previousNodes)
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM catalog_nodes WHERE id=@Node AND kind IN ('unmatched','episode')
                  AND identity_locked=0
                  AND NOT EXISTS(SELECT 1 FROM node_assets WHERE node_id=@Node)
                  AND NOT EXISTS(SELECT 1 FROM metadata_snapshots WHERE node_id=@Node)
                  AND NOT EXISTS(SELECT 1 FROM metadata_field_values WHERE node_id=@Node)
                  AND NOT EXISTS(SELECT 1 FROM artwork WHERE node_id=@Node)
                  AND NOT EXISTS(SELECT 1 FROM external_ids WHERE node_id=@Node)
                  AND NOT EXISTS(SELECT 1 FROM node_user_data WHERE node_id=@Node);
                """,
                new { Node = previous.id }, transaction);
        }
        await connection.ExecuteAsync(
            "DELETE FROM match_candidates WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
        var bindingChanged = previousNodes.Count != 1
                             || !string.Equals(
                                 previousNodes[0].id,
                                 episodeId.ToString("D"),
                                 StringComparison.OrdinalIgnoreCase);
        if (bindingChanged)
        {
            await connection.ExecuteAsync(
                """
                UPDATE catalog_jobs
                SET state='cancelled', error='Superseded by catalog hierarchy repair.', updated_at=@Now
                WHERE source_id=@Source AND kind='metadata_refresh' AND state='completed';
                """,
                new
                {
                    Source = sourceId.ToString("D"),
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }
    }

    private static async Task ApplyLocalMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        LocalVideoMetadata local,
        LocalVideoMetadataScope scope)
    {
        var values = local.ForScope(scope);
        var hasLocalProvenance = values != null;
        if (!HasLocalMetadataValues(values))
            values = null;
        var artwork = scope == LocalVideoMetadataScope.Movie
            ? local.ArtworkForScope(LocalVideoMetadataScope.Movie)
                .Concat(local.ArtworkForScope(LocalVideoMetadataScope.Container))
                .Concat(local.ArtworkForScope(LocalVideoMetadataScope.Episode))
                .DistinctBy(item => $"{item.Kind}\0{item.Path}", StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray()
            : local.ArtworkForScope(scope);
        hasLocalProvenance |= !artwork.IsDefaultOrEmpty;
        await SynchronizeLocalFieldsAsync(
            connection, transaction, nodeId, values, scope, hasLocalProvenance);
        await SynchronizeLocalArtworkAsync(connection, transaction, nodeId, artwork);
    }

    private static bool HasLocalMetadataValues(LocalVideoMetadataValues? values) =>
        values?.HasValues == true;

    private static async Task SynchronizeLocalFieldsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        LocalVideoMetadataValues? values,
        LocalVideoMetadataScope scope,
        bool hasLocalProvenance)
    {
        var previous = (await connection.QueryAsync<LocalFieldRow>(
                "SELECT field,value FROM metadata_field_values WHERE node_id=@Node AND provider_id='local';",
                new { Node = nodeId }, transaction))
            .ToDictionary(row => row.field, row => row.value, StringComparer.OrdinalIgnoreCase);
        var node = await connection.QuerySingleAsync<LocalNodeScalarRow>(
            """
            SELECT kind,primary_title,original_title,overview,year,season_number,
                   episode_number,absolute_episode_number,is_special
            FROM catalog_nodes WHERE id=@Node;
            """,
            new { Node = nodeId }, transaction);
        await connection.ExecuteAsync(
            "DELETE FROM metadata_field_values WHERE node_id=@Node AND provider_id='local';",
            new { Node = nodeId }, transaction);

        var now = ToDb(DateTimeOffset.UtcNow);
        var people = values == null
            ? ImmutableArray<VideoPersonCredit>.Empty
            : values.Directors.Select((name, index) => new VideoPersonCredit(
                    $"local-director-{index}", name, null, "Director", null))
                .ToImmutableArray();
        var fields = new Dictionary<string, string?>
        {
            // Local NFO/artwork ownership is provenance, not a rebuildable node scalar.
            // Keep the scope even for an empty NFO or artwork-only owner, while a completely
            // empty scan result writes no marker at all.
            ["localScope"] = hasLocalProvenance ? scope.ToString().ToLowerInvariant() : null,
            ["seasonNumber"] = values?.SeasonNumber?.ToString(CultureInfo.InvariantCulture),
            ["episodeNumber"] = values?.EpisodeNumber?.ToString(CultureInfo.InvariantCulture),
            ["absoluteEpisodeNumber"] = values?.AbsoluteEpisodeNumber?.ToString(CultureInfo.InvariantCulture),
            ["isSpecial"] = values?.SeasonNumber == null
                            || scope is not (LocalVideoMetadataScope.Season or LocalVideoMetadataScope.Episode)
                ? null
                : (values.SeasonNumber.Value == 0)
                    .ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            ["title"] = values?.Title,
            ["originalTitle"] = values?.OriginalTitle,
            ["overview"] = values?.Overview,
            ["year"] = values?.Year?.ToString(CultureInfo.InvariantCulture),
            ["genres"] = SerializeLocalArray(values?.Genres ?? []),
            ["actors"] = SerializeLocalArray(values?.Actors ?? []),
            ["tagline"] = values?.Tagline,
            ["officialRating"] = values?.OfficialRating,
            ["communityRating"] = values?.CommunityRating?.ToString(CultureInfo.InvariantCulture),
            ["status"] = values?.Status,
            ["tags"] = SerializeLocalArray(values?.Tags ?? []),
            ["studios"] = SerializeLocalArray(values?.Studios ?? []),
            ["people"] = people.Length == 0 ? null : JsonSerializer.Serialize(people, JsonOptions),
            ["externalIds"] = values == null || values.ExternalIds.Count == 0
                ? null
                : JsonSerializer.Serialize(values.ExternalIds, JsonOptions),
        };
        foreach (var field in fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES(@Node,@Field,@Value,'local',300,0,@Now);
                """,
                new { Node = nodeId, Field = field.Key, Value = field.Value, Now = now }, transaction);
        }

        await SynchronizeLocalAliasesAndIdsAsync(
            connection, transaction, nodeId, previous.GetValueOrDefault("externalIds"), values, now);
        var remaining = (await connection.QueryAsync<MetadataFieldValueRow>(
                """
                SELECT field,value,priority,is_locked,updated_at
                FROM metadata_field_values
                WHERE node_id=@Node AND value IS NOT NULL AND TRIM(value)<>''
                  AND field IN ('title','originalTitle','overview','year');
                """,
                new { Node = nodeId }, transaction))
            .GroupBy(field => field.field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(field => field.is_locked)
                    .ThenByDescending(field => field.priority)
                    .ThenByDescending(field => field.updated_at, StringComparer.Ordinal)
                    .First().value,
                StringComparer.OrdinalIgnoreCase);
        var filenameTitle = await connection.ExecuteScalarAsync<string?>(
            "SELECT alias FROM catalog_aliases WHERE node_id=@Node AND provider_id='filename' ORDER BY alias LIMIT 1;",
            new { Node = nodeId }, transaction);
        var structuralTitle = node.kind switch
        {
            "season" when node.season_number == 0 => "Specials",
            "season" when node.season_number.HasValue => $"Season {node.season_number.Value}",
            "episode" when node.episode_number.HasValue => $"Episode {node.episode_number.Value}",
            _ => filenameTitle ?? node.primary_title,
        };
        var title = ResolveSynchronizedScalar(
            node.primary_title, previous.GetValueOrDefault("title"), values?.Title,
            remaining.GetValueOrDefault("title"), structuralTitle);
        var originalTitle = ResolveSynchronizedScalar(
            node.original_title, previous.GetValueOrDefault("originalTitle"), values?.OriginalTitle,
            remaining.GetValueOrDefault("originalTitle"), null);
        var overview = ResolveSynchronizedScalar(
            node.overview, previous.GetValueOrDefault("overview"), values?.Overview,
            remaining.GetValueOrDefault("overview"), null);
        var currentYear = node.year?.ToString(CultureInfo.InvariantCulture);
        var resolvedYear = ResolveSynchronizedScalar(
            currentYear, previous.GetValueOrDefault("year"),
            values?.Year?.ToString(CultureInfo.InvariantCulture),
            remaining.GetValueOrDefault("year"), null);
        await connection.ExecuteAsync(
            """
            UPDATE catalog_nodes SET primary_title=@Title,original_title=@OriginalTitle,
                overview=@Overview,year=@Year,updated_at=@Now WHERE id=@Node;
            """,
            new
            {
                Node = nodeId,
                Title = title ?? node.primary_title,
                OriginalTitle = originalTitle,
                Overview = overview,
                Year = int.TryParse(resolvedYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                    ? year
                    : (int?)null,
                Now = now,
            }, transaction);
    }

    private static async Task SynchronizeLocalAliasesAndIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        string? previousExternalIdsJson,
        LocalVideoMetadataValues? values,
        string now)
    {
        await connection.ExecuteAsync(
            "DELETE FROM catalog_aliases WHERE node_id=@Node AND provider_id='local';",
            new { Node = nodeId }, transaction);
        foreach (var alias in new[] { values?.Title, values?.OriginalTitle }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
                VALUES(@Node,'local',@Alias,@Normalized);
                """,
                new { Node = nodeId, Alias = alias, Normalized = NormalizeTitle(alias) }, transaction);
        }

        foreach (var pair in DeserializeLocalExternalIds(previousExternalIdsJson))
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM external_ids
                WHERE node_id=@Node AND provider_id=@Provider AND external_id=@ExternalId
                  AND is_identity_locked=0;
                """,
                new { Node = nodeId, Provider = pair.Key, ExternalId = pair.Value }, transaction);
        }
        if (values == null)
            return;
        foreach (var pair in values.ExternalIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,0)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET
                    external_id=CASE WHEN external_ids.is_identity_locked=1
                                     THEN external_ids.external_id ELSE excluded.external_id END;
                """,
                new { Node = nodeId, Provider = pair.Key.ToLowerInvariant(), ExternalId = pair.Value }, transaction);
        }
    }

    private static string? ResolveSynchronizedScalar(
        string? current,
        string? previousLocal,
        string? newLocal,
        string? bestPersisted,
        string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(bestPersisted))
            return bestPersisted;
        if (!string.IsNullOrWhiteSpace(newLocal))
            return newLocal;
        return !string.IsNullOrWhiteSpace(previousLocal)
               && string.Equals(current, previousLocal, StringComparison.Ordinal)
            ? fallback
            : current;
    }

    private static string? SerializeLocalArray(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty ? null : JsonSerializer.Serialize(values, JsonOptions);

    private static ImmutableDictionary<string, string> DeserializeLocalExternalIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ImmutableDictionary<string, string>.Empty;
        try
        {
            return JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(value, JsonOptions)
                   ?? ImmutableDictionary<string, string>.Empty;
        }
        catch (JsonException)
        {
            return ImmutableDictionary<string, string>.Empty;
        }
    }

    private static ImmutableArray<string> DeserializeLocalArray(
        string? value,
        ImmutableArray<string> fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback.IsDefault ? [] : fallback;
        try
        {
            return JsonSerializer.Deserialize<ImmutableArray<string>>(value, JsonOptions) is { IsDefault: false } parsed
                ? parsed
                : fallback.IsDefault ? [] : fallback;
        }
        catch (JsonException)
        {
            return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToImmutableArray();
        }
    }

    private static ImmutableArray<VideoPersonCredit> DeserializeLocalPeople(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<ImmutableArray<VideoPersonCredit>>(value, JsonOptions);
            return parsed.IsDefault ? [] : parsed;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Task PruneEmptyAutomaticNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        connection.ExecuteAsync(
            """
            DELETE FROM catalog_nodes
            WHERE kind IN ('unmatched','episode','movie')
              AND identity_locked=0
              AND NOT EXISTS(SELECT 1 FROM node_assets na WHERE na.node_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM catalog_nodes child WHERE child.parent_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM external_ids e
                             WHERE e.node_id=catalog_nodes.id AND e.is_identity_locked=1)
              AND NOT EXISTS(SELECT 1 FROM metadata_field_values f
                             WHERE f.node_id=catalog_nodes.id
                               AND (f.is_locked=1 OR f.provider_id='local'))
              AND NOT EXISTS(SELECT 1 FROM artwork a
                             WHERE a.node_id=catalog_nodes.id AND a.provider_id='local')
              AND NOT EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=catalog_nodes.id);

            DELETE FROM catalog_nodes
            WHERE kind='season'
              AND identity_locked=0
              AND NOT EXISTS(SELECT 1 FROM node_assets na WHERE na.node_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM catalog_nodes child WHERE child.parent_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM external_ids e
                             WHERE e.node_id=catalog_nodes.id AND e.is_identity_locked=1)
              AND NOT EXISTS(SELECT 1 FROM metadata_field_values f
                             WHERE f.node_id=catalog_nodes.id
                               AND (f.is_locked=1 OR f.provider_id='local'))
              AND NOT EXISTS(SELECT 1 FROM artwork a
                             WHERE a.node_id=catalog_nodes.id AND a.provider_id='local')
              AND NOT EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=catalog_nodes.id);

            DELETE FROM catalog_nodes
            WHERE kind='series'
              AND identity_locked=0
              AND NOT EXISTS(SELECT 1 FROM node_assets na WHERE na.node_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM catalog_nodes child WHERE child.parent_id=catalog_nodes.id)
              AND NOT EXISTS(SELECT 1 FROM external_ids e
                             WHERE e.node_id=catalog_nodes.id AND e.is_identity_locked=1)
              AND NOT EXISTS(SELECT 1 FROM metadata_field_values f
                             WHERE f.node_id=catalog_nodes.id
                               AND (f.is_locked=1 OR f.provider_id='local'))
              AND NOT EXISTS(SELECT 1 FROM artwork a
                             WHERE a.node_id=catalog_nodes.id AND a.provider_id='local')
              AND NOT EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=catalog_nodes.id);
            """,
            transaction: transaction);

    private static async Task SynchronizeLocalArtworkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        ImmutableArray<LocalVideoArtwork> desiredArtwork)
    {
        var now = ToDb(DateTimeOffset.UtcNow);
        var desired = desiredArtwork
            .Where(item => File.Exists(item.Path))
            .DistinctBy(item => $"{item.Kind}\0{item.Path}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existing = (await connection.QueryAsync<LocalArtworkRow>(
                """
                SELECT id,kind,local_path,selected,ordinal
                FROM artwork WHERE node_id=@Node AND provider_id='local';
                """,
                new { Node = nodeId }, transaction)).ToList();
        var desiredKeys = desired
            .Select(item => $"{item.Kind}\0{item.Path}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var obsolete = existing
            .Where(item => item.local_path == null
                           || !desiredKeys.Contains($"{item.kind}\0{item.local_path}"))
            .ToArray();
        var obsoleteIds = obsolete
            .Select(item => item.id)
            .ToArray();
        if (obsoleteIds.Length > 0)
        {
            await connection.ExecuteAsync(
                """
                UPDATE node_user_data SET preferred_artwork_id=NULL
                WHERE node_id=@Node AND preferred_artwork_id IN @Ids;
                UPDATE asset_user_data SET poster_path=NULL
                WHERE poster_path IN (
                    SELECT local_path FROM artwork
                    WHERE id IN @Ids AND local_path IS NOT NULL);
                DELETE FROM artwork WHERE id IN @Ids;
                """,
                new { Node = nodeId, Ids = obsoleteIds }, transaction);
        }

        var kindOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var artwork in desired)
        {
            var ordinal = kindOrdinals.GetValueOrDefault(artwork.Kind);
            kindOrdinals[artwork.Kind] = ordinal + 1;
            var match = existing.FirstOrDefault(item => item.local_path != null
                && item.kind.Equals(artwork.Kind, StringComparison.OrdinalIgnoreCase)
                && item.local_path.Equals(artwork.Path, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                await connection.ExecuteAsync(
                    "UPDATE artwork SET selected=@Selected,ordinal=@Ordinal,updated_at=@Now WHERE id=@Id;",
                    new
                    {
                        Id = match.id,
                        Selected = ordinal == 0 ? 1 : 0,
                        Ordinal = ordinal,
                        Now = now,
                    }, transaction);
                continue;
            }
            await connection.ExecuteAsync(
                """
                INSERT INTO artwork(
                    id,node_id,provider_id,kind,local_path,selected,ordinal,created_at,updated_at)
                VALUES(@Id,@Node,'local',@Kind,@Path,@Selected,@Ordinal,@Now,@Now);
                """,
                new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Node = nodeId,
                    Kind = artwork.Kind,
                    Path = artwork.Path,
                    Selected = ordinal == 0 ? 1 : 0,
                    Ordinal = ordinal,
                    Now = now,
                }, transaction);
        }
    }

    private static async Task PersistTypedTmdbCrossReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesNodeId,
        VideoMetadataDetails metadata)
    {
        var tmdbIdText = metadata.ProviderId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
            ? metadata.ProviderItemId
            : metadata.ExternalIds.GetValueOrDefault("tmdb");
        if (!int.TryParse(tmdbIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbShowId)
            || tmdbShowId <= 0)
        {
            return;
        }

        var seriesNode = seriesNodeId.ToString("D");
        var now = ToDb(DateTimeOffset.UtcNow);
        var preferredOrdering = metadata.TmdbOrdering;
        var representedOrderings = metadata.Seasons
            .Where(season => !string.IsNullOrWhiteSpace(season.TmdbOrderingId))
            .Select(season => new VideoTmdbOrdering(
                season.TmdbShowId ?? tmdbShowId,
                season.TmdbOrderingId!,
                season.TmdbOrderingType,
                preferredOrdering != null
                && string.Equals(
                    preferredOrdering.OrderingId,
                    season.TmdbOrderingId,
                    StringComparison.Ordinal)))
            .Append(preferredOrdering ?? new VideoTmdbOrdering(
                tmdbShowId,
                tmdbShowId.ToString(CultureInfo.InvariantCulture),
                VideoTmdbOrderingType.Default,
                IsPreferred: true))
            .Where(ordering => ordering.TmdbShowId == tmdbShowId)
            .DistinctBy(ordering => ordering.OrderingId, StringComparer.Ordinal)
            .ToArray();

        var hasUserPreferredOrdering = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tmdb_orderings WHERE series_node_id=@Series AND tmdb_show_id=@Show AND is_user_preferred=1;",
            new { Series = seriesNode, Show = tmdbShowId }, transaction) != 0;
        if (!hasUserPreferredOrdering)
        {
            await connection.ExecuteAsync(
                "UPDATE tmdb_orderings SET is_preferred=0,updated_at=@Now WHERE series_node_id=@Series AND tmdb_show_id=@Show;",
                new { Series = seriesNode, Show = tmdbShowId, Now = now }, transaction);
        }
        foreach (var ordering in representedOrderings)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO tmdb_orderings(
                    series_node_id,tmdb_show_id,ordering_id,ordering_type,
                    is_preferred,is_user_preferred,created_at,updated_at)
                VALUES(@Series,@Show,@Ordering,@Type,@Preferred,0,@Now,@Now)
                ON CONFLICT(series_node_id,tmdb_show_id,ordering_id) DO UPDATE SET
                    ordering_type=excluded.ordering_type,
                    is_preferred=CASE
                        WHEN tmdb_orderings.is_user_preferred=1 THEN tmdb_orderings.is_preferred
                        WHEN @HasUserPreferred=1 THEN 0
                        ELSE excluded.is_preferred END,
                    updated_at=excluded.updated_at;
                """,
                new
                {
                    Series = seriesNode,
                    Show = tmdbShowId,
                    Ordering = ordering.OrderingId,
                    Type = (int)ordering.Type,
                    Preferred = ordering.IsPreferred ? 1 : 0,
                    HasUserPreferred = hasUserPreferredOrdering ? 1 : 0,
                    Now = now,
                }, transaction);
        }

        var aidText = metadata.ExternalIds.GetValueOrDefault("anidb")
                      ?? await connection.ExecuteScalarAsync<string?>(
                          "SELECT external_id FROM external_ids WHERE node_id=@Node AND provider_id='anidb';",
                          new { Node = seriesNode }, transaction);
        if (!int.TryParse(aidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId)
            || animeId <= 0)
        {
            return;
        }

        var chosenOrdering = preferredOrdering
                             ?? representedOrderings.FirstOrDefault(ordering => ordering.IsPreferred)
                             ?? representedOrderings.First();
        await connection.ExecuteAsync(
            """
            INSERT INTO tmdb_show_xrefs(
                series_node_id,anidb_anime_id,tmdb_show_id,chosen_ordering_id,
                chosen_ordering_type,match_rating,created_at,updated_at)
            VALUES(@Series,@Aid,@Show,@Ordering,@OrderingType,@Rating,@Now,@Now)
            ON CONFLICT(series_node_id,anidb_anime_id,tmdb_show_id) DO UPDATE SET
                chosen_ordering_id=excluded.chosen_ordering_id,
                chosen_ordering_type=excluded.chosen_ordering_type,
                match_rating=CASE WHEN tmdb_show_xrefs.match_rating=@UserVerified
                                  THEN tmdb_show_xrefs.match_rating ELSE excluded.match_rating END,
                updated_at=excluded.updated_at;
            """,
            new
            {
                Series = seriesNode,
                Aid = animeId,
                Show = tmdbShowId,
                Ordering = chosenOrdering.OrderingId,
                OrderingType = (int)chosenOrdering.Type,
                Rating = (int)VideoMetadataMatchRating.FirstAvailable,
                UserVerified = (int)VideoMetadataMatchRating.UserVerified,
                Now = now,
            }, transaction);

        var localEpisodes = (await connection.QueryAsync<TypedAniDbEpisodeRow>(
            """
            WITH RECURSIVE descendants(id,parent_id,kind,primary_title,year,season_number,episode_number) AS (
                SELECT id,parent_id,kind,primary_title,year,season_number,episode_number
                FROM catalog_nodes WHERE id=@Series
                UNION ALL
                SELECT child.id,child.parent_id,child.kind,child.primary_title,child.year,
                       child.season_number,child.episode_number
                FROM catalog_nodes child
                JOIN descendants parent ON child.parent_id=parent.id
            )
            SELECT d.id AS episode_node_id,d.primary_title,d.year,d.season_number,d.episode_number,
                   eid.external_id AS anidb_episode_id,
                   COALESCE(owner.external_id,@AidText) AS anidb_anime_id
            FROM descendants d
            JOIN external_ids eid ON eid.node_id=d.id AND eid.provider_id='anidb-episode'
            LEFT JOIN external_ids owner ON owner.node_id=d.id AND owner.provider_id='anidb'
            WHERE d.kind='episode';
            """,
            new { Series = seriesNode, AidText = animeId.ToString(CultureInfo.InvariantCulture) }, transaction)).ToArray();
        var tmdbEpisodes = metadata.Seasons
            .SelectMany(season => (season.Episodes.IsDefault ? [] : season.Episodes)
                .Where(episode => episode.TmdbEpisodeId is > 0)
                .Select(episode => new TypedTmdbEpisodeCandidate(season, episode)))
            .ToArray();
        foreach (var localEpisode in localEpisodes)
        {
            if (!int.TryParse(
                    localEpisode.anidb_episode_id,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var episodeId)
                || !int.TryParse(
                    localEpisode.anidb_anime_id,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ownerAnimeId))
            {
                continue;
            }

            var localTitle = NormalizeTitle(localEpisode.primary_title);
            var candidates = tmdbEpisodes
                .Select(candidate => new
                {
                    Candidate = candidate,
                    TitleMatches = localTitle.Length > 0
                                   && string.Equals(
                                       localTitle,
                                       NormalizeTitle(candidate.Episode.Title),
                                       StringComparison.Ordinal),
                    YearMatches = localEpisode.year.HasValue
                                  && YearFromProviderDate(candidate.Episode.AirDate) == localEpisode.year,
                    SeasonMatches = localEpisode.season_number.HasValue
                                    && candidate.Season.SeasonNumber == localEpisode.season_number,
                    EpisodeMatches = localEpisode.episode_number.HasValue
                                     && candidate.Episode.EpisodeNumber == localEpisode.episode_number,
                })
                .Where(candidate => candidate.TitleMatches || candidate.EpisodeMatches)
                .OrderByDescending(candidate => candidate.TitleMatches)
                .ThenByDescending(candidate => candidate.YearMatches)
                .ThenByDescending(candidate => candidate.SeasonMatches)
                .ThenByDescending(candidate => candidate.EpisodeMatches)
                .ThenBy(candidate => candidate.Candidate.Season.Ordinal)
                .ThenBy(candidate => candidate.Candidate.Episode.Ordinal)
                .ToArray();
            if (candidates.FirstOrDefault() is not { } best)
                continue;

            var tmdbEpisode = best.Candidate.Episode;
            var tmdbSeason = best.Candidate.Season;
            var orderingId = tmdbEpisode.TmdbOrderingId
                             ?? tmdbSeason.TmdbOrderingId
                             ?? chosenOrdering.OrderingId;
            var seasonId = tmdbEpisode.TmdbEpisodeGroupId
                           ?? tmdbSeason.TmdbEpisodeGroupId
                           ?? tmdbEpisode.TmdbSeasonId?.ToString(CultureInfo.InvariantCulture)
                           ?? tmdbSeason.TmdbSeasonId?.ToString(CultureInfo.InvariantCulture);
            var matchRating = best.TitleMatches
                ? best.YearMatches
                    ? VideoMetadataMatchRating.DateAndTitleKindaMatches
                    : VideoMetadataMatchRating.TitleMatches
                : best.YearMatches
                    ? VideoMetadataMatchRating.DateKindaMatches
                    : VideoMetadataMatchRating.FirstAvailable;
            await connection.ExecuteAsync(
                """
                INSERT INTO tmdb_episode_xrefs(
                    episode_node_id,series_node_id,anidb_anime_id,anidb_episode_id,
                    tmdb_show_id,tmdb_episode_id,ordering_id,season_id,season_number,
                    episode_number,ordinal,match_rating,created_at,updated_at)
                VALUES(@EpisodeNode,@Series,@Aid,@Eid,@Show,@TmdbEpisode,@Ordering,
                       @SeasonId,@Season,@Episode,@Ordinal,@Rating,@Now,@Now)
                ON CONFLICT(
                    episode_node_id,anidb_episode_id,tmdb_show_id,tmdb_episode_id,ordering_id)
                DO UPDATE SET
                    series_node_id=excluded.series_node_id,
                    anidb_anime_id=excluded.anidb_anime_id,
                    season_id=excluded.season_id,
                    season_number=excluded.season_number,
                    episode_number=excluded.episode_number,
                    ordinal=excluded.ordinal,
                    match_rating=CASE WHEN tmdb_episode_xrefs.match_rating=@UserVerified
                                      THEN tmdb_episode_xrefs.match_rating ELSE excluded.match_rating END,
                    updated_at=excluded.updated_at;
                """,
                new
                {
                    EpisodeNode = localEpisode.episode_node_id,
                    Series = seriesNode,
                    Aid = ownerAnimeId,
                    Eid = episodeId,
                    Show = tmdbShowId,
                    TmdbEpisode = tmdbEpisode.TmdbEpisodeId!.Value,
                    Ordering = orderingId,
                    SeasonId = seasonId,
                    Season = tmdbSeason.SeasonNumber,
                    Episode = tmdbEpisode.EpisodeNumber,
                    Ordinal = tmdbEpisode.Ordinal,
                    Rating = (int)matchRating,
                    UserVerified = (int)VideoMetadataMatchRating.UserVerified,
                    Now = now,
                }, transaction);
        }
    }

    private static int? YearFromProviderDate(string? value) =>
        value?.Length >= 4
        && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;

    private sealed record TypedTmdbEpisodeCandidate(
        VideoMetadataSeason Season,
        VideoMetadataEpisode Episode);

    private async Task<VideoCatalogSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var sources = (await connection.QueryAsync<SourceRow>(
            "SELECT * FROM library_sources ORDER BY created_at,name;")).ToList();
        var sourceRoutes = (await connection.QueryAsync<SourceRouteRow>(
            "SELECT * FROM source_provider_routes ORDER BY source_id,ordinal;")).ToList();
        var assets = (await connection.QueryAsync<AssetRow>(
            """
            SELECT a.*,u.display_title,u.is_favorite,u.bound_subtitle_path,
                u.poster_path AS user_poster_path,u.profile_id,
                (SELECT art.local_path FROM artwork art
                 JOIN node_assets na ON na.node_id=art.node_id
                 WHERE na.asset_id=a.id AND art.kind='poster' AND art.local_path IS NOT NULL
                   AND art.enabled=1 AND art.desired=1
                 ORDER BY
                    CASE WHEN art.id=(SELECT preferred_artwork_id FROM node_user_data WHERE node_id=art.node_id)
                         THEN 0 ELSE 1 END,
                    CASE WHEN art.provider_id='local' THEN 0 WHEN art.selected=1 THEN 1
                         WHEN art.preferred=1 THEN 2 ELSE 3 END,
                    art.ordinal,art.created_at,art.id
                 LIMIT 1) AS catalog_poster_path,
                (EXISTS(SELECT 1 FROM catalog_reset_assets reset
                        WHERE reset.asset_id=a.id)
                 OR EXISTS(
                    SELECT 1 FROM source_assets membership
                    JOIN catalog_reset_sources reset
                      ON reset.source_id=membership.source_id
                    WHERE membership.asset_id=a.id)) AS catalog_reset_pending
            FROM media_assets a LEFT JOIN asset_user_data u ON u.asset_id=a.id
            WHERE a.is_hidden=0 ORDER BY a.imported_at DESC,a.title;
            """)).ToList();
        var sourceAssets = (await connection.QueryAsync<LinkRow>(
            "SELECT source_id AS left_id,asset_id AS right_id FROM source_assets;")).ToList();
        var nodeAssets = (await connection.QueryAsync<LinkRow>(
            "SELECT node_id AS left_id,asset_id AS right_id FROM node_assets ORDER BY ordinal;")).ToList();
        var collectionAssets = (await connection.QueryAsync<LinkRow>(
            "SELECT collection_id AS left_id,asset_id AS right_id FROM collection_assets ORDER BY ordinal;")).ToList();
        var tagRows = (await connection.QueryAsync<TagLinkRow>(
            "SELECT at.asset_id,t.name FROM asset_tags at JOIN tags t ON t.id=at.tag_id ORDER BY t.name;")).ToList();

        var aliasRows = (await connection.QueryAsync<AliasRow>("SELECT * FROM catalog_aliases;")).ToList();
        var externalRows = (await connection.QueryAsync<ExternalIdRow>("SELECT * FROM external_ids;")).ToList();
        var localFieldRows = (await connection.QueryAsync<SnapshotLocalFieldRow>(
            """
            SELECT node_id,field,value FROM metadata_field_values
            WHERE provider_id='local' AND value IS NOT NULL AND TRIM(value)<>'';
            """)).ToList();
        var metadataRows = (await connection.QueryAsync<MetadataSnapshotRow>(
            "SELECT node_id,provider_id,payload_json,source_url,fetched_at,expires_at FROM metadata_snapshots;")).ToList();
        var tmdbShowXrefRows = (await connection.QueryAsync<TmdbShowXrefRow>(
            "SELECT * FROM tmdb_show_xrefs;")).ToList();
        var tmdbEpisodeXrefRows = (await connection.QueryAsync<TmdbEpisodeXrefRow>(
            "SELECT * FROM tmdb_episode_xrefs;")).ToList();
        var tmdbOrderingRows = (await connection.QueryAsync<TmdbOrderingRow>(
            "SELECT * FROM tmdb_orderings;")).ToList();
        var artworkRows = (await connection.QueryAsync<ArtworkSnapshotRow>(
            """
            SELECT a.*,
                CASE WHEN u.preferred_artwork_id=a.id THEN 1 ELSE 0 END AS user_preferred
            FROM artwork a LEFT JOIN node_user_data u ON u.node_id=a.node_id;
            """)).ToList();
        var nodeRows = (await connection.QueryAsync<NodeRow>(
            """
            SELECT n.*,
                (SELECT MAX(ms.expires_at) FROM metadata_snapshots ms WHERE ms.node_id=n.id)
                    AS metadata_expires_at
            FROM catalog_nodes n;
            """)).ToList();
        var nodes = nodeRows.Select(row =>
        {
            var nodeLocalFields = localFieldRows
                .Where(item => item.node_id == row.id)
                .ToDictionary(item => item.field, item => item.value, StringComparer.OrdinalIgnoreCase);
            var nodeMetadataRows = metadataRows.Where(item => item.node_id == row.id).ToList();
            var parsedMetadata = nodeMetadataRows
                .Select(item => new
                {
                    Row = item,
                    Details = TryDeserializeMetadataDetails(item.payload_json),
                })
                .Where(item => item.Details != null)
                .ToList();
            var details = parsedMetadata
                .OrderByDescending(item => ParseDate(item.Row.fetched_at))
                .Select(item => item.Details)
                .FirstOrDefault();
            var persistedSeasons = parsedMetadata
                .Where(item => item.Details is { Seasons.IsDefaultOrEmpty: false })
                .OrderByDescending(item => item.Details!.Seasons.Length)
                .ThenByDescending(item => item.Details!.Seasons.Sum(season =>
                    season.Episodes.IsDefault ? season.EpisodeCount ?? 0 : season.Episodes.Length))
                .ThenByDescending(item => ParseDate(item.Row.fetched_at))
                .Select(item => item.Details!.Seasons)
                .FirstOrDefault();
            var sourceUrls = nodeMetadataRows
                .Where(item => !string.IsNullOrWhiteSpace(item.source_url))
                .GroupBy(item => item.provider_id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(group => group.Key, group => group.First().source_url!, StringComparer.OrdinalIgnoreCase);
            var nodeArtwork = artworkRows
                .Where(item => item.node_id == row.id)
                .ToArray();
            string? ArtworkPath(string kind) => nodeArtwork
                .Where(item => item.local_path != null
                               && item.enabled != 0
                               && item.desired != 0
                               && item.kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.user_preferred)
                .ThenByDescending(item => item.selected)
                .ThenByDescending(item => item.preferred)
                .ThenBy(item => item.provider_id == "local" ? 0 : 1)
                .ThenBy(item => item.ordinal)
                .Select(item => item.local_path)
                .FirstOrDefault();
            var localPeople = DeserializeLocalPeople(nodeLocalFields.GetValueOrDefault("people"));
            var peopleSource = localPeople.Length > 0
                ? localPeople
                : details is { People.IsDefault: false } ? details.People : [];
            var people = peopleSource.Length > 0
                ? peopleSource.Select(person => person with
                {
                    LocalImagePath = ArtworkPath($"person:{person.ProviderPersonId}"),
                }).ToImmutableArray()
                : [];
            var relatedItems = details is { RelatedItems.IsDefault: false }
                ? details.RelatedItems.Select(item => item with
                {
                    LocalPosterPath = ArtworkPath($"related:{item.ProviderId}:{item.ProviderItemId}:poster"),
                    LocalBackdropPath = ArtworkPath($"related:{item.ProviderId}:{item.ProviderItemId}:backdrop"),
                }).ToImmutableArray()
                : [];
            return new VideoCatalogNodeSnapshot(
                Guid.Parse(row.id),
                ParseNullableGuid(row.parent_id),
                ParseNodeKind(row.kind),
                row.primary_title,
                row.original_title,
                row.subtitle,
                row.overview,
                row.year,
                row.season_number,
                row.episode_number,
                row.absolute_episode_number,
                row.is_special != 0,
                row.identity_locked != 0 || externalRows.Any(external => external.node_id == row.id && external.is_identity_locked != 0),
                aliasRows.Where(alias => alias.node_id == row.id).Select(alias => alias.alias).ToImmutableArray(),
                externalRows.Where(external => external.node_id == row.id)
                    .ToImmutableDictionary(external => external.provider_id, external => external.external_id, StringComparer.OrdinalIgnoreCase),
                ParseDate(row.metadata_expires_at),
                DeserializeLocalArray(nodeLocalFields.GetValueOrDefault("genres"), details?.Genres ?? []),
                DeserializeLocalArray(nodeLocalFields.GetValueOrDefault("actors"), details?.Actors ?? []),
                sourceUrls,
                ArtworkPath("backdrop"),
                ArtworkPath("poster"),
                ArtworkPath("thumb"),
                ArtworkPath("logo"),
                nodeLocalFields.GetValueOrDefault("tagline") ?? details?.Tagline,
                nodeLocalFields.GetValueOrDefault("officialRating") ?? details?.OfficialRating,
                double.TryParse(
                    nodeLocalFields.GetValueOrDefault("communityRating"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var localRating)
                    ? localRating
                    : details?.CommunityRating,
                details?.EndYear,
                nodeLocalFields.GetValueOrDefault("status") ?? details?.Status,
                DeserializeLocalArray(
                    nodeLocalFields.GetValueOrDefault("tags"),
                    details is { Tags.IsDefault: false } ? details.Tags : []),
                DeserializeLocalArray(
                    nodeLocalFields.GetValueOrDefault("studios"),
                    details is { Studios.IsDefault: false } ? details.Studios : []),
                people,
                relatedItems)
            {
                IdentityLockedProviders = externalRows
                    .Where(external => external.node_id == row.id && external.is_identity_locked != 0)
                    .Select(external => external.provider_id)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                ArtworkCandidates = nodeArtwork
                    .OrderBy(item => item.kind, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.user_preferred)
                    .ThenByDescending(item => item.selected)
                    .ThenByDescending(item => item.preferred)
                    .ThenBy(item => item.ordinal)
                    .ThenBy(item => item.created_at, StringComparer.Ordinal)
                    .Select(item => new VideoCatalogArtworkSnapshot(
                        Guid.Parse(item.id),
                        Guid.Parse(item.node_id),
                        item.provider_id,
                        item.kind,
                        item.remote_url,
                        item.local_path,
                        item.language,
                        item.width,
                        item.height,
                        item.attribution_url,
                        item.enabled != 0,
                        item.desired != 0,
                        item.preferred != 0,
                        item.selected != 0,
                        item.user_preferred != 0,
                        item.ordinal,
                        item.download_attempts,
                        item.last_error,
                        ParseDate(item.created_at) ?? DateTimeOffset.UnixEpoch,
                        ParseDate(item.updated_at) ?? ParseDate(item.created_at) ?? DateTimeOffset.UnixEpoch))
                    .ToImmutableArray(),
                Seasons = persistedSeasons.IsDefault ? [] : persistedSeasons,
                TmdbShowCrossReferences = tmdbShowXrefRows
                    .Where(xref => xref.series_node_id == row.id)
                    .OrderBy(xref => xref.anidb_anime_id)
                    .ThenBy(xref => xref.tmdb_show_id)
                    .Select(xref => new VideoTmdbShowCrossReference(
                        Guid.Parse(xref.series_node_id),
                        xref.anidb_anime_id,
                        xref.tmdb_show_id,
                        xref.chosen_ordering_id,
                        (VideoTmdbOrderingType)xref.chosen_ordering_type,
                        (VideoMetadataMatchRating)xref.match_rating,
                        ParseDate(xref.updated_at) ?? DateTimeOffset.UnixEpoch))
                    .ToImmutableArray(),
                TmdbEpisodeCrossReferences = tmdbEpisodeXrefRows
                    .Where(xref => xref.episode_node_id == row.id)
                    .OrderBy(xref => xref.ordering_id, StringComparer.Ordinal)
                    .ThenBy(xref => xref.ordinal)
                    .Select(xref => new VideoTmdbEpisodeCrossReference(
                        Guid.Parse(xref.episode_node_id),
                        Guid.Parse(xref.series_node_id),
                        xref.anidb_anime_id,
                        xref.anidb_episode_id,
                        xref.tmdb_show_id,
                        xref.tmdb_episode_id,
                        xref.ordering_id,
                        xref.season_id,
                        xref.season_number,
                        xref.episode_number,
                        xref.ordinal,
                        (VideoMetadataMatchRating)xref.match_rating,
                        ParseDate(xref.updated_at) ?? DateTimeOffset.UnixEpoch))
                    .ToImmutableArray(),
                TmdbOrderings = tmdbOrderingRows
                    .Where(ordering => ordering.series_node_id == row.id)
                    .OrderByDescending(ordering => ordering.is_user_preferred)
                    .ThenByDescending(ordering => ordering.is_preferred)
                    .ThenBy(ordering => ordering.ordering_id, StringComparer.Ordinal)
                    .Select(ordering => new VideoTmdbOrderingSnapshot(
                        Guid.Parse(ordering.series_node_id),
                        ordering.tmdb_show_id,
                        ordering.ordering_id,
                        (VideoTmdbOrderingType)ordering.ordering_type,
                        ordering.is_preferred != 0,
                        ordering.is_user_preferred != 0,
                        ParseDate(ordering.updated_at) ?? DateTimeOffset.UnixEpoch))
                    .ToImmutableArray(),
            };
        })
            .ToImmutableArray();

        var assetSnapshots = assets.Select(row => new VideoCatalogAssetSnapshot(
            Guid.Parse(row.id),
            row.identity_key,
            row.kind == "remote" ? VideoMediaAssetKind.RemoteResource : VideoMediaAssetKind.LocalFile,
            row.location,
            row.title,
            row.parent_folder,
            row.file_size,
            ParseDate(row.modified_at),
            ParseDate(row.imported_at) ?? DateTimeOffset.UnixEpoch,
            ParseDate(row.last_seen_at) ?? DateTimeOffset.UnixEpoch,
            ParseAvailability(row.availability),
            row.episode_start,
            row.episode_end,
            row.provider_id,
            row.remote_id,
            row.original_url,
            row.canonical_url,
            row.remote_thumbnail_url,
            row.remote_subtitle_language,
            row.duration_seconds,
            row.display_title,
            row.is_favorite != 0,
            tagRows.Where(tag => tag.asset_id == row.id).Select(tag => tag.name).ToImmutableArray(),
            row.bound_subtitle_path,
            row.user_poster_path ?? row.catalog_poster_path,
            row.profile_id,
            sourceAssets.Where(link => link.right_id == row.id).Select(link => Guid.Parse(link.left_id)).ToImmutableArray(),
            nodeAssets.Where(link => link.right_id == row.id).Select(link => Guid.Parse(link.left_id)).ToImmutableArray(),
            collectionAssets.Where(link => link.right_id == row.id).Select(link => Guid.Parse(link.left_id)).ToImmutableArray(),
            row.is_hidden != 0,
            row.catalog_reset_pending != 0)).ToImmutableArray();

        var collectionRows = (await connection.QueryAsync<CollectionRow>("SELECT * FROM collections ORDER BY manual_sort_order,name;")).ToList();
        var ruleRows = (await connection.QueryAsync<RuleRow>("SELECT * FROM collection_rules ORDER BY collection_id,ordinal;")).ToList();
        var collections = collectionRows.Select(row => new VideoCatalogCollectionSnapshot(
            Guid.Parse(row.id),
            row.name,
            row.kind == "smart" ? VideoCollectionKind.Smart : VideoCollectionKind.Manual,
            row.manual_sort_order,
            ruleRows.Where(rule => rule.collection_id == row.id)
                .Select(rule => JsonSerializer.Deserialize<VideoSmartRule>(rule.rule_json, JsonOptions) ?? new VideoSmartRule())
                .ToImmutableArray(),
            collectionAssets.Where(link => link.left_id == row.id).Select(link => Guid.Parse(link.right_id)).ToImmutableArray(),
            ParseDate(row.created_at) ?? DateTimeOffset.UnixEpoch,
            ParseDate(row.updated_at) ?? DateTimeOffset.UnixEpoch)).ToImmutableArray();

        var candidates = (await connection.QueryAsync<CandidateRow>("SELECT * FROM match_candidates ORDER BY asset_id,score DESC;")).Select(row =>
            new VideoMatchCandidateSnapshot(
                Guid.Parse(row.id), Guid.Parse(row.asset_id), row.provider_id, row.provider_item_id,
                row.title, row.year, row.score, row.title_score, row.evidence,
                row.hard_conflict != 0, ParseDate(row.created_at) ?? DateTimeOffset.UnixEpoch)).ToImmutableArray();
        var jobs = (await connection.QueryAsync<JobRow>("SELECT * FROM catalog_jobs ORDER BY created_at DESC;")).Select(row =>
            new VideoCatalogJobSnapshot(
                Guid.Parse(row.id), ParseNullableGuid(row.source_id), ParseJobKind(row.kind), ParseJobState(row.state),
                row.generation, row.processed_count, row.total_count, row.error,
                ParseDate(row.created_at) ?? DateTimeOffset.UnixEpoch,
                ParseDate(row.updated_at) ?? DateTimeOffset.UnixEpoch,
                row.matched_count,
                row.needs_review_count,
                row.failed_count)).ToImmutableArray();
        var sourceSnapshots = sources.Select(row => new VideoCatalogSourceSnapshot(
            Guid.Parse(row.id), row.name, row.folder_path, row.normalized_folder_path,
            ParseMediaType(row.media_type), row.language, row.region,
            sourceRoutes.Where(route => route.source_id == row.id && route.enabled != 0)
                .Select(route => route.provider_id).ToImmutableArray(),
            row.scan_generation,
            ParseDate(row.created_at) ?? DateTimeOffset.UnixEpoch,
            ParseDate(row.last_scanned_at), row.last_error)).ToImmutableArray();

        return new VideoCatalogSnapshot(
            VideoCatalogMode.Sqlite,
            sourceSnapshots,
            nodes,
            assetSnapshots,
            collections,
            candidates,
            jobs,
            null,
            DateTimeOffset.UtcNow);
    }

    private async Task ValidateExistingDatabaseAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;");
        if (version != SchemaVersion)
            throw new InvalidDataException($"Unsupported video catalog schema version {version}.");
        var ledger = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM migration_ledger;");
        if (ledger == 0)
            throw new InvalidDataException("Video catalog migration marker is missing.");
        var quick = await connection.ExecuteScalarAsync<string>("PRAGMA quick_check;");
        if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Video catalog quick_check failed: {quick}");
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
    }

    private async Task ApplyCompatibilityRepairsAsync(CancellationToken ct)
    {
        await EnsureMetadataJobCounterColumnsAsync(ct);
        await EnsureArtworkCandidateColumnsAsync(ct);
        await EnsureTypedTmdbCrossReferenceTablesAsync(ct);
        await EnsureCatalogResetAssetsTableAsync(ct);
        await ApplyLocalSidecarCompatibilityRepairAsync(ct);
        await EnsureArtworkIdentityIndexAsync(ct);
        const string category = "anilist-null-id-search-v9";
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var applied = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM migration_audit WHERE category=@Category;",
            new { Category = category }, transaction);
        if (applied != 0)
        {
            await transaction.RollbackAsync(ct);
            await ApplyEpisodicBundleCompatibilityRepairAsync(ct);
            return;
        }
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            UPDATE catalog_jobs
            SET state='cancelled', error='Superseded by AniList null-ID search fix.', updated_at=@Now
            WHERE kind='metadata_refresh' AND state='completed'
              AND source_id IN (
                SELECT DISTINCT sa.source_id
                FROM source_assets sa
                JOIN node_assets na ON na.asset_id=sa.asset_id
                JOIN catalog_nodes episode ON episode.id=na.node_id AND episode.kind='episode'
                WHERE NOT EXISTS (
                    SELECT 1 FROM metadata_snapshots m
                    WHERE m.provider_id IN ('tmdb','anilist','bangumi','tvmaze')
                      AND (m.node_id=episode.id
                           OR m.node_id=episode.parent_id
                           OR m.node_id=(SELECT parent_id FROM catalog_nodes WHERE id=episode.parent_id)))
              );
            DELETE FROM catalog_nodes
            WHERE kind='series'
              AND NOT EXISTS (
                  WITH RECURSIVE descendants(id) AS (
                      SELECT catalog_nodes.id
                      UNION ALL
                      SELECT child.id FROM catalog_nodes child
                      JOIN descendants parent ON child.parent_id=parent.id
                  )
                  SELECT 1 FROM descendants d
                  JOIN catalog_nodes protected ON protected.id=d.id
                  WHERE protected.identity_locked=1
                     OR EXISTS(SELECT 1 FROM node_assets na WHERE na.node_id=d.id)
                     OR EXISTS(SELECT 1 FROM external_ids e WHERE e.node_id=d.id)
                     OR EXISTS(SELECT 1 FROM metadata_snapshots m WHERE m.node_id=d.id)
                     OR EXISTS(SELECT 1 FROM metadata_field_values f WHERE f.node_id=d.id)
                     OR EXISTS(SELECT 1 FROM artwork a WHERE a.node_id=d.id)
                     OR EXISTS(SELECT 1 FROM node_user_data u WHERE u.node_id=d.id)
              );
            INSERT INTO migration_audit(id,category,details_json,created_at)
            VALUES(@Id,@Category,@Details,@Now);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                Category = category,
                Details = "{\"reason\":\"retry AniList title searches without null ID filters and remove empty duplicate scaffolds\"}",
                Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
        await ApplyEpisodicBundleCompatibilityRepairAsync(ct);
    }

    private async Task ApplyLocalSidecarCompatibilityRepairAsync(CancellationToken ct)
    {
        const string category = "local-sidecar-scopes-v12";
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var applied = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM migration_audit WHERE category=@Category;",
            new { Category = category }, transaction);
        if (applied != 0)
        {
            await transaction.RollbackAsync(ct);
            return;
        }

        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            CREATE TEMP TABLE artwork_dedupe_map(
                old_id TEXT PRIMARY KEY,
                keep_id TEXT NOT NULL);
            INSERT INTO artwork_dedupe_map(old_id,keep_id)
            WITH ranked AS (
                SELECT id,
                       FIRST_VALUE(id) OVER (
                           PARTITION BY node_id,provider_id,kind,
                                        COALESCE(local_path,''),COALESCE(remote_url,'')
                           ORDER BY selected DESC,ordinal,created_at,id) AS keep_id,
                       ROW_NUMBER() OVER (
                           PARTITION BY node_id,provider_id,kind,
                                        COALESCE(local_path,''),COALESCE(remote_url,'')
                           ORDER BY selected DESC,ordinal,created_at,id) AS row_number
                FROM artwork)
            SELECT id,keep_id FROM ranked WHERE row_number>1;
            UPDATE node_user_data
            SET preferred_artwork_id=(
                SELECT keep_id FROM artwork_dedupe_map
                WHERE old_id=node_user_data.preferred_artwork_id)
            WHERE preferred_artwork_id IN (SELECT old_id FROM artwork_dedupe_map);
            DELETE FROM artwork WHERE id IN (SELECT old_id FROM artwork_dedupe_map);
            DROP TABLE artwork_dedupe_map;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_artwork_identity_nullsafe
            ON artwork(node_id,provider_id,kind,COALESCE(local_path,''),COALESCE(remote_url,''));
            UPDATE media_assets SET modified_at=NULL WHERE kind='local';
            INSERT INTO migration_audit(id,category,details_json,created_at)
            VALUES(@Id,@Category,@Details,@Now);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                Category = category,
                Details = "{\"reason\":\"deduplicate local artwork and reparse read-only Jellyfin sidecars with scoped metadata\"}",
                Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
    }

    private async Task EnsureMetadataJobCounterColumnsAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var columns = (await connection.QueryAsync<SchemaColumnRow>(
                "PRAGMA table_info(catalog_jobs);"))
            .Select(column => column.name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in new[] { "matched_count", "needs_review_count", "failed_count" }
                     .Where(column => !columns.Contains(column)))
        {
            ct.ThrowIfCancellationRequested();
            await connection.ExecuteAsync(
                $"ALTER TABLE catalog_jobs ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;");
        }
    }

    private async Task EnsureCatalogResetAssetsTableAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS catalog_reset_sources(
                source_id TEXT PRIMARY KEY REFERENCES library_sources(id) ON DELETE CASCADE,
                reset_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_reset_assets(
                asset_id TEXT PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
                reset_at TEXT NOT NULL);
            """);
    }

    private async Task EnsureArtworkCandidateColumnsAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        var columns = (await connection.QueryAsync<SchemaColumnRow>(
                "PRAGMA table_info(artwork);"))
            .Select(column => column.name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new (string Name, string Definition)[]
        {
            ("language", "TEXT NULL"),
            ("width", "INTEGER NULL"),
            ("height", "INTEGER NULL"),
            ("attribution_url", "TEXT NULL"),
            ("enabled", "INTEGER NOT NULL DEFAULT 1"),
            ("desired", "INTEGER NOT NULL DEFAULT 1"),
            ("preferred", "INTEGER NOT NULL DEFAULT 0"),
            ("download_attempts", "INTEGER NOT NULL DEFAULT 0"),
            ("last_error", "TEXT NULL"),
            ("updated_at", "TEXT NULL"),
        };
        var addedPreferred = false;
        foreach (var (name, definition) in additions.Where(item => !columns.Contains(item.Name)))
        {
            ct.ThrowIfCancellationRequested();
            await connection.ExecuteAsync($"ALTER TABLE artwork ADD COLUMN {name} {definition};");
            addedPreferred |= name.Equals("preferred", StringComparison.OrdinalIgnoreCase);
        }
        if (addedPreferred)
            await connection.ExecuteAsync("UPDATE artwork SET preferred=selected;");
        await connection.ExecuteAsync(
            "UPDATE artwork SET updated_at=COALESCE(NULLIF(updated_at,''),created_at);");
    }

    private async Task EnsureArtworkIdentityIndexAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_artwork_identity_nullsafe
            ON artwork(node_id,provider_id,kind,COALESCE(local_path,''),COALESCE(remote_url,''));
            """);
    }

    private async Task EnsureTypedTmdbCrossReferenceTablesAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await connection.ExecuteAsync(TypedTmdbSchemaSql);
        ct.ThrowIfCancellationRequested();
    }

    private async Task ApplyEpisodicBundleCompatibilityRepairAsync(CancellationToken ct)
    {
        // v10 used the model name "local_file" even though media_assets.kind stores "local",
        // so existing installations consumed the audit marker without reparsing a single asset.
        // Use a new marker; never reuse the already-recorded broken repair.
        const string category = "jellyfin-folder-hierarchy-v11";
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var applied = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM migration_audit WHERE category=@Category;",
            new { Category = category }, transaction);
        if (applied != 0)
        {
            await transaction.RollbackAsync(ct);
            return;
        }
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            UPDATE media_assets
            SET modified_at=NULL
            WHERE kind='local';
            UPDATE catalog_nodes
            SET season_number=NULL,episode_number=NULL,absolute_episode_number=NULL
            WHERE kind IN ('series','movie');
            UPDATE catalog_nodes
            SET episode_number=NULL,absolute_episode_number=NULL
            WHERE kind='season';
            INSERT INTO migration_audit(id,category,details_json,created_at)
            VALUES(@Id,@Category,@Details,@Now);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                Category = category,
                Details = "{\"reason\":\"reparse local assets with folder-first series ownership while preserving source media and user state\"}",
                Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await connection.ExecuteAsync(SchemaSql);
        await connection.ExecuteAsync(TypedTmdbSchemaSql);
        ct.ThrowIfCancellationRequested();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct) =>
        await OpenConnectionAsync(_databasePath, ct, enableWal: true);

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string path,
        CancellationToken ct,
        bool enableWal)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            // Short, serialized operations do not benefit from a process-global
            // pool, and disabling it lets removable/test data directories close
            // deterministically while WAL remains enabled.
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync($"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
        if (enableWal)
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        return connection;
    }

    private async Task<FileStream> AcquireMigrationLockAsync(CancellationToken ct)
    {
        var lockPath = _databasePath + ".migration.lock";
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(BusyTimeoutMilliseconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task<VideoLibraryCatalogDocument?> TryReadLegacyLenientAsync(CancellationToken ct)
    {
        var result = await _json.ReadAsync<VideoLibraryCatalogDocument>(_legacyCatalogPath, ct);
        return result.Status == NovelJsonReadStatus.Success ? result.Value : null;
    }

    private static VideoCatalogSnapshot ProjectLegacySnapshot(
        VideoLibraryCatalogDocument? document,
        string error)
    {
        if (document == null)
            return VideoCatalogSnapshot.Empty(VideoCatalogMode.LegacyReadOnly) with { PersistentError = error };
        document.Sources ??= [];
        document.Items ??= [];
        document.RemoteItems ??= [];
        document.ItemMetadataByPath ??= [];
        document.Collections ??= [];
        var sources = document.Sources.Select(source => new VideoCatalogSourceSnapshot(
            source.Id, source.Name, source.Path, source.Path.ToUpperInvariant(),
            VideoLibraryMediaType.Auto, "ja-JP", "JP", [], 0,
            source.CreatedAt ?? DateTimeOffset.UnixEpoch, source.LastScannedAt, source.LastError)).ToImmutableArray();
        var collectionByIdentity = document.Collections
            .SelectMany(collection => collection.ItemPaths.Select(identity => (Identity: NormalizeIdentity(identity), collection.Id)))
            .GroupBy(pair => pair.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Id).ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
        var assets = new List<VideoCatalogAssetSnapshot>();
        foreach (var item in document.Items)
        {
            var identity = NormalizeIdentity(item.Path);
            var metadata = GetLegacyMetadata(document, identity);
            assets.Add(CreateLegacyAsset(
                identity, item.Title, item.ParentFolder, item.FileSize, item.ModifiedAt,
                item.ImportedAt ?? item.LastSeenAt, item.LastSeenAt,
                File.Exists(identity) ? VideoMediaAvailability.Available : VideoMediaAvailability.Unavailable,
                metadata, item.SourceID == Guid.Empty ? [] : [item.SourceID],
                collectionByIdentity.GetValueOrDefault(identity, [])));
        }
        foreach (var remote in document.RemoteItems)
        {
            var identity = $"remote://{remote.Identity.ProviderID}/{remote.Identity.RemoteID}";
            var metadata = GetLegacyMetadata(document, identity);
            assets.Add(CreateLegacyAsset(
                identity, remote.Identity.Title, remote.Identity.ProviderID, 0, null,
                remote.AddedAt, remote.LastResolvedAt, VideoMediaAvailability.Unknown,
                metadata, [], collectionByIdentity.GetValueOrDefault(identity, []),
                remote.Identity));
        }
        var collections = document.Collections.Select(collection => new VideoCatalogCollectionSnapshot(
            collection.Id, collection.Name,
            string.Equals(collection.Kind, "smart", StringComparison.OrdinalIgnoreCase)
                ? VideoCollectionKind.Smart : VideoCollectionKind.Manual,
            0,
            collection.SmartRules.Select(rule => new VideoSmartRule
            {
                Id = rule.Id.ToString("D"),
                Field = Enum.TryParse<VideoSmartRuleField>(rule.Field, true, out var field) ? field : VideoSmartRuleField.FileName,
                Match = Enum.TryParse<VideoSmartRuleMatch>(rule.Match, true, out var match) ? match : VideoSmartRuleMatch.Contains,
                Value = rule.Value,
            }).ToImmutableArray(),
            assets.Where(asset => collection.ItemPaths.Any(path => IdentityEquals(path, asset.IdentityKey)))
                .Select(asset => asset.Id).ToImmutableArray(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)).ToImmutableArray();
        return new VideoCatalogSnapshot(
            VideoCatalogMode.LegacyReadOnly,
            sources,
            [],
            assets.ToImmutableArray(),
            collections,
            [],
            [],
            error,
            DateTimeOffset.UtcNow);
    }

    private static VideoCatalogAssetSnapshot CreateLegacyAsset(
        string identity,
        string title,
        string parent,
        long size,
        DateTimeOffset? modified,
        DateTimeOffset imported,
        DateTimeOffset seen,
        VideoMediaAvailability availability,
        VideoLibraryItemMetadataDocument? metadata,
        ImmutableArray<Guid> sourceIds,
        ImmutableArray<Guid> collectionIds,
        RemoteVideoIdentityDocument? remote = null) =>
        new(
            DeterministicGuid(identity), identity,
            remote == null ? VideoMediaAssetKind.LocalFile : VideoMediaAssetKind.RemoteResource,
            identity, title, parent, size, modified, imported, seen, availability,
            null, null, remote?.ProviderID, remote?.RemoteID, remote?.OriginalURL, remote?.CanonicalURL,
            remote?.ThumbnailURL, null, remote?.Duration, metadata?.DisplayTitle,
            metadata?.IsFavorite == true, (metadata?.Tags ?? []).ToImmutableArray(),
            metadata?.BoundSubtitlePath, metadata?.PosterPath, metadata?.ProfileID,
            sourceIds, [], collectionIds);

    private static async Task ApplyLegacyDisplayTitleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        string? displayTitle)
    {
        if (string.IsNullOrWhiteSpace(displayTitle))
            return;
        await connection.ExecuteAsync(
            "UPDATE asset_user_data SET display_title=@Title WHERE asset_id=@Asset;",
            new { Title = displayTitle, Asset = assetId.ToString("D") }, transaction);
    }

    private static VideoLibraryItemMetadataDocument? GetLegacyMetadata(
        VideoLibraryCatalogDocument document,
        string identity)
    {
        var key = document.ItemMetadataByPath.Keys.FirstOrDefault(key => IdentityEquals(key, identity));
        return key == null ? null : document.ItemMetadataByPath[key];
    }

    private static async Task ReplaceTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        IEnumerable<string> tags)
    {
        await connection.ExecuteAsync(
            "DELETE FROM asset_tags WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
        foreach (var tag in tags.Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var tagId = DeterministicGuid("tag:" + tag.ToUpperInvariant());
            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO tags(id,name,normalized_name) VALUES(@Id,@Name,@Normalized);
                INSERT OR IGNORE INTO asset_tags(asset_id,tag_id) VALUES(@Asset,@Id);
                """,
                new
                {
                    Id = tagId.ToString("D"),
                    Name = tag,
                    Normalized = tag.ToUpperInvariant(),
                    Asset = assetId.ToString("D"),
                }, transaction);
        }
    }

    private static async Task<Guid?> GetAssetIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string identity)
    {
        var value = await connection.ExecuteScalarAsync<string?>(
            "SELECT id FROM media_assets WHERE identity_key=@Identity COLLATE NOCASE;",
            new { Identity = NormalizeIdentity(identity) }, transaction);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private async Task RunQueueAsync()
    {
        await foreach (var operation in _queue.Reader.ReadAllAsync())
            await operation();
    }

    private async Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(async () =>
        {
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
                return;
            }
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, ct);
        return await completion.Task.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
    }

    private static string NormalizeIdentity(string value) => LegacyVideoCatalogReader.NormalizeIdentity(value);
    private static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    private static bool IdentityEquals(string left, string right) =>
        string.Equals(NormalizeIdentity(left), NormalizeIdentity(right), StringComparison.OrdinalIgnoreCase);
    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var id) ? id : Guid.NewGuid();
    private static Guid? ParseNullableGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
    private static Guid DeterministicGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
    private static DateTimeOffset ToOffset(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };
    private static string ToDb(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime() : null;
    private static VideoMetadataDetails? TryDeserializeMetadataDetails(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<VideoMetadataDetails>(payload, JsonOptions)
                ?.WithInitializedCollections();
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static string NormalizeTitle(string value) => string.Concat(
        value.Normalize(System.Text.NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string ToDb(VideoMediaAvailability value) => value switch
    {
        VideoMediaAvailability.Available => "available",
        VideoMediaAvailability.Unavailable => "unavailable",
        _ => "unknown",
    };
    private static VideoMediaAvailability ParseAvailability(string value) => value switch
    {
        "available" => VideoMediaAvailability.Available,
        "unavailable" => VideoMediaAvailability.Unavailable,
        _ => VideoMediaAvailability.Unknown,
    };
    private static string ToDb(VideoCatalogJobKind value) => value switch
    {
        VideoCatalogJobKind.FullScan => "full_scan",
        VideoCatalogJobKind.MetadataRefresh => "metadata_refresh",
        _ => "incremental_scan",
    };
    private static VideoCatalogJobKind ParseJobKind(string value) => value switch
    {
        "full_scan" => VideoCatalogJobKind.FullScan,
        "metadata_refresh" => VideoCatalogJobKind.MetadataRefresh,
        _ => VideoCatalogJobKind.IncrementalScan,
    };
    private static VideoCatalogJobState ParseJobState(string value) => value switch
    {
        "queued" => VideoCatalogJobState.Queued,
        "running" => VideoCatalogJobState.Running,
        "paused" => VideoCatalogJobState.Paused,
        "completed" => VideoCatalogJobState.Completed,
        "cancelled" => VideoCatalogJobState.Cancelled,
        "interrupted" => VideoCatalogJobState.Interrupted,
        _ => VideoCatalogJobState.Failed,
    };
    private static VideoLibraryMediaType ParseMediaType(string value) => value switch
    {
        "anime" => VideoLibraryMediaType.Anime,
        "japanese_drama_tv" => VideoLibraryMediaType.JapaneseDramaTv,
        "movie" => VideoLibraryMediaType.Movie,
        _ => VideoLibraryMediaType.Auto,
    };
    private static string ToDb(VideoLibraryMediaType value) => value switch
    {
        VideoLibraryMediaType.Anime => "anime",
        VideoLibraryMediaType.JapaneseDramaTv => "japanese_drama_tv",
        VideoLibraryMediaType.Movie => "movie",
        _ => "auto",
    };
    private static VideoCatalogNodeKind ParseNodeKind(string value) => value switch
    {
        "movie" => VideoCatalogNodeKind.Movie,
        "series" => VideoCatalogNodeKind.Series,
        "season" => VideoCatalogNodeKind.Season,
        "episode" => VideoCatalogNodeKind.Episode,
        _ => VideoCatalogNodeKind.Unmatched,
    };

    private sealed class SourceRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string folder_path { get; set; } = "";
        public string normalized_folder_path { get; set; } = "";
        public string media_type { get; set; } = "auto";
        public string language { get; set; } = "ja-JP";
        public string region { get; set; } = "JP";
        public long scan_generation { get; set; }
        public string created_at { get; set; } = "";
        public string? last_scanned_at { get; set; }
        public string? last_error { get; set; }
    }
    private sealed class SourceRouteRow { public string source_id { get; set; } = ""; public string provider_id { get; set; } = ""; public int ordinal { get; set; } public int enabled { get; set; } }
    private sealed class LinkRow { public string left_id { get; set; } = ""; public string right_id { get; set; } = ""; }
    private sealed class TagLinkRow { public string asset_id { get; set; } = ""; public string name { get; set; } = ""; }
    private sealed class AliasRow { public string node_id { get; set; } = ""; public string alias { get; set; } = ""; }
    private sealed class ExternalIdRow { public string node_id { get; set; } = ""; public string provider_id { get; set; } = ""; public string external_id { get; set; } = ""; public int is_identity_locked { get; set; } }
    private sealed class MetadataFieldValueRow { public string field { get; set; } = ""; public string value { get; set; } = ""; public int priority { get; set; } public int is_locked { get; set; } public string updated_at { get; set; } = ""; }
    private sealed class LocalFieldRow { public string field { get; set; } = ""; public string value { get; set; } = ""; }
    private sealed class SnapshotLocalFieldRow { public string node_id { get; set; } = ""; public string field { get; set; } = ""; public string value { get; set; } = ""; }
    private sealed class PreservedFieldRow { public string node_id { get; set; } = ""; public string field { get; set; } = ""; public string value { get; set; } = ""; public string provider_id { get; set; } = ""; public int priority { get; set; } public int is_locked { get; set; } public string updated_at { get; set; } = ""; }
    private sealed class ScrapeResetAssetRow { public string asset_id { get; set; } = ""; public string kind { get; set; } = "local"; public string location { get; set; } = ""; public string title { get; set; } = ""; public string? source_id { get; set; } public string? folder_path { get; set; } public string? media_type { get; set; } }
    private sealed class LocalStructuralNodeRow { public string id { get; set; } = ""; public string kind { get; set; } = ""; public int? season_number { get; set; } public int? episode_number { get; set; } public int? absolute_episode_number { get; set; } public int is_special { get; set; } public string? local_scope { get; set; } public string? local_season_number { get; set; } public string? local_episode_number { get; set; } public string? local_absolute_episode_number { get; set; } public string? local_is_special { get; set; } public int has_local_provenance { get; set; } }
    private sealed class LocalNodeScalarRow { public string kind { get; set; } = ""; public string primary_title { get; set; } = ""; public string? original_title { get; set; } public string? overview { get; set; } public int? year { get; set; } public int? season_number { get; set; } public int? episode_number { get; set; } public int? absolute_episode_number { get; set; } public int is_special { get; set; } }
    private sealed class LocalArtworkRow { public string id { get; set; } = ""; public string kind { get; set; } = ""; public string? local_path { get; set; } public int selected { get; set; } public int ordinal { get; set; } }
    private sealed class MetadataSnapshotRow
    {
        public string node_id { get; set; } = ""; public string provider_id { get; set; } = "";
        public string payload_json { get; set; } = ""; public string? source_url { get; set; }
        public string fetched_at { get; set; } = ""; public string expires_at { get; set; } = "";
    }
    private sealed class ArtworkSnapshotRow
    {
        public string id { get; set; } = ""; public string node_id { get; set; } = "";
        public string provider_id { get; set; } = ""; public string kind { get; set; } = "";
        public string? remote_url { get; set; } public string? local_path { get; set; }
        public string? language { get; set; } public int? width { get; set; } public int? height { get; set; }
        public string? attribution_url { get; set; } public int enabled { get; set; } = 1;
        public int desired { get; set; } = 1; public int preferred { get; set; }
        public int selected { get; set; } public int user_preferred { get; set; }
        public int ordinal { get; set; } public int download_attempts { get; set; }
        public string? last_error { get; set; } public string created_at { get; set; } = "";
        public string? updated_at { get; set; }
    }
    private sealed class NodeRow
    {
        public string id { get; set; } = ""; public string? parent_id { get; set; } public string kind { get; set; } = "unmatched";
        public string primary_title { get; set; } = ""; public string? original_title { get; set; } public string? subtitle { get; set; }
        public string? overview { get; set; } public int? year { get; set; } public int? season_number { get; set; }
        public int? episode_number { get; set; } public int? absolute_episode_number { get; set; } public int is_special { get; set; }
        public int identity_locked { get; set; }
        public string? metadata_expires_at { get; set; }
    }
    private sealed class AssetRow
    {
        public string id { get; set; } = ""; public string identity_key { get; set; } = ""; public string kind { get; set; } = "local";
        public string location { get; set; } = ""; public string title { get; set; } = ""; public string parent_folder { get; set; } = "";
        public long file_size { get; set; } public string? modified_at { get; set; } public string imported_at { get; set; } = "";
        public string last_seen_at { get; set; } = ""; public string availability { get; set; } = "unknown";
        public int? episode_start { get; set; } public int? episode_end { get; set; } public string? provider_id { get; set; }
        public string? remote_id { get; set; } public string? original_url { get; set; } public string? canonical_url { get; set; }
        public string? remote_thumbnail_url { get; set; } public string? remote_subtitle_language { get; set; } public double? duration_seconds { get; set; }
        public int is_hidden { get; set; } public string? display_title { get; set; } public int is_favorite { get; set; }
        public int catalog_reset_pending { get; set; }
        public string? bound_subtitle_path { get; set; } public string? user_poster_path { get; set; } public string? catalog_poster_path { get; set; } public string? profile_id { get; set; }
    }
    private sealed class CollectionRow { public string id { get; set; } = ""; public string name { get; set; } = ""; public string kind { get; set; } = "manual"; public int manual_sort_order { get; set; } public string created_at { get; set; } = ""; public string updated_at { get; set; } = ""; }
    private sealed class RuleRow { public string collection_id { get; set; } = ""; public string rule_json { get; set; } = ""; }
    private sealed class CandidateRow { public string id { get; set; } = ""; public string asset_id { get; set; } = ""; public string provider_id { get; set; } = ""; public string provider_item_id { get; set; } = ""; public string title { get; set; } = ""; public int? year { get; set; } public double score { get; set; } public double title_score { get; set; } public string evidence { get; set; } = ""; public int hard_conflict { get; set; } public string created_at { get; set; } = ""; }
    private sealed class BoundNodeRow { public string id { get; set; } = ""; public string kind { get; set; } = "unmatched"; }
    private sealed class LockedNodeRow { public string id { get; set; } = ""; public string kind { get; set; } = "unmatched"; }
    private sealed class RebuildSeriesRow { public string id { get; set; } = ""; public int preserve { get; set; } }
    private sealed class JobRow { public string id { get; set; } = ""; public string? source_id { get; set; } public string kind { get; set; } = "incremental_scan"; public string state { get; set; } = "queued"; public long generation { get; set; } public int processed_count { get; set; } public int total_count { get; set; } public int matched_count { get; set; } public int needs_review_count { get; set; } public int failed_count { get; set; } public string? error { get; set; } public string created_at { get; set; } = ""; public string updated_at { get; set; } = ""; }
    private sealed class SchemaColumnRow { public string name { get; set; } = ""; }
    private sealed class ProviderCacheRow { public string cache_key { get; set; } = ""; public string provider_id { get; set; } = ""; public string? etag { get; set; } public string? last_modified { get; set; } public byte[]? payload { get; set; } public string? content_type { get; set; } public string fetched_at { get; set; } = ""; public string expires_at { get; set; } = ""; }

    private sealed class TypedAniDbEpisodeRow
    {
        public string episode_node_id { get; set; } = "";
        public string primary_title { get; set; } = "";
        public int? year { get; set; }
        public int? season_number { get; set; }
        public int? episode_number { get; set; }
        public string anidb_episode_id { get; set; } = "";
        public string anidb_anime_id { get; set; } = "";
    }

    private sealed class TmdbShowXrefRow
    {
        public string series_node_id { get; set; } = "";
        public int anidb_anime_id { get; set; }
        public int tmdb_show_id { get; set; }
        public string? chosen_ordering_id { get; set; }
        public int chosen_ordering_type { get; set; }
        public int match_rating { get; set; }
        public string updated_at { get; set; } = "";
    }

    private sealed class TmdbEpisodeXrefRow
    {
        public string episode_node_id { get; set; } = "";
        public string series_node_id { get; set; } = "";
        public int anidb_anime_id { get; set; }
        public int anidb_episode_id { get; set; }
        public int tmdb_show_id { get; set; }
        public int tmdb_episode_id { get; set; }
        public string ordering_id { get; set; } = "";
        public string? season_id { get; set; }
        public int season_number { get; set; }
        public int episode_number { get; set; }
        public int ordinal { get; set; }
        public int match_rating { get; set; }
        public string updated_at { get; set; } = "";
    }

    private sealed class TmdbOrderingRow
    {
        public string series_node_id { get; set; } = "";
        public int tmdb_show_id { get; set; }
        public string ordering_id { get; set; } = "";
        public int ordering_type { get; set; }
        public int is_preferred { get; set; }
        public int is_user_preferred { get; set; }
        public string updated_at { get; set; } = "";
    }

    private const string TypedTmdbSchemaSql = """
        CREATE TABLE IF NOT EXISTS tmdb_show_xrefs(
            series_node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            anidb_anime_id INTEGER NOT NULL, tmdb_show_id INTEGER NOT NULL,
            chosen_ordering_id TEXT NULL, chosen_ordering_type INTEGER NOT NULL DEFAULT -1,
            match_rating INTEGER NOT NULL DEFAULT 5,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY(series_node_id,anidb_anime_id,tmdb_show_id));
        CREATE TABLE IF NOT EXISTS tmdb_orderings(
            series_node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            tmdb_show_id INTEGER NOT NULL, ordering_id TEXT NOT NULL,
            ordering_type INTEGER NOT NULL, is_preferred INTEGER NOT NULL DEFAULT 0,
            is_user_preferred INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY(series_node_id,tmdb_show_id,ordering_id));
        CREATE TABLE IF NOT EXISTS tmdb_episode_xrefs(
            episode_node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            series_node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            anidb_anime_id INTEGER NOT NULL, anidb_episode_id INTEGER NOT NULL,
            tmdb_show_id INTEGER NOT NULL, tmdb_episode_id INTEGER NOT NULL,
            ordering_id TEXT NOT NULL, season_id TEXT NULL,
            season_number INTEGER NOT NULL, episode_number INTEGER NOT NULL,
            ordinal INTEGER NOT NULL DEFAULT 0, match_rating INTEGER NOT NULL DEFAULT 5,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY(episode_node_id,anidb_episode_id,tmdb_show_id,tmdb_episode_id,ordering_id));
        CREATE INDEX IF NOT EXISTS ix_tmdb_show_xrefs_identity
            ON tmdb_show_xrefs(anidb_anime_id,tmdb_show_id);
        CREATE INDEX IF NOT EXISTS ix_tmdb_episode_xrefs_identity
            ON tmdb_episode_xrefs(anidb_episode_id,tmdb_show_id,tmdb_episode_id);
        CREATE INDEX IF NOT EXISTS ix_tmdb_episode_xrefs_series
            ON tmdb_episode_xrefs(series_node_id,ordering_id,ordinal);
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS library_sources(
            id TEXT PRIMARY KEY, name TEXT NOT NULL, folder_path TEXT NOT NULL,
            normalized_folder_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            media_type TEXT NOT NULL, language TEXT NOT NULL, region TEXT NOT NULL,
            scan_generation INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
            last_scanned_at TEXT NULL, last_error TEXT NULL);
        CREATE TABLE IF NOT EXISTS source_provider_routes(
            source_id TEXT NOT NULL REFERENCES library_sources(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, ordinal INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY(source_id,provider_id));
        CREATE TABLE IF NOT EXISTS catalog_nodes(
            id TEXT PRIMARY KEY, parent_id TEXT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            kind TEXT NOT NULL, primary_title TEXT NOT NULL, original_title TEXT NULL,
            subtitle TEXT NULL, overview TEXT NULL, year INTEGER NULL, season_number INTEGER NULL,
            episode_number INTEGER NULL, absolute_episode_number INTEGER NULL,
            is_special INTEGER NOT NULL DEFAULT 0, identity_locked INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS media_assets(
            id TEXT PRIMARY KEY, identity_key TEXT NOT NULL UNIQUE COLLATE NOCASE,
            kind TEXT NOT NULL, location TEXT NOT NULL, title TEXT NOT NULL, parent_folder TEXT NOT NULL,
            file_size INTEGER NOT NULL, modified_at TEXT NULL, imported_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL, availability TEXT NOT NULL, episode_start INTEGER NULL,
            episode_end INTEGER NULL, provider_id TEXT NULL, remote_id TEXT NULL, original_url TEXT NULL,
            canonical_url TEXT NULL, remote_thumbnail_url TEXT NULL, remote_subtitle_language TEXT NULL,
            duration_seconds REAL NULL, is_hidden INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS source_assets(
            source_id TEXT NOT NULL REFERENCES library_sources(id) ON DELETE CASCADE,
            asset_id TEXT NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
            last_seen_generation INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(source_id,asset_id));
        CREATE TABLE IF NOT EXISTS catalog_reset_sources(
            source_id TEXT PRIMARY KEY REFERENCES library_sources(id) ON DELETE CASCADE,
            reset_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_reset_assets(
            asset_id TEXT PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
            reset_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS node_assets(
            node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            asset_id TEXT NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
            is_preferred INTEGER NOT NULL DEFAULT 0, ordinal INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(node_id,asset_id));
        CREATE TABLE IF NOT EXISTS external_ids(
            node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, external_id TEXT NOT NULL,
            is_identity_locked INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(node_id,provider_id));
        CREATE TABLE IF NOT EXISTS catalog_aliases(
            node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, alias TEXT NOT NULL, normalized_alias TEXT NOT NULL,
            PRIMARY KEY(node_id,provider_id,alias));
        CREATE TABLE IF NOT EXISTS metadata_snapshots(
            id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, provider_item_id TEXT NOT NULL, payload_json TEXT NOT NULL,
            source_url TEXT NULL, etag TEXT NULL, last_modified TEXT NULL,
            fetched_at TEXT NOT NULL, expires_at TEXT NOT NULL, last_error TEXT NULL,
            UNIQUE(node_id,provider_id));
        CREATE TABLE IF NOT EXISTS metadata_field_values(
            node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            field TEXT NOT NULL, value TEXT NULL, provider_id TEXT NOT NULL,
            priority INTEGER NOT NULL, is_locked INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL,
            PRIMARY KEY(node_id,field,provider_id));
        CREATE TABLE IF NOT EXISTS artwork(
            id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, kind TEXT NOT NULL, remote_url TEXT NULL, local_path TEXT NULL,
            etag TEXT NULL, last_modified TEXT NULL, language TEXT NULL,
            width INTEGER NULL, height INTEGER NULL, attribution_url TEXT NULL,
            enabled INTEGER NOT NULL DEFAULT 1, desired INTEGER NOT NULL DEFAULT 1,
            preferred INTEGER NOT NULL DEFAULT 0, selected INTEGER NOT NULL DEFAULT 0,
            ordinal INTEGER NOT NULL DEFAULT 0, download_attempts INTEGER NOT NULL DEFAULT 0,
            last_error TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT '',
            UNIQUE(node_id,provider_id,kind,local_path,remote_url));
        CREATE TABLE IF NOT EXISTS asset_user_data(
            asset_id TEXT PRIMARY KEY REFERENCES media_assets(id) ON DELETE CASCADE,
            display_title TEXT NULL, is_favorite INTEGER NOT NULL DEFAULT 0,
            bound_subtitle_path TEXT NULL, poster_path TEXT NULL, profile_id TEXT NULL,
            updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS node_user_data(
            node_id TEXT PRIMARY KEY REFERENCES catalog_nodes(id) ON DELETE CASCADE,
            is_favorite INTEGER NOT NULL DEFAULT 0, preferred_asset_id TEXT NULL REFERENCES media_assets(id),
            preferred_artwork_id TEXT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS tags(
            id TEXT PRIMARY KEY, name TEXT NOT NULL, normalized_name TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS asset_tags(
            asset_id TEXT NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
            tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY(asset_id,tag_id));
        CREATE TABLE IF NOT EXISTS collections(
            id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL,
            manual_sort_order INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS collection_rules(
            id TEXT PRIMARY KEY, collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL, rule_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS collection_assets(
            collection_id TEXT NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
            asset_id TEXT NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(collection_id,asset_id));
        CREATE TABLE IF NOT EXISTS match_candidates(
            id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES media_assets(id) ON DELETE CASCADE,
            provider_id TEXT NOT NULL, provider_item_id TEXT NOT NULL, title TEXT NOT NULL,
            year INTEGER NULL, score REAL NOT NULL, title_score REAL NOT NULL,
            evidence TEXT NOT NULL, hard_conflict INTEGER NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS catalog_jobs(
            id TEXT PRIMARY KEY, source_id TEXT NULL REFERENCES library_sources(id) ON DELETE SET NULL,
            kind TEXT NOT NULL, state TEXT NOT NULL, generation INTEGER NOT NULL,
            processed_count INTEGER NOT NULL, total_count INTEGER NOT NULL,
            matched_count INTEGER NOT NULL DEFAULT 0, needs_review_count INTEGER NOT NULL DEFAULT 0,
            failed_count INTEGER NOT NULL DEFAULT 0,
            error TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS provider_cache(
            cache_key TEXT PRIMARY KEY, provider_id TEXT NOT NULL, etag TEXT NULL,
            last_modified TEXT NULL, payload BLOB NULL, content_type TEXT NULL,
            fetched_at TEXT NOT NULL, expires_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS migration_ledger(
            id TEXT PRIMARY KEY, schema_version INTEGER NOT NULL, legacy_path TEXT NOT NULL,
            legacy_sha256 TEXT NULL, counts_json TEXT NOT NULL, completed_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS migration_audit(
            id TEXT PRIMARY KEY, category TEXT NOT NULL, details_json TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_media_assets_availability ON media_assets(availability,is_hidden);
        CREATE INDEX IF NOT EXISTS ix_catalog_nodes_parent ON catalog_nodes(parent_id,kind);
        CREATE INDEX IF NOT EXISTS ix_match_candidates_asset_score ON match_candidates(asset_id,score DESC);
        CREATE INDEX IF NOT EXISTS ix_catalog_jobs_source_state ON catalog_jobs(source_id,state);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_artwork_identity_nullsafe
        ON artwork(node_id,provider_id,kind,COALESCE(local_path,''),COALESCE(remote_url,''));
        """;
}
