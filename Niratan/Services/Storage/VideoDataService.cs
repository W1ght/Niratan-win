using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Novels;
using Niratan.Services.Video;

namespace Niratan.Services.Storage;

/// <summary>
/// Compatibility facade for existing Video view models. Catalog operations are backed exclusively
/// by SQLite; playback state remains in the byte-compatible Niratan JSON history store.
/// </summary>
internal sealed class VideoDataService : IVideoDataService
{
    private readonly IVideoCatalogRepository _catalog;
    private readonly IVideoPlaybackHistoryStore _history;
    private readonly IVideoFileNameParser _parser;

    public VideoDataService(
        IVideoCatalogRepository catalog,
        IVideoPlaybackHistoryStore history,
        IVideoFileNameParser parser)
    {
        _catalog = catalog;
        _history = history;
        _parser = parser;
    }

    internal VideoDataService(
        string legacyCatalogPath,
        string historyPath,
        INiratanJsonFileStore? json = null)
    {
        var store = json ?? new NiratanJsonFileStore();
        var databasePath = Path.ChangeExtension(Path.GetFullPath(legacyCatalogPath), ".sqlite3");
        _catalog = new SQLiteVideoCatalogRepository(
            databasePath,
            legacyCatalogPath,
            store,
            NullLogger<SQLiteVideoCatalogRepository>.Instance);
        _history = new VideoPlaybackHistoryStore(historyPath, store);
        _parser = new VideoFileNameParser();
    }

