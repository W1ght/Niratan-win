using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hoshi.Models;

namespace Hoshi.Services.Video;

public interface IVideoPlayerWindowService
{
    Task OpenAsync(VideoItem video, CancellationToken ct = default);

    Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default);
}
