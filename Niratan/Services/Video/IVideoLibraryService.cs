using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public sealed record VideoFolderScanResult(
    int ImportedCount,
    IReadOnlyList<VideoItem> Videos);

public sealed record VideoSourceRefreshResult(
    VideoLibrarySource Source,
    int VideoCount,
    IReadOnlyList<VideoItem> Videos);

public interface IVideoLibraryService
{
    Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default);

    Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default);

    Task<Result<VideoItem>> AddRemoteVideoAsync(
        ResolvedRemoteVideoSource source,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoItem>.Failure("Remote videos are not available.", "Add link failed"));

    Task<Result<VideoFolderScanResult>> ScanFolderAsync(
        string folderPath,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<VideoLibrarySource>>> GetSourcesAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<VideoLibrarySource>>.Success([]));

    Task<Result<VideoSourceRefreshResult>> RefreshSourceAsync(
        string sourceId,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoSourceRefreshResult>.Failure("Video source was not found.", "Refresh failed"));

    Task<Result<IReadOnlyList<VideoSourceRefreshResult>>> RefreshAllSourcesAsync(
        CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<VideoSourceRefreshResult>>.Success([]));

    Task<Result> RemoveSourceAsync(string sourceId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    Task<Result<int>> RemoveMissingVideosAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<int>.Success(0));

    Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default);

    Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default);

    Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default);

    Task<Result> DeleteVideosAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    Task<Result> UpdateVideoDetailsAsync(
        string videoId,
        string title,
        IReadOnlyList<string> tags,
        string? subtitlePath,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    Task<Result> SaveProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default);

    Task<Result> SavePlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default);

    Task<Result> MarkWatchedAsync(
        string videoId,
        CancellationToken ct = default);

    Task<Result> ClearProgressAsync(
        string videoId,
        CancellationToken ct = default);

    Task<Result> SetVideoProfileAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result<IReadOnlyList<VideoCollection>>.Success([]));

    Task<Result<VideoCollection>> CreateSmartCollectionAsync(
        string name,
        IReadOnlyList<VideoSmartRule> rules,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoCollection>.Failure("Smart collections are not available.", "Collection unavailable"));

    Task<Result<VideoCollection>> CreateManualCollectionAsync(
        string name,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoCollection>.Failure("Manual collections are not available.", "Collection unavailable"));

    Task<Result> DeleteCollectionAsync(string collectionId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());

    Task<Result<VideoCollection>> UpdateManualCollectionAsync(
        VideoCollection collection,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoCollection>.Success(collection));

    Task<Result<VideoCollection>> UpdateSmartCollectionAsync(
        VideoCollection collection,
        string name,
        IReadOnlyList<VideoSmartRule> rules,
        CancellationToken ct = default) =>
        Task.FromResult(Result<VideoCollection>.Success(collection));

    Task<Result> SetFavoriteAsync(
        string videoId,
        bool isFavorite,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}
