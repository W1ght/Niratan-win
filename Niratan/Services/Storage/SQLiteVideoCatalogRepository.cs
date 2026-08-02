using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
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
        WriteAsync(async (connection, transaction) =>
        {
            var desiredKind = ownerKind switch
            {
                VideoMetadataMediaKind.Movie => "movie",
                VideoMetadataMediaKind.Season => "season",
                VideoMetadataMediaKind.Episode => "episode",
                _ => "series",
            };
            var nodeId = await connection.ExecuteScalarAsync<string?>(
                """
                WITH RECURSIVE ancestry(id,parent_id,kind,depth) AS (
                    SELECT n.id,n.parent_id,n.kind,0 FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id WHERE na.asset_id=@Asset
                    UNION ALL
                    SELECT parent.id,parent.parent_id,parent.kind,child.depth+1
                    FROM catalog_nodes parent JOIN ancestry child ON child.parent_id=parent.id
                )
                SELECT id FROM ancestry
                ORDER BY CASE WHEN kind=@DesiredKind THEN 0 ELSE 1 END,depth
                LIMIT 1;
                """,
                new { Asset = assetId.ToString("D"), DesiredKind = desiredKind }, transaction);
            if (nodeId == null)
                return;
            var hasSelected = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM artwork WHERE node_id=@Node AND kind=@Kind AND selected=1;",
                new { Node = nodeId, Kind = kind }, transaction) > 0;
            await connection.ExecuteAsync(
                """
                INSERT INTO artwork(id,node_id,provider_id,kind,remote_url,local_path,etag,last_modified,selected,ordinal,created_at)
                VALUES(@Id,@Node,@Provider,@Kind,@Url,@Path,@ETag,@LastModified,@Selected,0,@Now)
                ON CONFLICT(node_id,provider_id,kind,local_path,remote_url) DO UPDATE SET
                    etag=excluded.etag,last_modified=excluded.last_modified;
                """,
                new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Node = nodeId,
                    Provider = providerId,
                    Kind = kind,
                    Url = remoteUrl,
                    Path = localPath,
                    ETag = etag,
                    LastModified = lastModified.HasValue ? ToDb(lastModified.Value) : null,
                    Selected = hasSelected ? 0 : 1,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }, ct);

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
            await EnsureWritableCoreAsync(ct);
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var generation = await connection.ExecuteScalarAsync<long>(
                "UPDATE library_sources SET scan_generation=scan_generation+1 WHERE id=@Id RETURNING scan_generation;",
                new { Id = sourceId.ToString("D") }, transaction);
            if (generation <= 0)
                throw new KeyNotFoundException("Video source was not found.");
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
                    SourceId = sourceId.ToString("D"),
                    Kind = ToDb(kind),
                    Generation = generation,
                    Now = now,
                }, transaction);
            await transaction.CommitAsync(ct);
            _lastSnapshot = await ReadSnapshotAsync(ct);
            return generation;
        }, ct);

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

            foreach (var item in batch.Assets)
            {
                var upsert = item.Asset with { SourceId = batch.SourceId };
                var assetId = await UpsertAssetCoreAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    upsert,
                    batch.ExpectedGeneration);
                await ApplyParsedIdentityAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    assetId,
                    batch.SourceId,
                    item.ParsedIdentity,
                    item.LocalMetadata,
                    applyMetadata: !item.SkipMetadataProcessing);
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

    public Task ApplyMetadataMatchAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        VideoMetadataDetails? details,
        bool lockIdentity,
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
                  AND (a.identity_locked=1 OR e.is_identity_locked=1)
                ORDER BY CASE a.kind WHEN 'series' THEN 0 WHEN 'movie' THEN 0 WHEN 'season' THEN 1 ELSE 2 END
                LIMIT 1;
                """,
                new
                {
                    Asset = assetId.ToString("D"),
                    Provider = metadata.ProviderId,
                    ExternalId = metadata.ProviderItemId,
                }, transaction);
            if (lockedTarget != null)
            {
                await ApplyNodeMetadataAsync(
                    connection,
                    transaction,
                    Guid.Parse(lockedTarget.id),
                    lockedTarget.kind,
                    metadata,
                    lockIdentity: true);
                await connection.ExecuteAsync(
                    "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                    new { Asset = assetId.ToString("D") }, transaction);
                return;
            }

            var targetNodes = new List<Guid>();
            if (metadata.MediaKind == VideoMetadataMediaKind.Movie)
            {
                var nodeId = currentNodes.Count == 1
                    ? Guid.Parse(currentNodes[0])
                    : Guid.NewGuid();
                if (currentNodes.Count != 1)
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
                            Locked = lockIdentity ? 1 : 0,
                            Now = ToDb(DateTimeOffset.UtcNow),
                        }, transaction);
                }
                await ApplyNodeMetadataAsync(connection, transaction, nodeId, "movie", metadata, lockIdentity);
                targetNodes.Add(nodeId);
            }
            else
            {
                var seriesId = await FindOrCreateSeriesNodeAsync(
                    connection, transaction, assetId, metadata, lockIdentity);
                var currentEpisodeTitle = await connection.ExecuteScalarAsync<string?>(
                    """
                    SELECT n.primary_title FROM catalog_nodes n
                    JOIN node_assets na ON na.node_id=n.id
                    WHERE na.asset_id=@Asset AND n.kind='episode'
                    ORDER BY na.ordinal LIMIT 1;
                    """,
                    new { Asset = assetId.ToString("D") }, transaction);
                var episodeStart = assetRow.episode_start ?? metadata.EpisodeNumber;
                var episodeEnd = assetRow.episode_end ?? episodeStart;
                if (!episodeStart.HasValue)
                {
                    targetNodes.Add(seriesId);
                }
                else
                {
                    Guid? seasonId = null;
                    if (metadata.SeasonNumber.HasValue)
                        seasonId = await FindOrCreateSeasonNodeAsync(connection, transaction, seriesId, metadata.SeasonNumber.Value);
                    for (var episode = episodeStart.Value; episode <= episodeEnd.GetValueOrDefault(episodeStart.Value); episode++)
                    {
                        var episodeId = await FindOrCreateEpisodeNodeAsync(
                            connection,
                            transaction,
                            seriesId,
                            seasonId,
                            metadata,
                            episode,
                            episode == episodeStart.Value && !string.IsNullOrWhiteSpace(currentEpisodeTitle)
                                ? currentEpisodeTitle
                                : $"Episode {episode}",
                            lockIdentity,
                            applyMetadata: metadata.MediaKind == VideoMetadataMediaKind.Episode);
                        targetNodes.Add(episodeId);
                    }
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
            foreach (var oldNode in currentNodes)
            {
                await connection.ExecuteAsync(
                    """
                    DELETE FROM catalog_nodes
                    WHERE id=@Node AND kind='unmatched'
                      AND NOT EXISTS(SELECT 1 FROM node_assets WHERE node_id=@Node);
                    """,
                    new { Node = oldNode }, transaction);
            }
            await connection.ExecuteAsync(
                "DELETE FROM match_candidates WHERE asset_id=@Asset;",
                new { Asset = assetId.ToString("D") }, transaction);
        }, ct);

    private static async Task<Guid> FindOrCreateSeriesNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        VideoMetadataDetails metadata,
        bool lockIdentity)
    {
        var existing = await connection.ExecuteScalarAsync<string?>(
            """
            SELECT e.node_id FROM external_ids e JOIN catalog_nodes n ON n.id=e.node_id
            WHERE n.kind='series' AND e.provider_id=@Provider AND e.external_id=@ExternalId LIMIT 1;
            """,
            new { Provider = metadata.ProviderId, ExternalId = metadata.ProviderItemId }, transaction);
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
        var id = Guid.TryParse(existing, out var parsed) ? parsed : Guid.NewGuid();
        if (existing == null)
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
                    Locked = lockIdentity ? 1 : 0,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }
        await ApplyNodeMetadataAsync(connection, transaction, id, "series", metadata, lockIdentity);
        return id;
    }

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
        bool lockIdentity,
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
        var id = Guid.TryParse(existing, out var parsed) ? parsed : Guid.NewGuid();
        if (existing == null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,original_title,subtitle,overview,year,
                    season_number,episode_number,absolute_episode_number,is_special,identity_locked,created_at,updated_at)
                VALUES(@Id,@Parent,'episode',@Title,@Original,@Subtitle,@Overview,@Year,
                    @Season,@Episode,@Absolute,@Special,@Locked,@Now,@Now);
                """,
                new
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
                    Locked = applyMetadata && lockIdentity ? 1 : 0,
                    Now = ToDb(DateTimeOffset.UtcNow),
                }, transaction);
        }
        if (applyMetadata)
        {
            await ApplyNodeMetadataAsync(
                connection,
                transaction,
                id,
                "episode",
                metadata with { EpisodeNumber = episodeNumber },
                lockIdentity);
        }
        return id;
    }

    private static async Task ApplyNodeMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid nodeId,
        string kind,
        VideoMetadataDetails metadata,
        bool lockIdentity)
    {
        var localTitle = await connection.ExecuteScalarAsync<string?>(
            "SELECT value FROM metadata_field_values WHERE node_id=@Node AND field='title' AND provider_id='local';",
            new { Node = nodeId.ToString("D") }, transaction);
        var title = localTitle ?? metadata.Title;
        var now = ToDb(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync(
            """
            UPDATE catalog_nodes SET kind=@Kind,primary_title=@Title,
                original_title=COALESCE(@Original,original_title),subtitle=COALESCE(@Subtitle,subtitle),
                overview=COALESCE(@Overview,overview),year=COALESCE(@Year,year),
                season_number=COALESCE(@Season,season_number),episode_number=COALESCE(@Episode,episode_number),
                absolute_episode_number=COALESCE(@Absolute,absolute_episode_number),
                identity_locked=MAX(identity_locked,@Locked),updated_at=@Now WHERE id=@Node;
            """,
            new
            {
                Node = nodeId.ToString("D"), Kind = kind, Title = title,
                Original = metadata.OriginalTitle, metadata.Subtitle, metadata.Overview, metadata.Year,
                Season = metadata.SeasonNumber, Episode = metadata.EpisodeNumber,
                Absolute = metadata.AbsoluteEpisodeNumber, Locked = lockIdentity ? 1 : 0, Now = now,
            }, transaction);
        var ids = metadata.ExternalIds.SetItem(metadata.ProviderId, metadata.ProviderItemId);
        foreach (var pair in ids)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,@Locked)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET external_id=excluded.external_id,
                    is_identity_locked=MAX(external_ids.is_identity_locked,excluded.is_identity_locked);
                """,
                new
                {
                    Node = nodeId.ToString("D"), Provider = pair.Key, ExternalId = pair.Value,
                    Locked = lockIdentity ? 1 : 0,
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
    }

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
        bool applyMetadata)
    {
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

        var episodeStart = local?.EpisodeNumber ?? parsed.EpisodeStart ?? parsed.AbsoluteEpisodeNumber;
        if (episodeStart.HasValue
            && nodeRows.All(node => string.Equals(node.kind, "unmatched", StringComparison.OrdinalIgnoreCase)))
        {
            await PromoteLocalEpisodeHierarchyAsync(
                connection, transaction, assetId, sourceId, nodeRows, parsed, local, episodeStart.Value);
            return;
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
                    external_id=excluded.external_id,
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
            await ApplyLocalMetadataAsync(connection, transaction, nodeId, local);
    }

    private static async Task PromoteLocalEpisodeHierarchyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        Guid sourceId,
        IReadOnlyList<BoundNodeRow> previousNodes,
        ParsedVideoIdentity parsed,
        LocalVideoMetadata? local,
        int episodeStart)
    {
        var seriesTitle = string.IsNullOrWhiteSpace(parsed.NormalizedTitle)
            ? parsed.FolderTitle ?? parsed.OriginalName
            : parsed.NormalizedTitle;
        var normalizedTitle = NormalizeTitle(seriesTitle);
        var year = local?.Year ?? parsed.Year;
        var existingSeries = await connection.ExecuteScalarAsync<string?>(
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
            WHERE series.kind='series' AND alias.provider_id='filename'
              AND alias.normalized_alias=@NormalizedTitle
              AND COALESCE(series.year,-1)=COALESCE(@Year,-1)
              AND sa.source_id=@SourceId
            ORDER BY series.created_at LIMIT 1;
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
                    OriginalTitle = local?.OriginalTitle,
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
                Alias = seriesTitle,
                NormalizedAlias = normalizedTitle,
            }, transaction);
        foreach (var pair in parsed.ExternalIds)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES(@Node,@Provider,@ExternalId,1)
                ON CONFLICT(node_id,provider_id) DO UPDATE SET
                    external_id=excluded.external_id,is_identity_locked=1;
                """,
                new
                {
                    Node = seriesId.ToString("D"),
                    Provider = pair.Key.ToLowerInvariant(),
                    ExternalId = pair.Value,
                }, transaction);
        }
        if (local != null)
            await ApplyLocalArtworkAsync(connection, transaction, seriesId.ToString("D"), local);

        var seasonNumber = local?.SeasonNumber ?? parsed.SeasonNumber;
        if (!seasonNumber.HasValue && parsed.SpecialKind != ParsedVideoSpecialKind.None)
            seasonNumber = 0;
        Guid? seasonId = seasonNumber.HasValue
            ? await FindOrCreateSeasonNodeAsync(connection, transaction, seriesId, seasonNumber.Value)
            : null;
        var parentId = seasonId ?? seriesId;
        var episodeEnd = Math.Max(episodeStart, parsed.EpisodeEnd ?? episodeStart);
        var targetNodes = new List<Guid>();
        for (var episodeNumber = episodeStart; episodeNumber <= episodeEnd; episodeNumber++)
        {
            var absoluteNumber = parsed.AbsoluteEpisodeNumber.HasValue
                ? parsed.AbsoluteEpisodeNumber + (episodeNumber - episodeStart)
                : local?.AbsoluteEpisodeNumber;
            var existingEpisode = await connection.ExecuteScalarAsync<string?>(
                """
                SELECT id FROM catalog_nodes
                WHERE parent_id=@Parent AND kind='episode' AND episode_number=@Episode
                  AND COALESCE(absolute_episode_number,-1)=COALESCE(@Absolute,-1)
                LIMIT 1;
                """,
                new
                {
                    Parent = parentId.ToString("D"),
                    Episode = episodeNumber,
                    Absolute = absoluteNumber,
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
                        Title = episodeNumber == episodeStart && !string.IsNullOrWhiteSpace(local?.Title)
                            ? local.Title
                            : $"Episode {episodeNumber}",
                        OriginalTitle = episodeNumber == episodeStart ? local?.OriginalTitle : null,
                        Overview = episodeNumber == episodeStart ? local?.Overview : null,
                        Year = year,
                        Season = seasonNumber,
                        Episode = episodeNumber,
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
            if (local != null && episodeNumber == episodeStart)
                await ApplyLocalMetadataAsync(connection, transaction, episodeId.ToString("D"), local);
            targetNodes.Add(episodeId);
        }

        await connection.ExecuteAsync(
            "DELETE FROM node_assets WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
        for (var ordinal = 0; ordinal < targetNodes.Count; ordinal++)
        {
            await connection.ExecuteAsync(
                "INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal) VALUES(@Node,@Asset,1,@Ordinal);",
                new
                {
                    Node = targetNodes[ordinal].ToString("D"),
                    Asset = assetId.ToString("D"),
                    Ordinal = ordinal,
                }, transaction);
        }
        foreach (var previous in previousNodes)
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM catalog_nodes WHERE id=@Node AND kind='unmatched'
                  AND NOT EXISTS(SELECT 1 FROM node_assets WHERE node_id=@Node);
                """,
                new { Node = previous.id }, transaction);
        }
        await connection.ExecuteAsync(
            "DELETE FROM match_candidates WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, transaction);
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

    private static async Task ApplyLocalMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        LocalVideoMetadata local)
    {
        var now = ToDb(DateTimeOffset.UtcNow);
        foreach (var field in new Dictionary<string, string?>
                 {
                     ["title"] = local.Title,
                     ["originalTitle"] = local.OriginalTitle,
                     ["overview"] = local.Overview,
                     ["year"] = local.Year?.ToString(CultureInfo.InvariantCulture),
                 })
        {
            if (string.IsNullOrWhiteSpace(field.Value))
                continue;
            await connection.ExecuteAsync(
                """
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES(@Node,@Field,@Value,'local',300,0,@Now)
                ON CONFLICT(node_id,field,provider_id) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;
                """,
                new { Node = nodeId, Field = field.Key, Value = field.Value, Now = now }, transaction);
        }
        await ApplyLocalArtworkAsync(connection, transaction, nodeId, local);
    }

    private static async Task ApplyLocalArtworkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        LocalVideoMetadata local)
    {
        var now = ToDb(DateTimeOffset.UtcNow);
        var ordinal = 0;
        foreach (var path in local.ArtworkPaths)
        {
            await connection.ExecuteAsync(
                """
                INSERT OR IGNORE INTO artwork(
                    id,node_id,provider_id,kind,local_path,selected,ordinal,created_at)
                VALUES(@Id,@Node,'local',@Kind,@Path,@Selected,@Ordinal,@Now);
                """,
                new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Node = nodeId,
                    Kind = Path.GetFileNameWithoutExtension(path).Contains("fanart", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(path).Contains("backdrop", StringComparison.OrdinalIgnoreCase)
                            ? "backdrop" : "poster",
                    Path = path,
                    Selected = ordinal == 0 ? 1 : 0,
                    Ordinal = ordinal++,
                    Now = now,
                }, transaction);
        }
    }

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
                 ORDER BY CASE WHEN art.provider_id='local' THEN 0 WHEN art.selected=1 THEN 1 ELSE 2 END,art.ordinal
                 LIMIT 1) AS catalog_poster_path
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
        var metadataRows = (await connection.QueryAsync<MetadataSnapshotRow>(
            "SELECT node_id,provider_id,payload_json,source_url,fetched_at,expires_at FROM metadata_snapshots;")).ToList();
        var artworkRows = (await connection.QueryAsync<ArtworkSnapshotRow>(
            "SELECT node_id,provider_id,kind,local_path,selected,ordinal FROM artwork WHERE local_path IS NOT NULL;")).ToList();
        var nodeRows = (await connection.QueryAsync<NodeRow>(
            """
            SELECT n.*,
                (SELECT MAX(ms.expires_at) FROM metadata_snapshots ms WHERE ms.node_id=n.id)
                    AS metadata_expires_at
            FROM catalog_nodes n;
            """)).ToList();
        var nodes = nodeRows.Select(row =>
        {
            var nodeMetadataRows = metadataRows.Where(item => item.node_id == row.id).ToList();
            var details = nodeMetadataRows
                .OrderByDescending(item => ParseDate(item.fetched_at))
                .Select(item => TryDeserializeMetadataDetails(item.payload_json))
                .FirstOrDefault(item => item != null);
            var sourceUrls = nodeMetadataRows
                .Where(item => !string.IsNullOrWhiteSpace(item.source_url))
                .GroupBy(item => item.provider_id, StringComparer.OrdinalIgnoreCase)
                .ToImmutableDictionary(group => group.Key, group => group.First().source_url!, StringComparer.OrdinalIgnoreCase);
            string? ArtworkPath(string kind) => artworkRows
                .Where(item => item.node_id == row.id && item.kind == kind)
                .OrderByDescending(item => item.selected)
                .ThenBy(item => item.provider_id == "local" ? 0 : 1)
                .ThenBy(item => item.ordinal)
                .Select(item => item.local_path)
                .FirstOrDefault();
            var people = details is { People.IsDefault: false }
                ? details.People.Select(person => person with
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
                details?.Genres ?? [],
                details?.Actors ?? [],
                sourceUrls,
                ArtworkPath("backdrop"),
                ArtworkPath("poster"),
                ArtworkPath("thumb"),
                ArtworkPath("logo"),
                details?.Tagline,
                details?.OfficialRating,
                details?.CommunityRating,
                details?.EndYear,
                details?.Status,
                details is { Tags.IsDefault: false } ? details.Tags : [],
                details is { Studios.IsDefault: false } ? details.Studios : [],
                people,
                relatedItems);
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
            row.is_hidden != 0)).ToImmutableArray();

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
                ParseDate(row.updated_at) ?? DateTimeOffset.UnixEpoch)).ToImmutableArray();
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
        const string category = "series-rich-details-routing-v5";
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
            UPDATE catalog_jobs
            SET state='cancelled', error='Superseded by rich series metadata routing upgrade.', updated_at=@Now
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
                  JOIN node_assets na ON na.node_id=d.id
              )
              AND NOT EXISTS (SELECT 1 FROM external_ids e WHERE e.node_id=catalog_nodes.id)
              AND NOT EXISTS (SELECT 1 FROM metadata_snapshots m WHERE m.node_id=catalog_nodes.id)
              AND NOT EXISTS (SELECT 1 FROM artwork a WHERE a.node_id=catalog_nodes.id)
              AND NOT EXISTS (SELECT 1 FROM node_user_data u WHERE u.node_id=catalog_nodes.id);
            INSERT INTO migration_audit(id,category,details_json,created_at)
            VALUES(@Id,@Category,@Details,@Now);
            """,
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                Category = category,
                Details = "{\"reason\":\"retry lightweight title-index matches through rich details providers and remove empty duplicate scaffolds\"}",
                Now = now,
            }, transaction);
        await transaction.CommitAsync(ct);
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await connection.ExecuteAsync(SchemaSql);
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
    private sealed class MetadataSnapshotRow
    {
        public string node_id { get; set; } = ""; public string provider_id { get; set; } = "";
        public string payload_json { get; set; } = ""; public string? source_url { get; set; }
        public string fetched_at { get; set; } = ""; public string expires_at { get; set; } = "";
    }
    private sealed class ArtworkSnapshotRow
    {
        public string node_id { get; set; } = ""; public string provider_id { get; set; } = "";
        public string kind { get; set; } = ""; public string? local_path { get; set; }
        public int selected { get; set; } public int ordinal { get; set; }
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
        public string? bound_subtitle_path { get; set; } public string? user_poster_path { get; set; } public string? catalog_poster_path { get; set; } public string? profile_id { get; set; }
    }
    private sealed class CollectionRow { public string id { get; set; } = ""; public string name { get; set; } = ""; public string kind { get; set; } = "manual"; public int manual_sort_order { get; set; } public string created_at { get; set; } = ""; public string updated_at { get; set; } = ""; }
    private sealed class RuleRow { public string collection_id { get; set; } = ""; public string rule_json { get; set; } = ""; }
    private sealed class CandidateRow { public string id { get; set; } = ""; public string asset_id { get; set; } = ""; public string provider_id { get; set; } = ""; public string provider_item_id { get; set; } = ""; public string title { get; set; } = ""; public int? year { get; set; } public double score { get; set; } public double title_score { get; set; } public string evidence { get; set; } = ""; public int hard_conflict { get; set; } public string created_at { get; set; } = ""; }
    private sealed class BoundNodeRow { public string id { get; set; } = ""; public string kind { get; set; } = "unmatched"; }
    private sealed class LockedNodeRow { public string id { get; set; } = ""; public string kind { get; set; } = "unmatched"; }
    private sealed class JobRow { public string id { get; set; } = ""; public string? source_id { get; set; } public string kind { get; set; } = "incremental_scan"; public string state { get; set; } = "queued"; public long generation { get; set; } public int processed_count { get; set; } public int total_count { get; set; } public string? error { get; set; } public string created_at { get; set; } = ""; public string updated_at { get; set; } = ""; }
    private sealed class ProviderCacheRow { public string cache_key { get; set; } = ""; public string provider_id { get; set; } = ""; public string? etag { get; set; } public string? last_modified { get; set; } public byte[]? payload { get; set; } public string? content_type { get; set; } public string fetched_at { get; set; } = ""; public string expires_at { get; set; } = ""; }

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
            etag TEXT NULL, last_modified TEXT NULL, selected INTEGER NOT NULL DEFAULT 0,
            ordinal INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
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
        """;
}
