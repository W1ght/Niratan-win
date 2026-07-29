using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Novels;

namespace Niratan.Services.Storage;

internal sealed class VideoDataService : IVideoDataService
{
    private static readonly Guid LooseFilesSourceId =
        Guid.Parse("00000000-0000-0000-0000-00000000A11C");

    private readonly INiratanJsonFileStore _json;
    private readonly string _catalogPath;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VideoLibraryCatalogDocument? _catalog;
    private VideoPlaybackHistoryDocument? _history;

    public VideoDataService(INiratanJsonFileStore json)
        : this(
            Path.Combine(AppDataHelper.GetDataPath(), "video_library.json"),
            Path.Combine(AppDataHelper.GetDataPath(), "video_playback_history.json"),
            json)
    {
    }

    internal VideoDataService(
        string catalogPath,
        string historyPath,
        INiratanJsonFileStore? json = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _catalogPath = Path.GetFullPath(catalogPath);
        _historyPath = Path.GetFullPath(historyPath);
        _json = json ?? new NiratanJsonFileStore();
    }

    public async Task<IReadOnlyList<VideoItem>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var videos = EnumerateVideos()
                .Where(video => MatchesQuery(video, queryText))
                .OrderByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
                .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return videos;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            return EnumerateVideos().FirstOrDefault(video => IdentityEquals(video.Id, key));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (video.IsRemote)
                UpsertRemoteVideo(video);
            else
                UpsertLocalVideo(video);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateVideoDetailsAsync(
        string videoId,
        string title,
        string? tags,
        string? subtitlePath,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            var metadata = GetOrCreateMetadata(key);
            metadata.DisplayTitle = title.Trim();
            metadata.Tags = SplitTags(tags);
            metadata.BoundSubtitlePath = NormalizeOptionalPath(subtitlePath);
            RemoveMetadataIfEmpty(key, metadata);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteVideoAsync(string videoId, CancellationToken ct = default) =>
        DeleteVideosAsync([videoId], ct);

    public async Task DeleteVideosAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        if (videoIds.Count == 0)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var keys = videoIds
                .Select(NormalizeIdentityKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            RemoveCatalogItems(keys);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateVideoLastOpenedAsync(
        string videoId,
        DateTime lastOpenedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            if (_history!.PlaybackStates.TryGetValue(key, out var state))
            {
                state.UpdatedAt = ToDateTimeOffset(lastOpenedAt);
                await SaveHistoryAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveVideoProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            var current = CreatePlaybackState(key);
            SavePlaybackStateCore(
                key,
                new VideoPlaybackState(
                    positionSeconds,
                    durationSeconds,
                    current.SubtitleSelection,
                    current.SubtitleDelayMilliseconds,
                    current.PlaybackSpeed,
                    current.AudioDelaySeconds,
                    current.AudioSelection));
            await SaveHistoryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveVideoPlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            SavePlaybackStateCore(NormalizeIdentityKey(videoId), state);
            await SaveHistoryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return _catalog!.Collections
                .Select(ToVideoCollection)
                .OrderBy(collection => collection.ManualSortOrder)
                .ThenBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertVideoCollectionAsync(
        VideoCollection collection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var id = ParseOrCreateGuid(collection.Id);
            var document = _catalog!.Collections.FirstOrDefault(item => item.Id == id);
            if (document == null)
            {
                document = new VideoLibraryCollectionDocument { Id = id };
                _catalog.Collections.Add(document);
            }

            document.Name = collection.Name;
            document.Kind = collection.Kind == VideoCollectionKind.Smart ? "smart" : "manual";
            document.SmartRules = collection.SmartRules.Select(ToSmartRuleDocument).ToList();
            if (collection.Kind == VideoCollectionKind.Smart)
                document.ItemPaths.Clear();
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteVideoCollectionAsync(
        string collectionId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!Guid.TryParse(collectionId, out var id))
                return;

            _catalog!.Collections.RemoveAll(collection => collection.Id == id);
            foreach (var metadata in _catalog.ItemMetadataByPath.Values)
                metadata.CollectionIDs.RemoveAll(collectionID => collectionID == id);
            RemoveEmptyMetadata();
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetVideoCollectionItemsAsync(
        string collectionId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!Guid.TryParse(collectionId, out var id))
                return;

            var collection = _catalog!.Collections.FirstOrDefault(item => item.Id == id);
            if (collection == null)
                return;

            var paths = videoIds
                .Select(NormalizeIdentityKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            collection.ItemPaths = paths;

            foreach (var metadata in _catalog.ItemMetadataByPath.Values)
                metadata.CollectionIDs.RemoveAll(collectionID => collectionID == id);
            foreach (var path in paths)
            {
                var metadata = GetOrCreateMetadata(path);
                if (!metadata.CollectionIDs.Contains(id))
                    metadata.CollectionIDs.Add(id);
            }
            RemoveEmptyMetadata();
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VideoLibrarySource>> GetVideoLibrarySourcesAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return _catalog!.Sources
                .Select(ToVideoLibrarySource)
                .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(source => source.FolderPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return Guid.TryParse(sourceId, out var id)
                ? _catalog!.Sources.Where(source => source.Id == id).Select(ToVideoLibrarySource).FirstOrDefault()
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoLibrarySource?> GetVideoLibrarySourceByPathAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var path = NormalizeLocalPath(folderPath);
            return _catalog!.Sources
                .Where(source => IdentityEquals(source.Path, path))
                .Select(ToVideoLibrarySource)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertVideoLibrarySourceAsync(
        VideoLibrarySource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var path = NormalizeLocalPath(source.FolderPath);
            var document = _catalog!.Sources.FirstOrDefault(item => IdentityEquals(item.Path, path));
            if (document == null)
            {
                document = new VideoLibrarySourceDocument
                {
                    Id = ParseOrCreateGuid(source.Id),
                    Bookmark = [],
                };
                _catalog.Sources.Add(document);
            }

            document.Name = source.Name;
            document.Path = path;
            document.CreatedAt ??= ToDateTimeOffset(source.CreatedAt);
            document.LastScannedAt = ToNullableDateTimeOffset(source.LastScannedAt);
            document.LastError = source.LastError;
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateVideoLibrarySourceScanStateAsync(
        string sourceId,
        DateTime? lastScannedAt,
        string? lastError,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!Guid.TryParse(sourceId, out var id))
                return;

            var source = _catalog!.Sources.FirstOrDefault(item => item.Id == id);
            if (source == null)
                return;
            source.LastScannedAt = ToNullableDateTimeOffset(lastScannedAt);
            source.LastError = lastError;
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteVideoLibrarySourceAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!Guid.TryParse(sourceId, out var id))
                return;

            var removed = _catalog!.Items
                .Where(item => item.SourceID == id)
                .Select(item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _catalog.Items.RemoveAll(item => item.SourceID == id);
            _catalog.Sources.RemoveAll(source => source.Id == id);
            RemoveMetadataAndCollectionReferences(removed);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceVideoSourceItemsAsync(
        string sourceId,
        IReadOnlyList<VideoItem> videos,
        DateTime scannedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!Guid.TryParse(sourceId, out var id))
                throw new InvalidDataException("Video source identity is invalid.");

            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in videos)
            {
                ct.ThrowIfCancellationRequested();
                video.SourceId = sourceId;
                video.LastSeenAt = scannedAt;
                UpsertLocalVideo(video);
                retained.Add(NormalizeLocalPath(video.FilePath));
            }

            var removed = _catalog!.Items
                .Where(item => item.SourceID == id && !retained.Contains(item.Path))
                .Select(item => item.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _catalog.Items.RemoveAll(item => item.SourceID == id && !retained.Contains(item.Path));
            RemoveMetadataAndCollectionReferences(removed);

            var source = _catalog.Sources.FirstOrDefault(item => item.Id == id);
            if (source != null)
            {
                source.LastScannedAt = ToDateTimeOffset(scannedAt);
                source.LastError = null;
            }

            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateVideoFavoriteAsync(
        string videoId,
        bool isFavorite,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            var metadata = GetOrCreateMetadata(key);
            metadata.IsFavorite = isFavorite;
            RemoveMetadataIfEmpty(key, metadata);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkVideoWatchedAsync(
        string videoId,
        DateTime watchedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            var existing = _history!.PlaybackStates.GetValueOrDefault(key);
            var duration = existing?.Duration;
            _history.Positions.Remove(key);
            _history.PlaybackStates[key] = new VideoPlaybackStateDocument
            {
                Position = Math.Max(duration ?? 0, 0),
                Duration = duration,
                UpdatedAt = ToDateTimeOffset(watchedAt),
                IsFinished = true,
                ResumeOptions = new VideoPlaybackResumeOptionsDocument(),
            };
            await SaveHistoryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearVideoProgressAsync(
        string videoId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            _history!.Positions.Remove(key);
            _history.PlaybackStates.Remove(key);
            await SaveHistoryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateVideoProfileIdAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentityKey(videoId);
            var metadata = GetOrCreateMetadata(key);
            metadata.ProfileID = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
            RemoveMetadataIfEmpty(key, metadata);
            await SaveCatalogAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_catalog != null && _history != null)
            return;

        var catalogResult = await _json.ReadAsync<VideoLibraryCatalogDocument>(_catalogPath, ct);
        if (catalogResult.Status == NovelJsonReadStatus.Invalid)
        {
            throw new InvalidDataException(
                $"Video library catalog is invalid and was preserved: {catalogResult.Error}");
        }

        var historyResult = await _json.ReadAsync<VideoPlaybackHistoryDocument>(_historyPath, ct);
        if (historyResult.Status == NovelJsonReadStatus.Invalid)
        {
            throw new InvalidDataException(
                $"Video playback history is invalid and was preserved: {historyResult.Error}");
        }

        _catalog = catalogResult.Value ?? new VideoLibraryCatalogDocument();
        _history = historyResult.Value ?? new VideoPlaybackHistoryDocument();
        NormalizeLoadedDocuments();
    }

    private void NormalizeLoadedDocuments()
    {
        _catalog!.Sources ??= [];
        _catalog.Items ??= [];
        _catalog.RemoteItems ??= [];
        _catalog.ItemMetadataByPath ??= [];
        _catalog.Collections ??= [];
        _history!.Positions ??= [];
        _history.PlaybackStates ??= [];
        _history.SubtitleSelections ??= [];
    }

    private IEnumerable<VideoItem> EnumerateVideos()
    {
        foreach (var item in _catalog!.Items)
            yield return ToVideoItem(item);
        foreach (var remote in _catalog.RemoteItems)
            yield return ToVideoItem(remote);
    }

    private VideoItem ToVideoItem(VideoLibraryItemDocument item)
    {
        var key = NormalizeIdentityKey(item.Path);
        var metadata = GetMetadata(key);
        var playback = CreatePlaybackState(key);
        return new VideoItem
        {
            Id = key,
            Title = metadata?.DisplayTitle ?? item.Title,
            FilePath = key,
            SubtitlePath = metadata?.BoundSubtitlePath,
            ImportedAt = (item.ImportedAt ?? item.LastSeenAt).UtcDateTime,
            LastOpenedAt = GetPlaybackUpdatedAt(key),
            LastPositionSeconds = playback.PositionSeconds,
            DurationSeconds = playback.DurationSeconds,
            FileSizeBytes = item.FileSize,
            ModifiedAt = item.ModifiedAt?.UtcDateTime,
            SourceFolderPath = Path.GetDirectoryName(key),
            SourceId = item.SourceID.ToString("D"),
            LastSeenAt = item.LastSeenAt.UtcDateTime,
            PosterPath = metadata?.PosterPath,
            Tags = JoinTags(metadata?.Tags),
            CollectionName = item.ParentFolder,
            IsFavorite = metadata?.IsFavorite == true,
            IsWatched = IsFinished(key),
            SubtitleSelectionKind = playback.SubtitleSelection.Kind,
            SubtitleSelectionPath = playback.SubtitleSelection.ExternalPath,
            SubtitleSelectionTrackId = playback.SubtitleSelection.TrackId,
            SubtitleSelectionTrackName = playback.SubtitleSelection.TrackName,
            ProfileId = metadata?.ProfileID,
            SubtitleDelayMilliseconds = playback.SubtitleDelayMilliseconds,
            PlaybackSpeed = playback.PlaybackSpeed,
            AudioDelaySeconds = playback.AudioDelaySeconds,
            AudioSelectionKind = playback.AudioSelection?.Kind ?? VideoAudioSelectionKind.None,
            AudioSelectionTrackId = playback.AudioSelection?.TrackId,
            AudioSelectionFfIndex = playback.AudioSelection?.FfIndex,
            AudioSelectionTitle = playback.AudioSelection?.Title,
            AudioSelectionLanguage = playback.AudioSelection?.Language,
            AudioSelectionCodec = playback.AudioSelection?.Codec,
        };
    }

    private VideoItem ToVideoItem(RemoteVideoLibraryItemDocument remote)
    {
        var identity = remote.Identity;
        var key = $"remote://{identity.ProviderID}/{identity.RemoteID}";
        var metadata = GetMetadata(key);
        var playback = CreatePlaybackState(key);
        var subtitleSelection = playback.SubtitleSelection.Kind == VideoSubtitleSelectionKind.None
            && !string.IsNullOrWhiteSpace(remote.SubtitleLanguage)
                ? VideoSubtitleSelection.RemoteLanguage(remote.SubtitleLanguage)
                : playback.SubtitleSelection;
        return new VideoItem
        {
            Id = key,
            Title = metadata?.DisplayTitle ?? identity.Title,
            FilePath = key,
            SubtitlePath = metadata?.BoundSubtitlePath,
            ImportedAt = remote.AddedAt.UtcDateTime,
            LastOpenedAt = GetPlaybackUpdatedAt(key),
            LastPositionSeconds = playback.PositionSeconds,
            DurationSeconds = playback.DurationSeconds > 0
                ? playback.DurationSeconds
                : identity.Duration ?? 0,
            ModifiedAt = remote.LastResolvedAt.UtcDateTime,
            SourceFolderPath = "YouTube Video",
            SourceId = null,
            LastSeenAt = remote.AddedAt.UtcDateTime,
            PosterPath = metadata?.PosterPath,
            Tags = JoinTags(metadata?.Tags),
            CollectionName = "YouTube Video",
            IsFavorite = metadata?.IsFavorite == true,
            IsWatched = IsFinished(key),
            SubtitleSelectionKind = subtitleSelection.Kind,
            SubtitleSelectionPath = subtitleSelection.ExternalPath,
            SubtitleSelectionTrackId = subtitleSelection.TrackId,
            SubtitleSelectionTrackName = subtitleSelection.TrackName,
            ProfileId = metadata?.ProfileID,
            ProviderId = identity.ProviderID,
            RemoteId = identity.RemoteID,
            OriginalUrl = identity.OriginalURL,
            CanonicalUrl = identity.CanonicalURL ?? identity.OriginalURL,
            RemoteThumbnailUrl = identity.ThumbnailURL,
            RemoteSubtitleLanguage = subtitleSelection.RemoteLanguageCode ?? remote.SubtitleLanguage,
            SubtitleDelayMilliseconds = playback.SubtitleDelayMilliseconds,
            PlaybackSpeed = playback.PlaybackSpeed,
            AudioDelaySeconds = playback.AudioDelaySeconds,
            AudioSelectionKind = playback.AudioSelection?.Kind ?? VideoAudioSelectionKind.None,
            AudioSelectionTrackId = playback.AudioSelection?.TrackId,
            AudioSelectionFfIndex = playback.AudioSelection?.FfIndex,
            AudioSelectionTitle = playback.AudioSelection?.Title,
            AudioSelectionLanguage = playback.AudioSelection?.Language,
            AudioSelectionCodec = playback.AudioSelection?.Codec,
        };
    }

    private void UpsertLocalVideo(VideoItem video)
    {
        var path = NormalizeLocalPath(video.FilePath);
        video.Id = path;
        video.FilePath = path;
        var sourceID = Guid.TryParse(video.SourceId, out var parsedSourceID)
            ? parsedSourceID
            : LooseFilesSourceId;
        var item = _catalog!.Items.FirstOrDefault(candidate => IdentityEquals(candidate.Path, path));
        if (item == null)
        {
            item = new VideoLibraryItemDocument
            {
                Path = path,
                ImportedAt = ToDateTimeOffset(video.ImportedAt),
            };
            _catalog.Items.Add(item);
        }

        item.Path = path;
        item.SourceID = sourceID;
        item.Title = Path.GetFileNameWithoutExtension(path);
        item.ParentFolder = ResolveParentFolder(video, path);
        item.FileSize = Math.Max(video.FileSizeBytes, 0);
        item.ModifiedAt = ToNullableDateTimeOffset(video.ModifiedAt);
        item.LastSeenAt = ToDateTimeOffset(video.LastSeenAt ?? DateTime.UtcNow);
        item.MediaIdentity = VideoMediaIdentityDocument.Local(path);

        var metadata = GetOrCreateMetadata(path);
        metadata.IsFavorite |= video.IsFavorite;
        metadata.PosterPath ??= NormalizeOptionalPath(video.PosterPath);
        metadata.ProfileID ??= video.ProfileId;
        metadata.BoundSubtitlePath ??= NormalizeOptionalPath(video.SubtitlePath);
        if (metadata.Tags.Count == 0)
            metadata.Tags = SplitTags(video.Tags);
        RemoveMetadataIfEmpty(path, metadata);
    }

    private void UpsertRemoteVideo(VideoItem video)
    {
        if (string.IsNullOrWhiteSpace(video.ProviderId)
            || string.IsNullOrWhiteSpace(video.RemoteId)
            || string.IsNullOrWhiteSpace(video.OriginalUrl))
        {
            throw new InvalidDataException("Remote video identity is incomplete.");
        }

        var key = $"remote://{video.ProviderId}/{video.RemoteId}";
        video.Id = key;
        video.FilePath = key;
        var remote = _catalog!.RemoteItems.FirstOrDefault(item =>
            IdentityEquals(
                $"remote://{item.Identity.ProviderID}/{item.Identity.RemoteID}",
                key));
        var now = DateTimeOffset.UtcNow;
        if (remote == null)
        {
            remote = new RemoteVideoLibraryItemDocument { AddedAt = ToDateTimeOffset(video.ImportedAt) };
            _catalog.RemoteItems.Add(remote);
        }

        remote.Identity = new RemoteVideoIdentityDocument
        {
            ProviderID = video.ProviderId,
            RemoteID = video.RemoteId,
            OriginalURL = video.OriginalUrl,
            CanonicalURL = video.CanonicalUrl,
            Title = video.Title,
            ThumbnailURL = video.RemoteThumbnailUrl,
            Duration = video.DurationSeconds > 0 ? video.DurationSeconds : null,
        };
        remote.SubtitleLanguage = video.RemoteSubtitleLanguage;
        remote.HasResolvedSubtitleMetadata = true;
        remote.LastResolvedAt = video.ModifiedAt.HasValue
            ? ToDateTimeOffset(video.ModifiedAt.Value)
            : now;

        var metadata = GetOrCreateMetadata(key);
        metadata.IsFavorite |= video.IsFavorite;
        metadata.ProfileID ??= video.ProfileId;
        metadata.BoundSubtitlePath ??= NormalizeOptionalPath(video.SubtitlePath);
        if (metadata.Tags.Count == 0)
            metadata.Tags = SplitTags(video.Tags);
        RemoveMetadataIfEmpty(key, metadata);
    }

    private void SavePlaybackStateCore(string key, VideoPlaybackState state)
    {
        var position = NormalizeSeconds(state.PositionSeconds);
        var duration = NormalizeSeconds(state.DurationSeconds);
        SaveSubtitleSelection(key, state.SubtitleSelection);

        if (duration <= 0 || position < 2)
        {
            _history!.Positions.Remove(key);
            _history.PlaybackStates.Remove(key);
            return;
        }

        if (position >= duration - 5)
        {
            _history!.Positions.Remove(key);
            _history.PlaybackStates[key] = new VideoPlaybackStateDocument
            {
                Position = duration,
                Duration = duration,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsFinished = true,
                ResumeOptions = new VideoPlaybackResumeOptionsDocument(),
            };
            return;
        }

        _history!.Positions[key] = position;
        _history.PlaybackStates[key] = new VideoPlaybackStateDocument
        {
            Position = position,
            Duration = duration,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsFinished = false,
            ResumeOptions = ToResumeOptionsDocument(state),
        };
    }

    private VideoPlaybackState CreatePlaybackState(string key)
    {
        var document = _history!.PlaybackStates.GetValueOrDefault(key);
        var selection = FromSubtitleSelectionDocument(
            _history.SubtitleSelections.GetValueOrDefault(key));
        var resume = document?.ResumeOptions ?? new VideoPlaybackResumeOptionsDocument();
        var audioSelection = FromAudioSelectionDocument(resume.AudioSelection);
        return new VideoPlaybackState(
            document?.Position ?? _history.Positions.GetValueOrDefault(key),
            document?.Duration ?? 0,
            selection,
            (int)Math.Round((resume.SubtitleDelay ?? 0) * 1000),
            resume.Speed ?? 1,
            resume.AudioDelay ?? 0,
            audioSelection);
    }

    private void SaveSubtitleSelection(string key, VideoSubtitleSelection selection)
    {
        var document = ToSubtitleSelectionDocument(selection);
        if (document == null)
            _history!.SubtitleSelections.Remove(key);
        else
            _history!.SubtitleSelections[key] = document;
    }

    private static VideoPlaybackResumeOptionsDocument ToResumeOptionsDocument(
        VideoPlaybackState state) =>
        new()
        {
            Speed = Math.Abs(state.PlaybackSpeed - 1) >= 0.001
                ? VideoPlaybackState.NormalizePlaybackSpeed(state.PlaybackSpeed)
                : null,
            SubtitleDelay = Math.Abs(state.SubtitleDelayMilliseconds) >= 5
                ? VideoPlaybackState.NormalizeSubtitleDelayMilliseconds(
                    state.SubtitleDelayMilliseconds) / 1000d
                : null,
            AudioDelay = Math.Abs(state.AudioDelaySeconds) >= 0.005
                ? VideoPlaybackState.NormalizeAudioDelaySeconds(state.AudioDelaySeconds)
                : null,
            AudioSelection = ToAudioSelectionDocument(
                state.AudioSelection ?? VideoAudioSelection.None()),
        };

    private static VideoSubtitleSelectionDocument? ToSubtitleSelectionDocument(
        VideoSubtitleSelection selection) =>
        selection.Kind switch
        {
            VideoSubtitleSelectionKind.Off => new VideoSubtitleSelectionDocument
            {
                Off = new EmptyVideoSelectionDocument(),
            },
            VideoSubtitleSelectionKind.ExternalFile when !string.IsNullOrWhiteSpace(selection.ExternalPath) =>
                new VideoSubtitleSelectionDocument
                {
                    External = new ExternalVideoSubtitleSelectionDocument
                    {
                        Path = NormalizeLocalPath(selection.ExternalPath),
                    },
                },
            VideoSubtitleSelectionKind.EmbeddedTrack when selection.TrackId.HasValue =>
                new VideoSubtitleSelectionDocument
                {
                    Embedded = new EmbeddedVideoSubtitleSelectionDocument
                    {
                        Value = new VideoSubtitleTrackIdentityDocument
                        {
                            TrackID = selection.TrackId.Value,
                            Title = selection.TrackName ?? "",
                        },
                    },
                },
            VideoSubtitleSelectionKind.RemoteLanguage
                when !string.IsNullOrWhiteSpace(selection.RemoteLanguageCode) =>
                new VideoSubtitleSelectionDocument
                {
                    Remote = new RemoteLanguageVideoSubtitleSelectionDocument
                    {
                        Language = selection.RemoteLanguageCode,
                    },
                },
            _ => null,
        };

    private static VideoSubtitleSelection FromSubtitleSelectionDocument(
        VideoSubtitleSelectionDocument? document)
    {
        if (document?.Off != null)
            return VideoSubtitleSelection.Off();
        if (document?.External != null && !string.IsNullOrWhiteSpace(document.External.Path))
            return VideoSubtitleSelection.ExternalFile(document.External.Path);
        if (document?.Embedded?.Value != null)
        {
            return VideoSubtitleSelection.EmbeddedTrack(
                document.Embedded.Value.TrackID,
                document.Embedded.Value.Title);
        }
        if (document?.Remote != null && !string.IsNullOrWhiteSpace(document.Remote.Language))
            return VideoSubtitleSelection.RemoteLanguage(document.Remote.Language);
        return VideoSubtitleSelection.None();
    }

    private static VideoAudioSelectionDocument? ToAudioSelectionDocument(
        VideoAudioSelection selection) =>
        selection.Kind switch
        {
            VideoAudioSelectionKind.Off => new VideoAudioSelectionDocument
            {
                Off = new EmptyVideoSelectionDocument(),
            },
            VideoAudioSelectionKind.EmbeddedTrack => new VideoAudioSelectionDocument
            {
                Embedded = new EmbeddedVideoAudioSelectionDocument
                {
                    Value = new VideoAudioTrackIdentityDocument
                    {
                        TrackID = selection.TrackId ?? 0,
                        FfIndex = selection.FfIndex,
                        Title = selection.Title ?? "",
                        Language = selection.Language,
                        Codec = selection.Codec,
                    },
                },
            },
            _ => null,
        };

    private static VideoAudioSelection FromAudioSelectionDocument(
        VideoAudioSelectionDocument? document)
    {
        if (document?.Off != null)
            return VideoAudioSelection.Off();
        if (document?.Embedded?.Value is { } value)
        {
            return new VideoAudioSelection(
                VideoAudioSelectionKind.EmbeddedTrack,
                value.TrackID,
                value.FfIndex,
                value.Title,
                value.Language,
                value.Codec);
        }
        return VideoAudioSelection.None();
    }

    private VideoLibraryItemMetadataDocument? GetMetadata(string key)
    {
        var existingKey = _catalog!.ItemMetadataByPath.Keys.FirstOrDefault(
            candidate => IdentityEquals(candidate, key));
        return existingKey == null ? null : _catalog.ItemMetadataByPath[existingKey];
    }

    private VideoLibraryItemMetadataDocument GetOrCreateMetadata(string key)
    {
        var existingKey = _catalog!.ItemMetadataByPath.Keys.FirstOrDefault(
            candidate => IdentityEquals(candidate, key));
        if (existingKey != null)
            return _catalog.ItemMetadataByPath[existingKey];

        var metadata = new VideoLibraryItemMetadataDocument();
        _catalog.ItemMetadataByPath[key] = metadata;
        return metadata;
    }

    private void RemoveMetadataIfEmpty(
        string key,
        VideoLibraryItemMetadataDocument metadata)
    {
        if (!IsMetadataEmpty(metadata))
            return;
        var existingKey = _catalog!.ItemMetadataByPath.Keys.FirstOrDefault(
            candidate => IdentityEquals(candidate, key));
        if (existingKey != null)
            _catalog.ItemMetadataByPath.Remove(existingKey);
    }

    private void RemoveEmptyMetadata()
    {
        foreach (var key in _catalog!.ItemMetadataByPath
                     .Where(pair => IsMetadataEmpty(pair.Value))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _catalog.ItemMetadataByPath.Remove(key);
        }
    }

    private static bool IsMetadataEmpty(VideoLibraryItemMetadataDocument metadata) =>
        string.IsNullOrWhiteSpace(metadata.DisplayTitle)
        && !metadata.IsFavorite
        && metadata.Tags.Count == 0
        && metadata.CollectionIDs.Count == 0
        && string.IsNullOrWhiteSpace(metadata.BoundSubtitlePath)
        && string.IsNullOrWhiteSpace(metadata.PosterPath)
        && string.IsNullOrWhiteSpace(metadata.ProfileID);

    private void RemoveCatalogItems(HashSet<string> keys)
    {
        _catalog!.Items.RemoveAll(item => keys.Contains(item.Path));
        _catalog.RemoteItems.RemoveAll(item =>
            keys.Contains($"remote://{item.Identity.ProviderID}/{item.Identity.RemoteID}"));
        RemoveMetadataAndCollectionReferences(keys);
    }

    private void RemoveMetadataAndCollectionReferences(HashSet<string> keys)
    {
        foreach (var metadataKey in _catalog!.ItemMetadataByPath.Keys
                     .Where(keys.Contains)
                     .ToList())
        {
            _catalog.ItemMetadataByPath.Remove(metadataKey);
        }

        foreach (var collection in _catalog.Collections)
            collection.ItemPaths.RemoveAll(keys.Contains);
    }

    private DateTime? GetPlaybackUpdatedAt(string key) =>
        _history!.PlaybackStates.TryGetValue(key, out var state)
            ? state.UpdatedAt.UtcDateTime
            : null;

    private bool IsFinished(string key) =>
        _history!.PlaybackStates.TryGetValue(key, out var state) && state.IsFinished;

    private static VideoLibrarySource ToVideoLibrarySource(VideoLibrarySourceDocument source) =>
        new()
        {
            Id = source.Id.ToString("D"),
            Name = source.Name,
            FolderPath = source.Path,
            CreatedAt = source.CreatedAt?.UtcDateTime ?? DateTime.UnixEpoch,
            LastScannedAt = source.LastScannedAt?.UtcDateTime,
            LastError = source.LastError,
        };

    private static VideoCollection ToVideoCollection(VideoLibraryCollectionDocument document) =>
        new()
        {
            Id = document.Id.ToString("D"),
            Name = document.Name,
            Kind = string.Equals(document.Kind, "smart", StringComparison.OrdinalIgnoreCase)
                ? VideoCollectionKind.Smart
                : VideoCollectionKind.Manual,
            SmartRules = document.SmartRules.Select(FromSmartRuleDocument).ToList(),
            ItemIds = document.ItemPaths.ToList(),
        };

    private static VideoLibrarySmartRuleDocument ToSmartRuleDocument(VideoSmartRule rule) =>
        new()
        {
            Id = ParseOrCreateGuid(rule.Id),
            Field = ToCamelCase(rule.Field.ToString()),
            Match = ToCamelCase(rule.Match.ToString()),
            Value = rule.Value,
        };

    private static VideoSmartRule FromSmartRuleDocument(VideoLibrarySmartRuleDocument document) =>
        new()
        {
            Id = document.Id.ToString("D"),
            Field = Enum.TryParse<VideoSmartRuleField>(
                document.Field,
                ignoreCase: true,
                out var field)
                    ? field
                    : VideoSmartRuleField.FileName,
            Match = Enum.TryParse<VideoSmartRuleMatch>(
                document.Match,
                ignoreCase: true,
                out var match)
                    ? match
                    : VideoSmartRuleMatch.Contains,
            Value = document.Value,
        };

    private async Task SaveCatalogAsync(CancellationToken ct) =>
        await _json.WriteAsync(_catalogPath, _catalog!, ct);

    private async Task SaveHistoryAsync(CancellationToken ct) =>
        await _json.WriteAsync(_historyPath, _history!, ct);

    private static bool MatchesQuery(VideoItem video, string? queryText)
    {
        var query = queryText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return Contains(video.Title, query)
               || Contains(video.FilePath, query)
               || Contains(video.SourceFolderPath, query)
               || Contains(video.Tags, query)
               || Contains(video.CollectionName, query)
               || Contains(video.OriginalUrl, query);
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string ResolveParentFolder(VideoItem video, string path)
    {
        if (!string.IsNullOrWhiteSpace(video.CollectionName))
            return video.CollectionName;
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory)
            ? ""
            : new DirectoryInfo(directory).Name;
    }

    private static string NormalizeIdentityKey(string value) =>
        RemoteVideoIdentity.IsPersistenceKey(value)
            ? value.Trim()
            : NormalizeLocalPath(value);

    private static string NormalizeLocalPath(string value) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));

    private static string? NormalizeOptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeLocalPath(value);

    private static bool IdentityEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static Guid ParseOrCreateGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : Guid.NewGuid();

    private static List<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private static string? JoinTags(IReadOnlyList<string>? tags) =>
        tags == null || tags.Count == 0 ? null : string.Join(", ", tags);

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc);
    }

    private static DateTimeOffset? ToNullableDateTimeOffset(DateTime? value) =>
        value.HasValue ? ToDateTimeOffset(value.Value) : null;

    private static double NormalizeSeconds(double value) =>
        double.IsFinite(value) ? Math.Max(value, 0) : 0;

    private static string ToCamelCase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
