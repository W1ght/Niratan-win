using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Video;

namespace Hoshi.Services.Storage;

public interface IVideoDataService
{
    Task<IReadOnlyList<VideoItem>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default
    );
    Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default);
    Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default);
    Task DeleteVideoAsync(string videoId, CancellationToken ct = default);
    Task UpdateVideoLastOpenedAsync(
        string videoId,
        DateTime lastOpenedAt,
        CancellationToken ct = default
    );
    Task SaveVideoProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default
    );
    Task SaveVideoPlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default
    );
    Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(CancellationToken ct = default);
    Task UpsertVideoCollectionAsync(VideoCollection collection, CancellationToken ct = default);
    Task DeleteVideoCollectionAsync(string collectionId, CancellationToken ct = default);

    Task SetVideoCollectionItemsAsync(
        string collectionId,
        IReadOnlyList<string> videoIds,
        CancellationToken ct = default);
    Task UpdateVideoThumbnailPathAsync(string videoId, string? thumbnailPath, CancellationToken ct = default);
    Task UpdateVideoFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default);
    Task MarkVideoWatchedAsync(
        string videoId,
        DateTime watchedAt,
        CancellationToken ct = default
    );
    Task ClearVideoProgressAsync(
        string videoId,
        CancellationToken ct = default
    );
    Task UpdateVideoProfileIdAsync(
        string videoId,
        string? profileId,
        CancellationToken ct = default
    );
}
