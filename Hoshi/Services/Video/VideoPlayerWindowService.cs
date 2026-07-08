using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hoshi.Models;
using Hoshi.Services.Profiles;
using Hoshi.Views.Video;

namespace Hoshi.Services.Video;

internal sealed class VideoPlayerWindowService : IVideoPlayerWindowService
{
    private readonly IProfileRuntimeService _profileRuntime;
    private VideoPlayerWindow? _window;

    public VideoPlayerWindowService(IProfileRuntimeService profileRuntime)
    {
        _profileRuntime = profileRuntime;
    }

    public Task OpenAsync(VideoItem video, CancellationToken ct = default) =>
        OpenAsync(video, [video], ct);

    public async Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
    {
        await _profileRuntime.ActivateForVideoAsync(video, ct);

        if (_window == null)
        {
            _window = new VideoPlayerWindow();
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();
        await _window.OpenVideoAsync(video, playlist, ct);
    }
}
