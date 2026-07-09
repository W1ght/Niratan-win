using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Video;

public interface IVideoThumbnailService
{
    Task<string?> EnsureThumbnailAsync(
        VideoItem video,
        bool generateIfMissing,
        CancellationToken ct = default);

    void Suspend();
    void Resume();
}