    public async Task<IReadOnlyList<VideoItem>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        var videos = new List<VideoItem>(snapshot.Assets.Length);
        foreach (var asset in snapshot.Assets)
        {
            ct.ThrowIfCancellationRequested();
            var video = await ToVideoItemAsync(snapshot, asset, ct);
            if (MatchesQuery(video, queryText))
                videos.Add(video);
        }
        return videos
            .OrderByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
            .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        var asset = snapshot.Assets.FirstOrDefault(item => IdentityEquals(item.IdentityKey, videoId));
        return asset == null ? null : await ToVideoItemAsync(snapshot, asset, ct);
    }

    public Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        var key = NormalizeIdentity(string.IsNullOrWhiteSpace(video.FilePath) ? video.Id : video.FilePath);
        var kind = video.IsRemote ? VideoMediaAssetKind.RemoteResource : VideoMediaAssetKind.LocalFile;
        return _catalog.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            key,
            kind,
            key,
            video.Title,
            video.CollectionName ?? ResolveParentFolder(key),
            video.FileSizeBytes,
            ToOffset(video.ModifiedAt),
            ToOffset(video.ImportedAt) ?? DateTimeOffset.UtcNow,
            ToOffset(video.LastSeenAt) ?? DateTimeOffset.UtcNow,
            video.IsRemote || File.Exists(key) ? VideoMediaAvailability.Available : VideoMediaAvailability.Unavailable,
            Guid.TryParse(video.SourceId, out var sourceId) ? sourceId : null,
            ProviderId: video.ProviderId,
            RemoteId: video.RemoteId,
            OriginalUrl: video.OriginalUrl,
            CanonicalUrl: video.CanonicalUrl,
            RemoteThumbnailUrl: video.RemoteThumbnailUrl,
            RemoteSubtitleLanguage: video.RemoteSubtitleLanguage,
            DurationSeconds: video.DurationSeconds > 0 ? video.DurationSeconds : null,
            BoundSubtitlePath: video.SubtitlePath,
            PosterPath: video.PosterPath,
            ProfileId: video.ProfileId,
            Tags: video.Tags,
            IsFavorite: video.IsFavorite), ct);
    }

    public async Task UpdateVideoDetailsAsync(
        string videoId,
        string title,
        string? tags,
        string? subtitlePath,
        CancellationToken ct = default)
    {
        var existing = await GetRequiredAssetAsync(videoId, ct);
        await _catalog.UpdateAssetUserDataAsync(new VideoCatalogUserDataUpdate(
            existing.IdentityKey,
            title.Trim(),
            SplitTags(tags),
            subtitlePath,
            existing.PosterPath,
            existing.ProfileId), ct);
    }

    public Task DeleteVideoAsync(string videoId, CancellationToken ct = default) =>
        _catalog.SetAssetHiddenAsync(videoId, true, ct);

    public async Task DeleteVideosAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        foreach (var videoId in videoIds.Distinct(StringComparer.OrdinalIgnoreCase))
            await _catalog.SetAssetHiddenAsync(videoId, true, ct);
    }

    public Task UpdateVideoLastOpenedAsync(
        string videoId,
        DateTime lastOpenedAt,
        CancellationToken ct = default) =>
        _history.UpdateLastOpenedAsync(videoId, ToOffset(lastOpenedAt) ?? DateTimeOffset.UtcNow, ct);

    public Task SaveVideoProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default) =>
        _history.SaveProgressAsync(videoId, positionSeconds, durationSeconds, ct);

    public Task SaveVideoPlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default) =>
        _history.SaveAsync(videoId, state, ct);

    public async Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        var assetById = snapshot.Assets.ToDictionary(asset => asset.Id);
        return snapshot.Collections.Select(collection => new VideoCollection
        {
            Id = collection.Id.ToString("D"),
            Name = collection.Name,
            Kind = collection.Kind,
            ManualSortOrder = collection.ManualSortOrder,
            SmartRules = collection.SmartRules,
            ItemIds = collection.AssetIds
                .Where(assetById.ContainsKey)
                .Select(assetId => assetById[assetId].IdentityKey)
                .ToList(),
            CreatedAt = collection.CreatedAt.UtcDateTime,
            UpdatedAt = collection.UpdatedAt.UtcDateTime,
        }).ToList();
    }

    public Task UpsertVideoCollectionAsync(VideoCollection collection, CancellationToken ct = default) =>
        _catalog.UpsertCollectionAsync(collection, ct);

    public Task DeleteVideoCollectionAsync(string collectionId, CancellationToken ct = default) =>
        Guid.TryParse(collectionId, out var id)
            ? _catalog.DeleteCollectionAsync(id, ct)
            : Task.CompletedTask;

    public Task SetVideoCollectionItemsAsync(
        string collectionId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default) =>
        Guid.TryParse(collectionId, out var id)
            ? _catalog.SetCollectionAssetsAsync(id, videoIds, ct)
            : Task.CompletedTask;

    public async Task<IReadOnlyList<VideoLibrarySource>> GetVideoLibrarySourcesAsync(CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        return snapshot.Sources.Select(ToSource).ToList();
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        return Guid.TryParse(sourceId, out var id)
            ? snapshot.Sources.Where(source => source.Id == id).Select(ToSource).FirstOrDefault()
            : null;
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceByPathAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        return snapshot.Sources
            .Where(source => string.Equals(source.FolderPath, path, StringComparison.OrdinalIgnoreCase))
            .Select(ToSource)
            .FirstOrDefault();
    }

    public Task UpsertVideoLibrarySourceAsync(VideoLibrarySource source, CancellationToken ct = default) =>
        _catalog.UpsertSourceAsync(source, ct);

    public Task UpdateVideoLibrarySourceScanStateAsync(
        string sourceId,
        DateTime? lastScannedAt,
        string? lastError,
        CancellationToken ct = default) =>
        Guid.TryParse(sourceId, out var id)
            ? _catalog.UpdateSourceScanStateAsync(id, ToOffset(lastScannedAt), lastError, ct)
            : Task.CompletedTask;

    public Task DeleteVideoLibrarySourceAsync(string sourceId, CancellationToken ct = default) =>
        Guid.TryParse(sourceId, out var id)
            ? _catalog.RemoveSourceAsync(id, ct)
            : Task.CompletedTask;

    public async Task ReplaceVideoSourceItemsAsync(
        string sourceId,
        IReadOnlyList<VideoItem> videos,
        DateTime scannedAt,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(sourceId, out var id))
            throw new ArgumentException("Invalid video source id.", nameof(sourceId));
        var source = await GetVideoLibrarySourceAsync(sourceId, ct)
            ?? throw new KeyNotFoundException("Video source was not found.");
        var generation = await _catalog.BeginSourceScanAsync(id, VideoCatalogJobKind.IncrementalScan, ct);
        var assets = videos.Select(video =>
        {
            var identity = NormalizeIdentity(video.FilePath);
            var parsed = _parser.Parse(identity, source.FolderPath, source.MediaType);
            var upsert = new VideoCatalogAssetUpsert(
                identity,
                video.IsRemote ? VideoMediaAssetKind.RemoteResource : VideoMediaAssetKind.LocalFile,
                identity,
                video.Title,
                video.CollectionName ?? ResolveParentFolder(identity),
                video.FileSizeBytes,
                ToOffset(video.ModifiedAt),
                ToOffset(video.ImportedAt) ?? DateTimeOffset.UtcNow,
                ToOffset(video.LastSeenAt) ?? ToOffset(scannedAt) ?? DateTimeOffset.UtcNow,
                VideoMediaAvailability.Available,
                id,
                parsed.EpisodeStart,
                parsed.EpisodeEnd,
                video.ProviderId,
                video.RemoteId,
                video.OriginalUrl,
                video.CanonicalUrl,
                video.RemoteThumbnailUrl,
                video.RemoteSubtitleLanguage,
                video.DurationSeconds > 0 ? video.DurationSeconds : null,
                video.SubtitlePath,
                video.PosterPath,
                video.ProfileId,
                video.Tags,
                video.IsFavorite);
            return new VideoScanAsset(upsert, parsed);
        }).ToList();
        var applied = await _catalog.ApplyScanBatchAsync(new VideoScanBatch(
            id,
            generation,
            ToOffset(scannedAt) ?? DateTimeOffset.UtcNow,
            assets,
            true), ct);
        if (!applied)
            throw new OperationCanceledException("The video scan was superseded by a newer generation.");
    }

    public async Task UpdateVideoFavoriteAsync(
        string videoId,
        bool isFavorite,
        CancellationToken ct = default)
    {
        var existing = await GetRequiredAssetAsync(videoId, ct);
        await _catalog.UpdateAssetUserDataAsync(new VideoCatalogUserDataUpdate(
            existing.IdentityKey,
            existing.DisplayTitle,
            existing.Tags,
            existing.BoundSubtitlePath,
            existing.PosterPath,
            existing.ProfileId,
            isFavorite), ct);
    }

    public Task MarkVideoWatchedAsync(
        string videoId,
        DateTime watchedAt,
        CancellationToken ct = default) =>
        _history.MarkWatchedAsync(videoId, ToOffset(watchedAt) ?? DateTimeOffset.UtcNow, ct);

    public Task ClearVideoProgressAsync(string videoId, CancellationToken ct = default) =>
        _history.ClearProgressAsync(videoId, ct);

    public async Task UpdateVideoProfileIdAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default)
    {
        var existing = await GetRequiredAssetAsync(videoId, ct);
        await _catalog.UpdateAssetUserDataAsync(new VideoCatalogUserDataUpdate(
            existing.IdentityKey,
            existing.DisplayTitle,
            existing.Tags,
            existing.BoundSubtitlePath,
            existing.PosterPath,
            string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim()), ct);
    }

    private async Task<VideoCatalogAssetSnapshot> GetRequiredAssetAsync(string identity, CancellationToken ct)
    {
        var snapshot = await _catalog.GetSnapshotAsync(ct);
        return snapshot.Assets.FirstOrDefault(asset => IdentityEquals(asset.IdentityKey, identity))
            ?? throw new KeyNotFoundException("Video asset was not found.");
    }

    private async Task<VideoItem> ToVideoItemAsync(
        VideoCatalogSnapshot snapshot,
        VideoCatalogAssetSnapshot asset,
        CancellationToken ct)
    {
        var playback = await _history.GetAsync(asset.IdentityKey, ct);
        var sourceId = asset.SourceIds.FirstOrDefault();
        var source = sourceId == Guid.Empty ? null : snapshot.Sources.FirstOrDefault(item => item.Id == sourceId);
        var nodeId = asset.NodeIds.FirstOrDefault();
        var node = nodeId == Guid.Empty ? null : snapshot.Nodes.FirstOrDefault(item => item.Id == nodeId);
        var seriesNode = FindAncestor(snapshot, node, VideoCatalogNodeKind.Series);
        var descriptiveNode = node is { Genres.Length: > 0 } or { Actors.Length: > 0 }
            ? node
            : seriesNode ?? node;
        var item = new VideoItem
        {
            Id = asset.IdentityKey,
            Title = asset.DisplayTitle ?? asset.Title,
            FilePath = asset.Location,
            SubtitlePath = asset.BoundSubtitlePath,
            ImportedAt = asset.ImportedAt.UtcDateTime,
            LastOpenedAt = playback.UpdatedAt?.UtcDateTime,
            LastPositionSeconds = playback.State.PositionSeconds,
            DurationSeconds = playback.State.DurationSeconds > 0
                ? playback.State.DurationSeconds
                : asset.DurationSeconds ?? 0,
            FileSizeBytes = asset.FileSize,
            ModifiedAt = asset.ModifiedAt?.UtcDateTime,
            SourceFolderPath = source?.FolderPath ?? Path.GetDirectoryName(asset.Location),
            SourceId = sourceId == Guid.Empty ? null : sourceId.ToString("D"),
            LastSeenAt = asset.LastSeenAt.UtcDateTime,
            IsAvailable = asset.Availability is VideoMediaAvailability.Available or VideoMediaAvailability.Unknown,
            CatalogAssetId = asset.Id,
            CatalogNodeId = node?.Id,
            CatalogSeriesNodeId = seriesNode?.Id,
            CatalogSeriesTitle = seriesNode?.PrimaryTitle,
            CatalogNodeKind = node?.Kind ?? VideoCatalogNodeKind.Unmatched,
            LibraryMediaType = source?.MediaType ?? VideoLibraryMediaType.Auto,
            OriginalTitle = node?.OriginalTitle ?? seriesNode?.OriginalTitle,
            LocalizedSubtitle = node?.Subtitle ?? seriesNode?.Subtitle,
            Overview = node?.Overview ?? seriesNode?.Overview,
            ReleaseYear = node?.Year ?? seriesNode?.Year,
            SeasonNumber = node?.SeasonNumber,
            EpisodeNumber = node?.EpisodeNumber,
            AbsoluteEpisodeNumber = node?.AbsoluteEpisodeNumber,
            IsSpecialEpisode = node?.IsSpecial == true,
            IdentityLocked = node?.IdentityLocked == true,
            NeedsReview = snapshot.MatchCandidates.Any(candidate => candidate.AssetId == asset.Id),
            IsUnorganized = asset.CollectionIds.Length == 0,
            ExternalIds = node is { ExternalIds.Count: > 0 }
                ? node.ExternalIds
                : seriesNode?.ExternalIds ?? ImmutableDictionary<string, string>.Empty,
            MatchCandidates = snapshot.MatchCandidates.Where(candidate => candidate.AssetId == asset.Id).ToArray(),
            Genres = descriptiveNode?.Genres.IsDefault == false ? descriptiveNode.Genres : [],
            Actors = descriptiveNode?.Actors.IsDefault == false ? descriptiveNode.Actors : [],
            ProviderSourceUrls = descriptiveNode?.ProviderSourceUrls
                                 ?? ImmutableDictionary<string, string>.Empty,
            BackdropPath = node?.BackdropPath ?? seriesNode?.BackdropPath,
            ThumbPath = node?.ThumbPath,
            LogoPath = seriesNode?.LogoPath ?? node?.LogoPath,
            SeriesPosterPath = seriesNode?.PosterPath,
            SeriesThumbPath = seriesNode?.ThumbPath ?? seriesNode?.BackdropPath,
            Tagline = descriptiveNode?.Tagline,
            OfficialRating = descriptiveNode?.OfficialRating,
            CommunityRating = descriptiveNode?.CommunityRating,
            EndYear = descriptiveNode?.EndYear,
            SeriesStatus = descriptiveNode?.Status,
            MetadataTags = descriptiveNode?.Tags.IsDefault == false ? descriptiveNode.Tags : [],
            Studios = descriptiveNode?.Studios.IsDefault == false ? descriptiveNode.Studios : [],
            People = descriptiveNode?.People.IsDefault == false ? descriptiveNode.People : [],
            RelatedItems = descriptiveNode?.RelatedItems.IsDefault == false ? descriptiveNode.RelatedItems : [],
            PosterPath = asset.PosterPath ?? node?.PosterPath ?? seriesNode?.PosterPath,
            Tags = asset.Tags.Length == 0 ? null : string.Join(", ", asset.Tags),
            CollectionName = asset.ParentFolder,
            IsFavorite = asset.IsFavorite,
            IsWatched = playback.IsFinished,
            ProfileId = asset.ProfileId,
            ProviderId = asset.ProviderId,
            RemoteId = asset.RemoteId,
            OriginalUrl = asset.OriginalUrl,
            CanonicalUrl = asset.CanonicalUrl,
            RemoteThumbnailUrl = asset.RemoteThumbnailUrl,
            RemoteSubtitleLanguage = asset.RemoteSubtitleLanguage,
            SubtitleDelayMilliseconds = playback.State.SubtitleDelayMilliseconds,
            PlaybackSpeed = playback.State.PlaybackSpeed,
            AudioDelaySeconds = playback.State.AudioDelaySeconds,
            AudioSelectionKind = playback.State.AudioSelection?.Kind ?? VideoAudioSelectionKind.None,
            AudioSelectionTrackId = playback.State.AudioSelection?.TrackId,
            AudioSelectionFfIndex = playback.State.AudioSelection?.FfIndex,
            AudioSelectionTitle = playback.State.AudioSelection?.Title,
            AudioSelectionLanguage = playback.State.AudioSelection?.Language,
            AudioSelectionCodec = playback.State.AudioSelection?.Codec,
        };
        item.SetSubtitleSelection(playback.State.SubtitleSelection);
        if (playback.State.SubtitleSelection.Kind == VideoSubtitleSelectionKind.None
            && !string.IsNullOrWhiteSpace(asset.RemoteSubtitleLanguage))
        {
            item.RemoteSubtitleLanguage = asset.RemoteSubtitleLanguage;
            item.SubtitleSelectionKind = VideoSubtitleSelectionKind.RemoteLanguage;
        }
        return item;
    }

    private static VideoCatalogNodeSnapshot? FindAncestor(
        VideoCatalogSnapshot snapshot,
        VideoCatalogNodeSnapshot? node,
        VideoCatalogNodeKind kind)
    {
        while (node != null)
        {
            if (node.Kind == kind)
                return node;
            node = node.ParentId is Guid parentId
                ? snapshot.Nodes.FirstOrDefault(candidate => candidate.Id == parentId)
                : null;
        }
        return null;
    }

    private static VideoLibrarySource ToSource(VideoCatalogSourceSnapshot source) => new()
    {
        Id = source.Id.ToString("D"),
        Name = source.Name,
        FolderPath = source.FolderPath,
        MediaType = source.MediaType,
        Language = source.Language,
        Region = source.Region,
        ProviderOrder = source.ProviderOrder,
        ScanGeneration = source.ScanGeneration,
        CreatedAt = source.CreatedAt.UtcDateTime,
        LastScannedAt = source.LastScannedAt?.UtcDateTime,
        LastError = source.LastError,
    };

    private static bool MatchesQuery(VideoItem video, string? queryText)
    {
        var query = queryText?.Trim();
        return string.IsNullOrWhiteSpace(query)
               || Contains(video.Title, query)
               || Contains(video.FilePath, query)
               || Contains(video.SourceFolderPath, query)
               || Contains(video.Tags, query)
               || Contains(video.CollectionName, query)
               || Contains(video.OriginalUrl, query);
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizeIdentity(string value) => LegacyVideoCatalogReader.NormalizeIdentity(value);
    private static bool IdentityEquals(string left, string right) =>
        string.Equals(NormalizeIdentity(left), NormalizeIdentity(right), StringComparison.OrdinalIgnoreCase);
    private static string ResolveParentFolder(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
    private static DateTimeOffset? ToOffset(DateTime? value) =>
        !value.HasValue ? null : value.Value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value.Value),
            DateTimeKind.Local => new DateTimeOffset(value.Value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)),
        };
    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToImmutableArray();
}
