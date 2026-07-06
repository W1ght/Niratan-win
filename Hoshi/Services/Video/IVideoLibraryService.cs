using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;

namespace Hoshi.Services.Video;

public interface IVideoLibraryService
{
    Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default);

    Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default);

    Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default);

    Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default);

    Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default);

    Task<Result> SaveProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default);
}
