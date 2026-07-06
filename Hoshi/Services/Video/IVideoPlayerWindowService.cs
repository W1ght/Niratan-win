using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Video;

public interface IVideoPlayerWindowService
{
    Task OpenAsync(VideoItem video, CancellationToken ct = default);
}
