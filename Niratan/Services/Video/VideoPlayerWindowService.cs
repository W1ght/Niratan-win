using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Niratan.Models;
using Niratan.Services.Profiles;
using Niratan.Views.Video;
using Microsoft.UI.Xaml;

namespace Niratan.Services.Video;

internal sealed class VideoPlayerWindowService : IVideoPlayerWindowService
{
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly IVideoThumbnailService _thumbnailService;
    private VideoPlayerWindow? _window;
    private VideoItem? _activeVideo;

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
        => await OpenAsync(new VideoPlaybackLaunchRequest(video, playlist), ct);

    public async Task OpenAsync(VideoPlaybackLaunchRequest request, CancellationToken ct = default)
    {
        var video = request.Video;
        _activeVideo = video;
        await _profileRuntime.ActivateForVideoAsync(video, ct);

        if (_window == null)
        {
            _window = new VideoPlayerWindow();
            _window.PlaybackStateSaved += OnWindowPlaybackStateSaved;
            _window.Activated += OnWindowActivated;
            _window.Closed += OnWindowClosed;
            _thumbnailService.Suspend();
            PlaybackWindowOpened?.Invoke(this, EventArgs.Empty);
        }

        _window.Activate();
        await _window.OpenVideoAsync(request, ct);
    }

    private void OnWindowPlaybackStateSaved(object? sender, EventArgs e) =>
        LibraryChanged?.Invoke(this, EventArgs.Empty);

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated
            && (_window?.ViewModel.CurrentVideo ?? _activeVideo) is { } video)
        {
            await _profileRuntime.ActivateForVideoAsync(video);
        }
    }

    private void OnWindowClosed(object? sender, WindowEventArgs e)
    {
        if (sender is VideoPlayerWindow window)
        {
            window.Activated -= OnWindowActivated;
            window.Closed -= OnWindowClosed;
        }

        _window = null;
        _activeVideo = null;
        _thumbnailService.Resume();
        PlaybackWindowClosed?.Invoke(this, EventArgs.Empty);
    }
}
