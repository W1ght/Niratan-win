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

public interface IVideoLibraryService
{
    Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default);

    Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default);

    Task<Result<VideoFolderScanResult>> ScanFolderAsync(
        string folderPath,
        CancellationToken ct = default);

    Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default);

    Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default);

    Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default);

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

    Task<Result> SetFavoriteAsync(
        string videoId,
        bool isFavorite,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}
