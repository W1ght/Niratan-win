using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Views.Video;

namespace Hoshi.Services.Video;

internal sealed class VideoPlayerWindowService : IVideoPlayerWindowService
{
    private VideoPlayerWindow? _window;

    public async Task OpenAsync(VideoItem video, CancellationToken ct = default)
    {
        if (_window == null)
        {
            _window = new VideoPlayerWindow();
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();
        await _window.OpenVideoAsync(video, ct);
    }
}
