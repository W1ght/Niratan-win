using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoPlayerWindowService
{
    event EventHandler? LibraryChanged;
    event EventHandler? PlaybackWindowOpened
    {
        add { }
        remove { }
    }
    event EventHandler? PlaybackWindowClosed
    {
        add { }
        remove { }
    }
    bool IsPlaybackWindowOpen => false;

    Task OpenAsync(VideoItem video, CancellationToken ct = default);

    Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default);

    Task OpenAsync(VideoPlaybackLaunchRequest request, CancellationToken ct = default) =>
        OpenAsync(request.Video, request.Playlist, ct);
}
