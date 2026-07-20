using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Niratan.Services.Video;

internal sealed class VideoLibraryService : IVideoLibraryService
{
    private const string UntitledCollectionName = "Untitled Collection";

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".webm",
        ".avi",
        ".mov",
    };

    private static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa"];
    private static readonly string[] PosterExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] FolderPosterNames = ["cover", "poster", "folder"];

    private readonly IVideoDataService _dataService;
    private readonly ILogger<VideoLibraryService> _logger;

    public VideoLibraryService(IVideoDataService dataService, ILogger<VideoLibraryService> logger)
    {
        _dataService = dataService;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<IReadOnlyList<VideoItem>>.Success(
                await _dataService.GetVideosAsync(queryText, token)),
            "Error loading videos",
            ct);

    public async Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<VideoItem>.Failure("Video path is empty.", "Import failed");

        if (!File.Exists(filePath))
            return Result<VideoItem>.Failure("Video file was not found.", "Import failed");

        if (!SupportedVideoExtensions.Contains(Path.GetExtension(filePath)))
            return Result<VideoItem>.Failure("Unsupported video file type.", "Import failed");

        return await ExecuteAsync(
            async token =>
            {
                var fileInfo = new FileInfo(filePath);
                var video = new VideoItem
                {
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    FilePath = filePath,
                    SubtitlePath = FindSidecarSubtitle(filePath),
                    SourceFolderPath = Path.GetDirectoryName(filePath),
                    PosterPath = FindPosterImage(filePath),
                    CollectionName = GetCollectionName(filePath, Path.GetDirectoryName(filePath)),
                    FileSizeBytes = fileInfo.Length,
                    ModifiedAt = fileInfo.LastWriteTimeUtc,
                    ImportedAt = DateTime.UtcNow,
                };
                await _dataService.UpsertVideoAsync(video, token);
                _logger.LogInformation("Imported video {FilePath}", video.FilePath);
                return Result<VideoItem>.Success(video);
            },
            "Error saving video",
            ct);
    }

    public async Task<Result<VideoItem>> AddRemoteVideoAsync(
        ResolvedRemoteVideoSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Identity.ProviderId.Equals(YouTubeUrlParser.ProviderId, StringComparison.OrdinalIgnoreCase))
            return Result<VideoItem>.Failure("This remote video provider is not supported.", "Add link failed");

        return await ExecuteAsync(
            async token =>
            {
                var identity = source.Identity;
                var key = identity.PersistenceKey;
                var video = new VideoItem
                {
                    Id = key,
                    Title = identity.Title,
                    FilePath = key,
                    ImportedAt = DateTime.UtcNow,
                    DurationSeconds = identity.Duration?.TotalSeconds ?? 0,
                    SourceFolderPath = "YouTube Video",
                    CollectionName = "YouTube Video",
                    ProviderId = identity.ProviderId,
                    RemoteId = identity.RemoteId,
                    OriginalUrl = identity.OriginalUrl,
                    CanonicalUrl = identity.CanonicalUrl,
                    RemoteThumbnailUrl = identity.ThumbnailUrl,
                    RemoteSubtitleLanguage = source.SelectedSubtitleLanguage,
                    SubtitleSelectionKind = string.IsNullOrWhiteSpace(source.SelectedSubtitleLanguage)
                        ? VideoSubtitleSelectionKind.None
                        : VideoSubtitleSelectionKind.RemoteLanguage,
                };
                await _dataService.UpsertVideoAsync(video, token);
                _logger.LogInformation(
                    "Added remote video {ProviderId}/{RemoteId}",
                    identity.ProviderId,
                    identity.RemoteId);
                return Result<VideoItem>.Success(video);
            },
            "Error saving YouTube video",
            ct);
    }

    public async Task<Result<VideoFolderScanResult>> ScanFolderAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return Result<VideoFolderScanResult>.Failure("Video folder path is empty.", "Scan failed");

        if (!Directory.Exists(folderPath))
            return Result<VideoFolderScanResult>.Failure("Video folder was not found.", "Scan failed");

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var source = await _dataService.GetVideoLibrarySourceByPathAsync(rootPath, ct)
            ?? new VideoLibrarySource
            {
                Name = new DirectoryInfo(rootPath).Name,
                FolderPath = rootPath,
                CreatedAt = DateTime.UtcNow,
            };
        await _dataService.UpsertVideoLibrarySourceAsync(source, ct);

        var refreshed = await RefreshSourceCoreAsync(source, ct);
        return refreshed.IsSuccess
            ? Result<VideoFolderScanResult>.Success(new VideoFolderScanResult(
                refreshed.Value!.VideoCount,
                refreshed.Value.Videos))
            : Result<VideoFolderScanResult>.Failure(refreshed.Error!, refreshed.ErrorTitle!);
    }

    public async Task<Result<IReadOnlyList<VideoLibrarySource>>> GetSourcesAsync(CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<IReadOnlyList<VideoLibrarySource>>.Success(
                await _dataService.GetVideoLibrarySourcesAsync(token)),
            "Error loading video sources",
            ct);

    public async Task<Result<VideoSourceRefreshResult>> RefreshSourceAsync(
        string sourceId,
        CancellationToken ct = default)
    {
        var source = await _dataService.GetVideoLibrarySourceAsync(sourceId, ct);
        return source == null
            ? Result<VideoSourceRefreshResult>.Failure("Video source was not found.", "Refresh failed")
            : await RefreshSourceCoreAsync(source, ct);
    }

    public async Task<Result<IReadOnlyList<VideoSourceRefreshResult>>> RefreshAllSourcesAsync(
        CancellationToken ct = default)
    {
        var sourcesResult = await GetSourcesAsync(ct);
        if (!sourcesResult.IsSuccess)
        {
            return Result<IReadOnlyList<VideoSourceRefreshResult>>.Failure(
                sourcesResult.Error!, sourcesResult.ErrorTitle!);
        }

        var refreshed = new List<VideoSourceRefreshResult>();
        string? firstError = null;
        foreach (var source in sourcesResult.Value!)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RefreshSourceCoreAsync(source, ct);
            if (result.IsSuccess)
                refreshed.Add(result.Value!);
            else
                firstError ??= result.Error;
        }

        return firstError == null
            ? Result<IReadOnlyList<VideoSourceRefreshResult>>.Success(refreshed)
            : Result<IReadOnlyList<VideoSourceRefreshResult>>.Failure(
                firstError, "Some sources could not be refreshed");
    }

    public async Task<Result> RemoveSourceAsync(string sourceId, CancellationToken ct = default) =>
        await ExecuteAsync(
            token => _dataService.DeleteVideoLibrarySourceAsync(sourceId, token),
            "Error removing video source",
            ct);

    public async Task<Result<int>> RemoveMissingVideosAsync(CancellationToken ct = default) =>
        await ExecuteAsync(
            async token =>
            {
                var videos = await _dataService.GetVideosAsync(ct: token);
                var missingIds = videos
                    .Where(video => !video.IsRemote && !File.Exists(video.FilePath))
                    .Select(video => video.Id)
                    .ToList();
                await _dataService.DeleteVideosAsync(missingIds, token);
                return Result<int>.Success(missingIds.Count);
            },
            "Error removing missing videos",
            ct);

    private async Task<Result<VideoSourceRefreshResult>> RefreshSourceCoreAsync(
        VideoLibrarySource source,
        CancellationToken ct)
    {
        if (!Directory.Exists(source.FolderPath))
        {
            const string error = "Video source folder is no longer available.";
            await _dataService.UpdateVideoLibrarySourceScanStateAsync(
                source.Id, source.LastScannedAt, error, ct);
            return Result<VideoSourceRefreshResult>.Failure(error, "Refresh failed");
        }

        var rootPath = source.FolderPath;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        var result = await ExecuteAsync(
            async token =>
            {
                var scannedAt = DateTime.UtcNow;
                var videos = Directory
                    .EnumerateFiles(rootPath, "*", options)
                    .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => CreateVideoItemFromPath(path, rootPath, source.Id, scannedAt))
                    .ToList();

                foreach (var video in videos)
                {
                    token.ThrowIfCancellationRequested();
                    await _dataService.UpsertVideoAsync(video, token);
                }

                await _dataService.DeleteSourceVideosExceptAsync(
                    source.Id,
                    videos.Select(video => video.FilePath).ToList(),
                    token);
                await _dataService.UpdateVideoLibrarySourceScanStateAsync(
                    source.Id, scannedAt, null, token);
                source.LastScannedAt = scannedAt;
                source.LastError = null;

                _logger.LogInformation(
                    "Scanned {Count} videos from {FolderPath}",
                    videos.Count,
                    rootPath);
                return Result<VideoSourceRefreshResult>.Success(
                    new VideoSourceRefreshResult(source, videos.Count, videos));
            },
            "Error scanning video folder",
            ct);

        if (!result.IsSuccess && !result.IsCancelled)
        {
            source.LastError = result.Error;
            await _dataService.UpdateVideoLibrarySourceScanStateAsync(
                source.Id, source.LastScannedAt, result.Error, CancellationToken.None);
        }

        return result;
    }

    public async Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<VideoItem?>.Success(await _dataService.GetVideoAsync(videoId, token)),
            "Error loading video",
            ct);

    public async Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<IReadOnlyList<VideoCollection>>.Success(
                await _dataService.GetVideoCollectionsAsync(token)),
            "Error loading video collections",
            ct);

    public async Task<Result<VideoCollection>> CreateManualCollectionAsync(
        string name,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token =>
            {
                var collection = new VideoCollection
                {
                    Name = NormalizeCollectionName(name),
                    Kind = VideoCollectionKind.Manual,
                    ItemIds = NormalizeItemIds(videoIds),
                    SmartRules = [],
                };
                await _dataService.UpsertVideoCollectionAsync(collection, token);
                await _dataService.SetVideoCollectionItemsAsync(collection.Id, collection.ItemIds, token);
                return Result<VideoCollection>.Success(collection);
            },
            "Error saving video collection",
            ct);

    public async Task<Result<VideoCollection>> CreateSmartCollectionAsync(
        string name,
        IReadOnlyList<VideoSmartRule> rules,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token =>
            {
                var collection = new VideoCollection
                {
                    Name = NormalizeCollectionName(name),
                    Kind = VideoCollectionKind.Smart,
                    SmartRules = NormalizeSmartRules(rules),
                    ItemIds = [],
                };
                await _dataService.UpsertVideoCollectionAsync(collection, token);
                return Result<VideoCollection>.Success(collection);
            },
            "Error saving video collection",
            ct);

    public async Task<Result> DeleteCollectionAsync(string collectionId, CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => await _dataService.DeleteVideoCollectionAsync(collectionId, token),
            "Error deleting video collection",
            ct);

    public async Task<Result<VideoCollection>> UpdateManualCollectionAsync(
        VideoCollection collection,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token =>
            {
                collection.Kind = VideoCollectionKind.Manual;
                collection.ItemIds = NormalizeItemIds(videoIds);
                collection.SmartRules = [];
                collection.UpdatedAt = DateTime.UtcNow;
                await _dataService.UpsertVideoCollectionAsync(collection, token);
                await _dataService.SetVideoCollectionItemsAsync(collection.Id, collection.ItemIds, token);
                return Result<VideoCollection>.Success(collection);
            },
            "Error updating video collection",
            ct);

    public async Task<Result<VideoCollection>> UpdateSmartCollectionAsync(
        VideoCollection collection,
        string name,
        IReadOnlyList<VideoSmartRule> rules,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token =>
            {
                collection.Name = NormalizeCollectionName(name);
                collection.Kind = VideoCollectionKind.Smart;
                collection.SmartRules = NormalizeSmartRules(rules);
                collection.ItemIds = [];
                collection.UpdatedAt = DateTime.UtcNow;
                await _dataService.UpsertVideoCollectionAsync(collection, token);
                await _dataService.SetVideoCollectionItemsAsync(collection.Id, [], token);
                return Result<VideoCollection>.Success(collection);
            },
            "Error updating smart collection",
            ct);

    public async Task<Result> SetFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => await _dataService.UpdateVideoFavoriteAsync(videoId, isFavorite, token),
            "Error updating video favorite",
            ct);

    public async Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.UpdateVideoLastOpenedAsync(videoId, DateTime.UtcNow, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating video last opened time for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error opening video");
        }
    }

    public async Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.DeleteVideoAsync(videoId, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting video {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error deleting video");
        }
    }

    public async Task<Result> DeleteVideosAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default) =>
        await ExecuteAsync(
            token => _dataService.DeleteVideosAsync(NormalizeItemIds(videoIds), token),
            "Error deleting videos",
            ct);

    public async Task<Result> UpdateVideoDetailsAsync(
        string videoId,
        string title,
        IReadOnlyList<string> tags,
        string? subtitlePath,
        CancellationToken ct = default)
    {
        var normalizedTitle = title?.Trim() ?? string.Empty;
        if (normalizedTitle.Length == 0)
            return Result.Failure("Display title cannot be empty.", "Could not save video details");

        var normalizedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var normalizedSubtitle = string.IsNullOrWhiteSpace(subtitlePath)
            ? null
            : Path.GetFullPath(subtitlePath);
        if (normalizedSubtitle != null && !File.Exists(normalizedSubtitle))
            return Result.Failure("The selected subtitle file no longer exists.", "Could not bind subtitle");

        return await ExecuteAsync(
            token => _dataService.UpdateVideoDetailsAsync(
                videoId,
                normalizedTitle,
                normalizedTags.Count == 0 ? null : string.Join(", ", normalizedTags),
                normalizedSubtitle,
                token),
            "Error saving video details",
            ct);
    }

    public async Task<Result> SaveProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.SaveVideoProgressAsync(videoId, positionSeconds, durationSeconds, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving video progress for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error saving video progress");
        }
    }

    public async Task<Result> SavePlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.SaveVideoPlaybackStateAsync(videoId, state, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving video playback state for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error saving video playback state");
        }
    }

    public async Task<Result> MarkWatchedAsync(
        string videoId,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.MarkVideoWatchedAsync(videoId, DateTime.UtcNow, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking video watched for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error updating video");
        }
    }

    public async Task<Result> ClearProgressAsync(
        string videoId,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.ClearVideoProgressAsync(videoId, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing video progress for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error clearing progress");
        }
    }

    public async Task<Result> SetVideoProfileAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.UpdateVideoProfileIdAsync(videoId, profileId, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving profile override for video {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error saving video profile");
        }
    }

    public static string? FindSidecarSubtitle(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.Combine(directory ?? "", Path.GetFileNameWithoutExtension(videoPath));

        foreach (var extension in SubtitleExtensions)
        {
            var candidate = stem + extension;
            if (File.Exists(candidate))
                return candidate;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        foreach (var extension in SubtitleExtensions)
        {
            var prefix = Path.GetFileNameWithoutExtension(videoPath) + ".";
            var matches = Directory.EnumerateFiles(directory, "*" + extension)
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            var candidate = matches.FirstOrDefault();
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    public static string? FindPosterImage(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(videoPath));
        foreach (var extension in PosterExtensions)
        {
            var candidate = stem + extension;
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var name in FolderPosterNames)
        {
            foreach (var extension in PosterExtensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static VideoItem CreateVideoItemFromPath(
        string filePath,
        string rootPath,
        string sourceId,
        DateTime scannedAt)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileInfo = new FileInfo(filePath);
        return new VideoItem
        {
            Title = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            SubtitlePath = FindSidecarSubtitle(filePath),
            SourceFolderPath = directory,
            SourceId = sourceId,
            LastSeenAt = scannedAt,
            PosterPath = FindPosterImage(filePath),
            CollectionName = GetCollectionName(filePath, rootPath),
            FileSizeBytes = fileInfo.Length,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            ImportedAt = DateTime.UtcNow,
        };
    }

    private static string? GetCollectionName(string filePath, string? rootPath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        if (string.IsNullOrWhiteSpace(rootPath))
            return new DirectoryInfo(directory).Name;

        var fullRoot = Path.GetFullPath(rootPath);
        var fullDirectory = Path.GetFullPath(directory);
        if (string.Equals(fullRoot, fullDirectory, StringComparison.OrdinalIgnoreCase))
            return new DirectoryInfo(fullRoot).Name;

        var relative = Path.GetRelativePath(fullRoot, fullDirectory);
        var firstSegment = relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
        return string.IsNullOrWhiteSpace(firstSegment)
            ? new DirectoryInfo(fullDirectory).Name
            : firstSegment;
    }

    private static string NormalizeCollectionName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? UntitledCollectionName : normalized;
    }

    private static IReadOnlyList<string> NormalizeItemIds(IReadOnlyList<string> videoIds)
    {
        if (videoIds == null || videoIds.Count == 0)
            return [];

        return videoIds
            .Where(videoId => !string.IsNullOrWhiteSpace(videoId))
            .Select(videoId => videoId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<VideoSmartRule> NormalizeSmartRules(IReadOnlyList<VideoSmartRule> rules)
    {
        if (rules == null || rules.Count == 0)
            return [];

        return rules
            .Select(rule => new VideoSmartRule
            {
                Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id,
                Field = rule.Field,
                Match = rule.Match,
                Value = rule.Value?.Trim() ?? string.Empty,
            })
            .Where(rule => rule.Match == VideoSmartRuleMatch.IsTrue || rule.Value.Length > 0)
            .ToList();
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> action,
        string errorTitle,
        CancellationToken ct)
    {
        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ErrorTitle}", errorTitle);
            return Result<T>.Failure(ex.Message, errorTitle);
        }
    }

    private async Task<Result> ExecuteAsync(
        Func<CancellationToken, Task> action,
        string errorTitle,
        CancellationToken ct)
    {
        try
        {
            await action(ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ErrorTitle}", errorTitle);
            return Result.Failure(ex.Message, errorTitle);
        }
    }
}
