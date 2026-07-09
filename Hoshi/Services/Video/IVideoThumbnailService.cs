using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;

namespace Hoshi.Services.Video;

public interface IVideoThumbnailService
{
    Task<Result<string?>> EnsureThumbnailAsync(
        VideoItem video,
        bool generateIfMissing = true,
        CancellationToken ct = default);

    void Suspend()
    {
    }

    void Resume()
    {
    }
}
