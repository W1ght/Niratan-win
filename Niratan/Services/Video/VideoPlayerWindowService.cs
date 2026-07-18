using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Niratan.Models;
using Niratan.Views.Video;

namespace Niratan.Services.Video;

internal sealed class VideoPlayerWindowService : IVideoPlayerWindowService
{
    private readonly IVideoThumbnailService _thumbnailService;
    private VideoPlayerWindow? _window;

    public event EventHandler? LibraryChanged;
    public event EventHandler? PlaybackWindowOpened;
    public event EventHandler? PlaybackWindowClosed;

    public bool IsPlaybackWindowOpen => _window != null;

    public VideoPlayerWindowService(IVideoThumbnailService thumbnailService)
    {
        _thumbnailService = thumbnailService;
    }

    public Task OpenAsync(VideoItem video, CancellationToken ct = default) =>
        OpenAsync(video, [video], ct);

    public async Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
        => await OpenAsync(new VideoPlaybackLaunchRequest(video, playlist), ct);

    public async Task OpenAsync(VideoPlaybackLaunchRequest request, CancellationToken ct = default)
    {
        if (_window == null)
        {
            _window = new VideoPlayerWindow();
            _window.PlaybackStateSaved += OnWindowPlaybackStateSaved;
            _window.Closed += OnWindowClosed;
            _thumbnailService.Suspend();
            PlaybackWindowOpened?.Invoke(this, EventArgs.Empty);
        }

        _window.Activate();
        await _window.OpenVideoAsync(request, ct);
    }

    private void OnWindowPlaybackStateSaved(object? sender, EventArgs e) =>
        LibraryChanged?.Invoke(this, EventArgs.Empty);

    private void OnWindowClosed(object? sender, WindowEventArgs e)
    {
        if (sender is VideoPlayerWindow window)
        {
            window.Closed -= OnWindowClosed;
        }

        _window = null;
        _thumbnailService.Resume();
        PlaybackWindowClosed?.Invoke(this, EventArgs.Empty);
    }
}
