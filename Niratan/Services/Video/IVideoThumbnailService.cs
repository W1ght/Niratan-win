using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoThumbnailService
{
    Task<string?> EnsureThumbnailAsync(
        VideoItem video,
        bool generateIfMissing,
        CancellationToken ct = default);

    void Suspend();
    void Resume();
}
