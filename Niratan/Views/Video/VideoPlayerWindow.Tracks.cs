using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Services.Video;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Niratan.Views.Video;

public sealed partial class VideoPlayerWindow
{
    private async Task SelectVideoTrackAsync(VideoTrackInfo track, CancellationToken ct = default)
    {
        try
        {
            if (_isLoaded)
                await _playbackEngine.SelectTrackAsync(VideoTrackType.Video, track.Id, ct);

            ViewModel.SelectVideoTrack(track);
            UpdateVideoTrackSelection();
            await Task.Delay(120, ct);
            await RefreshMediaTracksAsync(ct: ct);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SelectAudioTrackAsync(VideoTrackInfo? track, CancellationToken ct = default)
    {
        try
        {
            if (_isLoaded)
                await _playbackEngine.SelectTrackAsync(VideoTrackType.Audio, track?.Id, ct);

            ViewModel.SelectAudioTrack(track);
            UpdateAudioTrackSelection();
            await Task.Delay(120, ct);
            await RefreshMediaTracksAsync(ct: ct);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SelectSubtitleTrackAsync(VideoTrackInfo? track, CancellationToken ct = default)
    {
        try
        {
            CancelEmbeddedTranscriptLoad();
            if (_isLoaded)
                await _playbackEngine.SelectTrackAsync(VideoTrackType.Subtitle, track?.Id, ct);

            if (track == null)
            {
                ViewModel.ClearSubtitles();
                await RefreshMediaTracksAsync(ct: ct);
                UpdateSubtitleControlAvailability();
                ViewModel.StatusText = "Subtitles off";
                await SaveCurrentVideoProgressAsync(ct);
                return;
            }

            ViewModel.SelectEmbeddedSubtitleTrack(track);
            UpdateSubtitleControlAvailability();
            UpdateSubtitleTrackSelection();
            ViewModel.BeginEmbeddedTranscriptLoad(track);
            StartEmbeddedTranscriptLoad(track, ct);
            await Task.Delay(120, ct);
            await RefreshMediaTracksAsync(ct: ct);
            ViewModel.UpdateEmbeddedSubtitleCue(await _playbackEngine.GetCurrentSubtitleCueAsync(ct));
            await SaveCurrentVideoProgressAsync(ct);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task RefreshMediaTracksAsync(bool waitForSubtitleTrack = false, CancellationToken ct = default)
    {
        IReadOnlyList<VideoTrackInfo> tracks = [];
        for (var attempt = 0; attempt < 15; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            tracks = await _playbackEngine.GetTracksAsync(ct);
            if (tracks.Count > 0 && (!waitForSubtitleTrack || tracks.Any(track => track.Type == VideoTrackType.Subtitle)))
                break;

            await Task.Delay(120, ct);
        }

        ViewModel.ReplaceTracks(tracks);
        UpdateVideoTrackSelection();
        UpdateAudioTrackSelection();
        UpdateSubtitleControlAvailability();
        UpdateSubtitleTrackSelection();
    }

    private async Task SelectInitialEmbeddedSubtitleTrackAsync(CancellationToken ct = default)
    {
        if (ViewModel.SubtitleTracks.Count == 0 || !string.IsNullOrWhiteSpace(ViewModel.PrimarySubtitleName))
            return;

        var track = ViewModel.SubtitleTracks.FirstOrDefault(item => item.IsSelected)
            ?? ViewModel.SubtitleTracks.FirstOrDefault(item => !item.IsImage)
            ?? ViewModel.SubtitleTracks.FirstOrDefault();
        if (track != null)
            await SelectSubtitleTrackAsync(track, ct);
    }

    private void StartEmbeddedTranscriptLoad(VideoTrackInfo track, CancellationToken ct)
    {
        _ = LoadEmbeddedTranscriptAsync(track, ct);
    }

    private async Task LoadEmbeddedTranscriptAsync(
        VideoTrackInfo track,
        CancellationToken ct)
    {
        var videoPath = ViewModel.CurrentVideo?.FilePath;
        if (string.IsNullOrWhiteSpace(videoPath))
            return;

        try
        {
            var result = await _subtitleTranscriptLoadCoordinator.LoadAsync(videoPath, track, ct);
            if (result.WasCancelled || !result.IsCurrent)
                return;

            if (ViewModel.CompleteEmbeddedTranscriptLoad(result.Track, result.Cues))
                ScrollCurrentTranscriptRowIntoView();
        }
        catch (Exception ex)
        {
            if (ViewModel.SelectedSubtitleTrackId == track.Id)
                ViewModel.FailEmbeddedTranscriptLoad(ex.Message);
        }
    }

    private void CancelEmbeddedTranscriptLoad()
    {
        _subtitleTranscriptLoadCoordinator.Cancel();
        ViewModel.CancelEmbeddedTranscriptLoadStatus();
    }

    private void UpdateVideoTrackSelection()
    {
        if (ViewModel == null)
            return;

        _isUpdatingVideoTrackSelection = true;
        VideoTrackListView.Visibility = ViewModel.HasVideoTracks
            ? Visibility.Visible
            : Visibility.Collapsed;
        VideoNoTracksText.Visibility = ViewModel.HasVideoTracks
            ? Visibility.Collapsed
            : Visibility.Visible;
        VideoTrackListView.SelectedItem = ViewModel.VideoTracks
            .FirstOrDefault(track => track.Id == ViewModel.SelectedVideoTrackId);
        _isUpdatingVideoTrackSelection = false;
    }

    private void UpdateAudioTrackSelection()
    {
        if (ViewModel == null)
            return;

        _isUpdatingAudioTrackSelection = true;
        AudioTrackListView.Visibility = ViewModel.HasAudioTracks
            ? Visibility.Visible
            : Visibility.Collapsed;
        AudioNoTracksText.Visibility = ViewModel.HasAudioTracks
            ? Visibility.Collapsed
            : Visibility.Visible;
        AudioTrackListView.SelectedItem = ViewModel.AudioTracks
            .FirstOrDefault(track => track.Id == ViewModel.SelectedAudioTrackId);
        AudioTrackOffIcon.Foreground = new SolidColorBrush(
            ViewModel.SelectedAudioTrackId.HasValue
                ? Windows.UI.Color.FromArgb(0x80, 0x11, 0x11, 0x11)
                : Windows.UI.Color.FromArgb(0xFF, 0x3F, 0x8D, 0xFF));
        _isUpdatingAudioTrackSelection = false;
    }

    private void UpdateSubtitleTrackSelection()
    {
        if (ViewModel == null)
            return;

        _isUpdatingSubtitleTrackSelection = true;
        SubtitleTrackListView.Visibility = ViewModel.HasSubtitleTracks
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubtitleNoTracksText.Visibility = ViewModel.HasSubtitleTracks
            ? Visibility.Collapsed
            : Visibility.Visible;
        SubtitleTrackListView.SelectedItem = ViewModel.SubtitleTracks
            .FirstOrDefault(track => track.Id == ViewModel.SelectedSubtitleTrackId);
        SubtitleTrackOffIcon.Foreground = new SolidColorBrush(
            ViewModel.SelectedSubtitleTrackId.HasValue
                ? Windows.UI.Color.FromArgb(0x80, 0x11, 0x11, 0x11)
                : Windows.UI.Color.FromArgb(0xFF, 0x3F, 0x8D, 0xFF));
        _isUpdatingSubtitleTrackSelection = false;
        UpdateSubtitleControlAvailability();
    }
}
