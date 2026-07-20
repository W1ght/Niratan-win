using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;

namespace Niratan.Services.Storage;

internal class VideoDataService : IVideoDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public VideoDataService()
        : this($"Data Source={Path.Combine(AppDataHelper.GetDataPath(), "niratan.db")}")
    {
    }

    internal VideoDataService(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<SqliteConnection> GetOpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync("PRAGMA foreign_keys = ON;");
        return connection;
    }

    public async Task<IReadOnlyList<VideoItem>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            SELECT Id, Title, FilePath, SubtitlePath, ImportedAt, LastOpenedAt,
                   LastPositionSeconds, DurationSeconds, ManualSortOrder,
                   FileSizeBytes, ModifiedAt, ThumbnailPath, IsFavorite,
                   SourceFolderPath, SourceId, LastSeenAt, PosterPath, Tags, CollectionName, IsWatched,
                   SubtitleSelectionKind, SubtitleSelectionPath,
                   SubtitleSelectionTrackId, SubtitleSelectionTrackName,
                   ProfileId, ProviderId, RemoteId, OriginalUrl, CanonicalUrl,
                   RemoteThumbnailUrl, RemoteSubtitleLanguage
            FROM VideoItems
            WHERE @QueryText IS NULL
                OR TRIM(@QueryText) = ''
                OR Title LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR FilePath LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR SourceFolderPath LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR Tags LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR CollectionName LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR OriginalUrl LIKE '%' || @QueryText || '%' COLLATE NOCASE
            ORDER BY COALESCE(LastOpenedAt, ImportedAt) DESC, Title ASC;
            """;

        var result = await connection.QueryAsync<VideoItem>(
            new CommandDefinition(
                sql,
                new { QueryText = queryText?.Trim() },
                cancellationToken: ct
            )
        );
        return result.ToList();
    }

    public async Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            SELECT Id, Title, FilePath, SubtitlePath, ImportedAt, LastOpenedAt,
                   LastPositionSeconds, DurationSeconds, ManualSortOrder,
                   FileSizeBytes, ModifiedAt, ThumbnailPath, IsFavorite,
                   SourceFolderPath, SourceId, LastSeenAt, PosterPath, Tags, CollectionName, IsWatched,
                   SubtitleSelectionKind, SubtitleSelectionPath,
                   SubtitleSelectionTrackId, SubtitleSelectionTrackName,
                   ProfileId, ProviderId, RemoteId, OriginalUrl, CanonicalUrl,
                   RemoteThumbnailUrl, RemoteSubtitleLanguage
            FROM VideoItems
            WHERE Id = @VideoId;
            """;

        return await connection.QueryFirstOrDefaultAsync<VideoItem>(
            new CommandDefinition(sql, new { VideoId = videoId }, cancellationToken: ct)
        );
    }

    public async Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            INSERT INTO VideoItems
                (Id, Title, FilePath, SubtitlePath, ImportedAt, LastOpenedAt,
                 LastPositionSeconds, DurationSeconds, ManualSortOrder,
                 FileSizeBytes, ModifiedAt, ThumbnailPath, IsFavorite,
                 SourceFolderPath, SourceId, LastSeenAt, PosterPath, Tags, CollectionName, IsWatched,
                 SubtitleSelectionKind, SubtitleSelectionPath,
                 SubtitleSelectionTrackId, SubtitleSelectionTrackName,
                 ProfileId, ProviderId, RemoteId, OriginalUrl, CanonicalUrl,
                 RemoteThumbnailUrl, RemoteSubtitleLanguage)
            VALUES
                (@Id, @Title, @FilePath, @SubtitlePath, @ImportedAt, @LastOpenedAt,
                 @LastPositionSeconds, @DurationSeconds, @ManualSortOrder,
                 @FileSizeBytes, @ModifiedAt, @ThumbnailPath, @IsFavorite,
                 @SourceFolderPath, @SourceId, @LastSeenAt, @PosterPath, @Tags, @CollectionName, @IsWatched,
                 @SubtitleSelectionKind, @SubtitleSelectionPath,
                 @SubtitleSelectionTrackId, @SubtitleSelectionTrackName,
                 @ProfileId, @ProviderId, @RemoteId, @OriginalUrl, @CanonicalUrl,
                 @RemoteThumbnailUrl, @RemoteSubtitleLanguage)
            ON CONFLICT(FilePath) DO UPDATE SET
                Title = CASE
                    WHEN excluded.SourceId IS NOT NULL AND VideoItems.SourceId IS NOT NULL
                        THEN VideoItems.Title
                    ELSE excluded.Title
                END,
                SubtitlePath = COALESCE(excluded.SubtitlePath, VideoItems.SubtitlePath),
                FileSizeBytes = excluded.FileSizeBytes,
                ModifiedAt = COALESCE(excluded.ModifiedAt, VideoItems.ModifiedAt),
                ThumbnailPath = COALESCE(excluded.ThumbnailPath, VideoItems.ThumbnailPath),
                IsFavorite = CASE
                    WHEN excluded.IsFavorite THEN 1
                    ELSE VideoItems.IsFavorite
                END,
                SourceFolderPath = COALESCE(excluded.SourceFolderPath, VideoItems.SourceFolderPath),
                SourceId = COALESCE(excluded.SourceId, VideoItems.SourceId),
                LastSeenAt = COALESCE(excluded.LastSeenAt, VideoItems.LastSeenAt),
                PosterPath = COALESCE(excluded.PosterPath, VideoItems.PosterPath),
                Tags = COALESCE(VideoItems.Tags, excluded.Tags),
                CollectionName = COALESCE(VideoItems.CollectionName, excluded.CollectionName),
                ProfileId = COALESCE(VideoItems.ProfileId, excluded.ProfileId),
                ProviderId = COALESCE(excluded.ProviderId, VideoItems.ProviderId),
                RemoteId = COALESCE(excluded.RemoteId, VideoItems.RemoteId),
                OriginalUrl = COALESCE(excluded.OriginalUrl, VideoItems.OriginalUrl),
                CanonicalUrl = COALESCE(excluded.CanonicalUrl, VideoItems.CanonicalUrl),
                RemoteThumbnailUrl = COALESCE(excluded.RemoteThumbnailUrl, VideoItems.RemoteThumbnailUrl),
                RemoteSubtitleLanguage = COALESCE(VideoItems.RemoteSubtitleLanguage, excluded.RemoteSubtitleLanguage),
                DurationSeconds = CASE
                    WHEN excluded.DurationSeconds > 0 THEN excluded.DurationSeconds
                    ELSE VideoItems.DurationSeconds
                END;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, video, cancellationToken: ct));
    }

    public async Task UpdateVideoDetailsAsync(
        string videoId,
        string title,
        string? tags,
        string? subtitlePath,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET Title = @Title,
                    Tags = @Tags,
                    SubtitlePath = @SubtitlePath,
                    SubtitleSelectionKind = CASE
                        WHEN @SubtitlePath IS NULL THEN 0
                        ELSE 1
                    END,
                    SubtitleSelectionPath = @SubtitlePath,
                    SubtitleSelectionTrackId = NULL,
                    SubtitleSelectionTrackName = NULL
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId, Title = title, Tags = tags, SubtitlePath = subtitlePath },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string collectionsSql = """
            SELECT Id, Name, Kind, RuleJson, CreatedAt, UpdatedAt, ManualSortOrder
            FROM VideoCollections
            ORDER BY ManualSortOrder, Name;
            """;
        const string itemsSql = """
            SELECT CollectionId, VideoId
            FROM VideoCollectionItems
            ORDER BY ManualSortOrder, VideoId;
            """;

        var collections = (await connection.QueryAsync<VideoCollection>(
            new CommandDefinition(collectionsSql, cancellationToken: ct))).ToList();
        if (collections.Count == 0)
            return collections;

        var memberships = await connection.QueryAsync<VideoCollectionMembershipRow>(
            new CommandDefinition(itemsSql, cancellationToken: ct));
        var itemsByCollectionId = memberships
            .GroupBy(row => row.CollectionId, row => row.VideoId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.ToList());

        foreach (var collection in collections)
        {
            collection.SmartRules = DeserializeSmartRules(collection.RuleJson);
            collection.ItemIds = itemsByCollectionId.GetValueOrDefault(collection.Id, []);
        }

        return collections;
    }

    public async Task UpsertVideoCollectionAsync(VideoCollection collection, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            INSERT INTO VideoCollections
                (Id, Name, Kind, RuleJson, CreatedAt, UpdatedAt, ManualSortOrder)
            VALUES
                (@Id, @Name, @Kind, @RuleJson, @CreatedAt, @UpdatedAt, @ManualSortOrder)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Kind = excluded.Kind,
                RuleJson = excluded.RuleJson,
                UpdatedAt = excluded.UpdatedAt,
                ManualSortOrder = excluded.ManualSortOrder;
            """;

        var ruleJson = SerializeSmartRules(collection.SmartRules);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    collection.Id,
                    collection.Name,
                    collection.Kind,
                    RuleJson = ruleJson,
                    collection.CreatedAt,
                    collection.UpdatedAt,
                    collection.ManualSortOrder,
                },
                cancellationToken: ct));
    }

    public async Task DeleteVideoCollectionAsync(string collectionId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM VideoCollections WHERE Id = @CollectionId;",
                new { CollectionId = collectionId },
                cancellationToken: ct));
    }

    public async Task SetVideoCollectionItemsAsync(
        string collectionId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM VideoCollectionItems WHERE CollectionId = @CollectionId;",
                new { CollectionId = collectionId },
                transaction,
                cancellationToken: ct));

        for (var index = 0; index < videoIds.Count; index++)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO VideoCollectionItems (CollectionId, VideoId, ManualSortOrder)
                    VALUES (@CollectionId, @VideoId, @ManualSortOrder);
                    """,
                    new
                    {
                        CollectionId = collectionId,
                        VideoId = videoIds[index],
                        ManualSortOrder = index,
                    },
                    transaction,
                    cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    public async Task UpdateVideoThumbnailPathAsync(string videoId, string? thumbnailPath, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET ThumbnailPath = @ThumbnailPath
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId, ThumbnailPath = thumbnailPath },
                cancellationToken: ct));
    }

    public async Task UpdateVideoFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET IsFavorite = @IsFavorite
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId, IsFavorite = isFavorite },
                cancellationToken: ct));
    }

    public async Task DeleteVideoAsync(string videoId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM VideoItems WHERE Id = @VideoId;",
                new { VideoId = videoId },
                cancellationToken: ct
            )
        );
    }

    public async Task DeleteVideosAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        if (videoIds.Count == 0)
            return;

        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM VideoItems WHERE Id IN @VideoIds;",
                new { VideoIds = videoIds },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VideoLibrarySource>> GetVideoLibrarySourcesAsync(
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        var result = await connection.QueryAsync<VideoLibrarySource>(
            new CommandDefinition(
                """
                SELECT Id, Name, FolderPath, CreatedAt, LastScannedAt, LastError
                FROM VideoLibrarySources
                ORDER BY Name COLLATE NOCASE, FolderPath COLLATE NOCASE;
                """,
                cancellationToken: ct));
        return result.ToList();
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryFirstOrDefaultAsync<VideoLibrarySource>(
            new CommandDefinition(
                """
                SELECT Id, Name, FolderPath, CreatedAt, LastScannedAt, LastError
                FROM VideoLibrarySources
                WHERE Id = @SourceId;
                """,
                new { SourceId = sourceId },
                cancellationToken: ct));
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceByPathAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        return await connection.QueryFirstOrDefaultAsync<VideoLibrarySource>(
            new CommandDefinition(
                """
                SELECT Id, Name, FolderPath, CreatedAt, LastScannedAt, LastError
                FROM VideoLibrarySources
                WHERE FolderPath = @FolderPath COLLATE NOCASE;
                """,
                new { FolderPath = folderPath },
                cancellationToken: ct));
    }

    public async Task UpsertVideoLibrarySourceAsync(
        VideoLibrarySource source,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO VideoLibrarySources
                    (Id, Name, FolderPath, CreatedAt, LastScannedAt, LastError)
                VALUES
                    (@Id, @Name, @FolderPath, @CreatedAt, @LastScannedAt, @LastError)
                ON CONFLICT(FolderPath) DO UPDATE SET
                    Name = excluded.Name,
                    LastScannedAt = excluded.LastScannedAt,
                    LastError = excluded.LastError;
                """,
                source,
                cancellationToken: ct));
    }

    public async Task UpdateVideoLibrarySourceScanStateAsync(
        string sourceId,
        DateTime? lastScannedAt,
        string? lastError,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoLibrarySources
                SET LastScannedAt = @LastScannedAt,
                    LastError = @LastError
                WHERE Id = @SourceId;
                """,
                new { SourceId = sourceId, LastScannedAt = lastScannedAt, LastError = lastError },
                cancellationToken: ct));
    }

    public async Task DeleteVideoLibrarySourceAsync(string sourceId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM VideoItems WHERE SourceId = @SourceId;",
            new { SourceId = sourceId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM VideoLibrarySources WHERE Id = @SourceId;",
            new { SourceId = sourceId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteSourceVideosExceptAsync(
        string sourceId,
        IReadOnlyList<string> retainedFilePaths,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        var sql = retainedFilePaths.Count == 0
            ? "DELETE FROM VideoItems WHERE SourceId = @SourceId;"
            : "DELETE FROM VideoItems WHERE SourceId = @SourceId AND FilePath NOT IN @RetainedFilePaths;";
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { SourceId = sourceId, RetainedFilePaths = retainedFilePaths },
                cancellationToken: ct));
    }

    public async Task UpdateVideoLastOpenedAsync(
        string videoId,
        DateTime lastOpenedAt,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE VideoItems SET LastOpenedAt = @LastOpenedAt WHERE Id = @VideoId;",
                new { VideoId = videoId, LastOpenedAt = lastOpenedAt },
                cancellationToken: ct
            )
        );
    }

    public async Task SaveVideoProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET LastPositionSeconds = @PositionSeconds,
                    DurationSeconds = @DurationSeconds,
                    IsWatched = CASE
                        WHEN @IsWatched THEN 1
                        ELSE IsWatched
                    END
                WHERE Id = @VideoId;
                """,
                new
                {
                    VideoId = videoId,
                    PositionSeconds = positionSeconds,
                    DurationSeconds = durationSeconds,
                    IsWatched = IsWatchedProgress(positionSeconds, durationSeconds),
                },
                cancellationToken: ct
            )
        );
    }

    public async Task SaveVideoPlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET LastPositionSeconds = @PositionSeconds,
                    DurationSeconds = @DurationSeconds,
                    IsWatched = CASE
                        WHEN @IsWatched THEN 1
                        ELSE IsWatched
                    END,
                    SubtitleSelectionKind = @SubtitleSelectionKind,
                    SubtitleSelectionPath = @SubtitleSelectionPath,
                    SubtitleSelectionTrackId = @SubtitleSelectionTrackId,
                    SubtitleSelectionTrackName = @SubtitleSelectionTrackName,
                    RemoteSubtitleLanguage = @RemoteSubtitleLanguage
                WHERE Id = @VideoId;
                """,
                new
                {
                    VideoId = videoId,
                    state.PositionSeconds,
                    state.DurationSeconds,
                    IsWatched = IsWatchedProgress(state.PositionSeconds, state.DurationSeconds),
                    SubtitleSelectionKind = (int)state.SubtitleSelection.Kind,
                    SubtitleSelectionPath = state.SubtitleSelection.ExternalPath,
                    SubtitleSelectionTrackId = state.SubtitleSelection.TrackId,
                    SubtitleSelectionTrackName = state.SubtitleSelection.TrackName,
                    RemoteSubtitleLanguage = state.SubtitleSelection.RemoteLanguageCode,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task MarkVideoWatchedAsync(
        string videoId,
        DateTime watchedAt,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET IsWatched = 1,
                    LastOpenedAt = @WatchedAt,
                    LastPositionSeconds = CASE
                        WHEN DurationSeconds > 0 THEN DurationSeconds
                        ELSE LastPositionSeconds
                    END
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId, WatchedAt = watchedAt },
                cancellationToken: ct));
    }

    public async Task ClearVideoProgressAsync(
        string videoId,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET LastPositionSeconds = 0,
                    IsWatched = 0
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId },
                cancellationToken: ct));
    }

    public async Task UpdateVideoProfileIdAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE VideoItems
                SET ProfileId = @ProfileId
                WHERE Id = @VideoId;
                """,
                new { VideoId = videoId, ProfileId = profileId },
                cancellationToken: ct));
    }

    private static bool IsWatchedProgress(double positionSeconds, double durationSeconds) =>
        durationSeconds > 0
        && positionSeconds >= durationSeconds * 0.98;

    private static IReadOnlyList<VideoSmartRule> DeserializeSmartRules(string? ruleJson) =>
        string.IsNullOrWhiteSpace(ruleJson)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<VideoSmartRule>>(ruleJson, JsonOptions) ?? [];

    private static string? SerializeSmartRules(IReadOnlyList<VideoSmartRule> smartRules) =>
        smartRules.Count == 0
            ? null
            : JsonSerializer.Serialize(smartRules, JsonOptions);

    private sealed record VideoCollectionMembershipRow(string CollectionId, string VideoId);
}
