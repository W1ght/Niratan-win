using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hoshi.Models;
using Hoshi.Services.Profiles;
using Hoshi.Views.Video;
using Microsoft.UI.Xaml;

namespace Hoshi.Services.Video;

internal sealed class VideoPlayerWindowService : IVideoPlayerWindowService
{
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly IVideoThumbnailService _thumbnailService;
    private VideoPlayerWindow? _window;

    public event EventHandler? LibraryChanged;
    public event EventHandler? PlaybackWindowOpened;
    public event EventHandler? PlaybackWindowClosed;

    public bool IsPlaybackWindowOpen => _window != null;

    public VideoPlayerWindowService(
        IProfileRuntimeService profileRuntime,
        IVideoThumbnailService thumbnailService)
    {
        _profileRuntime = profileRuntime;
        _thumbnailService = thumbnailService;
    }

    public Task OpenAsync(VideoItem video, CancellationToken ct = default) =>
        OpenAsync(video, [video], ct);

    public async Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
    {
        await _profileRuntime.ActivateForVideoAsync(video, ct);

        if (_window == null)
        {
            _window = new VideoPlayerWindow();
            _window.PlaybackStateSaved += OnWindowPlaybackStateSaved;
            _window.Closed += OnWindowClosed;
            _thumbnailService.Suspend();
            PlaybackWindowOpened?.Invoke(this, EventArgs.Empty);
        }

        _window.Activate();
        await _window.OpenVideoAsync(video, playlist, ct);
    }

    private void OnWindowPlaybackStateSaved(object? sender, EventArgs e) =>
        LibraryChanged?.Invoke(this, EventArgs.Empty);

    private void OnWindowClosed(object? sender, WindowEventArgs e)
    {
        if (sender is VideoPlayerWindow window)
            window.Closed -= OnWindowClosed;

        _window = null;
        _thumbnailService.Resume();
        PlaybackWindowClosed?.Invoke(this, EventArgs.Empty);
    }
}
