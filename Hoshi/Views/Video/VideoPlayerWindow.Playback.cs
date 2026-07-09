using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Shortcuts;
using Hoshi.Services.Video;

namespace Hoshi.Views.Video;

public sealed partial class VideoPlayerWindow
{
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_SIZE = 0xF000;
    private const double VideoResizeCornerGutter = 24;

    private enum VideoWindowResizeDirection
    {
        BottomLeft = 7,
        BottomRight = 8,
    }

    private void OpenBottomChromeOverlay()
    {
        if (!BottomChromePopup.IsOpen)
            BottomChromePopup.IsOpen = true;

        PositionBottomChromeOverlay();
        ShowBottomChromeForPointerActivity();
    }

    private void PositionBottomChromeOverlay()
    {
        if (VideoSurface.ActualWidth <= 0 || VideoSurface.ActualHeight <= 0)
            return;

        var point = VideoSurface.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        BottomChromePopupRoot.Width = VideoSurface.ActualWidth;
        BottomChromePopupRoot.Height = VideoSurface.ActualHeight;
        BottomChromePopup.HorizontalOffset = point.X;
        BottomChromePopup.VerticalOffset = point.Y;
    }

    private void MaximizeVideoWindowForTesting()
    {
        if (_isFullScreen)
            return;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();

        PositionBottomChromeOverlay();
        PositionVideoHost();
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlayPauseAsync();
    }

    private async Task TogglePlayPauseAsync()
    {
        try
        {
            _isPaused = !_isPaused;
            await _playbackEngine.SetPausedAsync(_isPaused);
            PlayPauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
            ApplySubtitleAppearance();
            ViewModel.StatusText = _isPaused ? "Paused" : "Playing";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekRelativeAsync(-ViewModel.SeekIntervalSeconds);
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekRelativeAsync(ViewModel.SeekIntervalSeconds);
    }

    private async void PreviousSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekToSubtitleAsync(ViewModel.GetPreviousSubtitleStart());
    }

    private async void NextSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekToSubtitleAsync(ViewModel.GetNextSubtitleStart());
    }

    private async void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
        await Task.CompletedTask;
    }

    private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingVolume || !_isLoaded)
            return;

        await SetVolumeAsync(e.NewValue);
    }

    private async void PlaybackSpeedPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && double.TryParse(tag, out var speed))
            await SetPlaybackSpeedAsync(speed);
    }

    private async void PlaybackSpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingPlaybackSpeed)
            return;

        await SetPlaybackSpeedAsync(e.NewValue);
    }

    private async void PlaybackSpeedNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (ViewModel == null || _isUpdatingPlaybackSpeed || double.IsNaN(args.NewValue))
            return;

        await SetPlaybackSpeedAsync(args.NewValue);
    }

    private async void AudioDelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingAudioDelay)
            return;

        await SetAudioDelayAsync(e.NewValue);
    }

    private async void AudioDelayEarlierButton_Click(object sender, RoutedEventArgs e) =>
        await SetAudioDelayAsync(ViewModel.AudioDelaySeconds - 0.5);

    private async void AudioDelayResetButton_Click(object sender, RoutedEventArgs e) =>
        await SetAudioDelayAsync(0);

    private async void AudioDelayLaterButton_Click(object sender, RoutedEventArgs e) =>
        await SetAudioDelayAsync(ViewModel.AudioDelaySeconds + 0.5);

    private async void SubtitleDelaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleDelay)
            return;

        await SetSubtitleDelayAsync((int)Math.Round(e.NewValue));
    }

    private async void SubtitleDelayNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (ViewModel == null || _isUpdatingSubtitleDelay || double.IsNaN(args.NewValue))
            return;

        await SetSubtitleDelayAsync((int)Math.Round(args.NewValue));
    }

    private async void SubtitleDelayBackLargeButton_Click(object sender, RoutedEventArgs e) =>
        await SetSubtitleDelayAsync(ViewModel.SubtitleDelayMilliseconds - 1000);

    private async void SubtitleDelayBackSmallButton_Click(object sender, RoutedEventArgs e) =>
        await SetSubtitleDelayAsync(ViewModel.SubtitleDelayMilliseconds - 50);

    private async void SubtitleDelayForwardSmallButton_Click(object sender, RoutedEventArgs e) =>
        await SetSubtitleDelayAsync(ViewModel.SubtitleDelayMilliseconds + 50);

    private async void SubtitleDelayForwardLargeButton_Click(object sender, RoutedEventArgs e) =>
        await SetSubtitleDelayAsync(ViewModel.SubtitleDelayMilliseconds + 1000);

    private async void HardwareDecodingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || !_isLoaded || _isUpdatingHardwareDecoding)
            return;

        await SetHardwareDecodingAsync(HardwareDecodingToggle.IsOn);
    }

    private async void DeinterlaceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingDeinterlace)
            return;

        await SetDeinterlaceAsync(DeinterlaceToggle.IsOn);
    }

    private async void HdrEnhancementToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingHdrEnhancement)
            return;

        await SetHDREnhancementAsync(HdrEnhancementToggle.IsOn);
    }

    private async void VideoEqualizerSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingVideoEqualizer)
            return;

        if (sender is Slider { Tag: string adjustment })
            await SetVideoEqualizerAsync(adjustment, e.NewValue);
    }

    private async void VideoEqualizerResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string adjustment })
            await SetVideoEqualizerAsync(adjustment, 0);
    }

    private async void AspectRatioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingAspectRatio)
            return;

        if (AspectRatioComboBox.SelectedItem is ComboBoxItem { Tag: string value })
            await SetAspectRatioAsync(value);
    }

    private async void RotateClockwiseButton_Click(object sender, RoutedEventArgs e)
    {
        await RotateClockwiseAsync();
    }

    private async void LoopFileToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        await SetLoopFileEnabledAsync(LoopFileToggle.IsOn);
    }

    private async void SetABLoopStartButton_Click(object sender, RoutedEventArgs e)
    {
        await SetABLoopStartAsync(ViewModel.CurrentPosition);
    }

    private async void SetABLoopEndButton_Click(object sender, RoutedEventArgs e)
    {
        await SetABLoopEndAsync(ViewModel.CurrentPosition);
    }

    private async void ClearABLoopButton_Click(object sender, RoutedEventArgs e)
    {
        await ClearABLoopAsync();
    }

    private async Task TogglePlayPauseFromVideoClickAsync()
    {
        RestoreVideoKeyboardFocus(FocusState.Pointer);
        await TogglePlayPauseAsync();
    }

    private void RunVideoSurfaceSingleClick()
    {
        _ = TogglePlayPauseFromVideoClickAsync();
    }

    private void ToggleFullScreenFromVideoDoubleClick()
    {
        RestoreVideoKeyboardFocus(FocusState.Pointer);
        ToggleFullScreen();
    }

    private async Task SeekRelativeAsync(double seconds)
    {
        var duration = ViewModel.Duration.TotalSeconds > 0 ? ViewModel.Duration.TotalSeconds : double.MaxValue;
        var target = TimeSpan.FromSeconds(Math.Clamp(
            ViewModel.CurrentPosition.TotalSeconds + seconds,
            0,
            duration));
        await SeekToAsync(target);
    }

    private async Task SeekToSubtitleAsync(TimeSpan? target)
    {
        if (target == null)
            return;

        await SeekToAsync(target.Value);
    }

    private async Task SeekToAsync(TimeSpan target)
    {
        try
        {
            _protectedRestoreFloor = null;
            await _playbackEngine.SeekAsync(target);
            ViewModel.UpdatePosition(target, ViewModel.Duration);
            ViewModel.StatusText = $"Seeked to {ViewModel.PositionText}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetVolumeAsync(double volume)
    {
        try
        {
            var value = Math.Clamp(volume, 0, 130);
            ViewModel.Volume = value;
            _isUpdatingVolume = true;
            VolumeSlider.Value = value;
            InspectorVolumeSlider.Value = value;
            _isUpdatingVolume = false;
            await _playbackEngine.SetVolumeAsync(value);
            ViewModel.StatusText = $"Volume {value:0}%";
        }
        catch (Exception ex)
        {
            _isUpdatingVolume = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetPlaybackSpeedAsync(double speed)
    {
        try
        {
            var value = Math.Clamp(speed, 0.25, 5);
            ViewModel.PlaybackSpeed = value;
            _isUpdatingPlaybackSpeed = true;
            PlaybackSpeedSlider.Value = value;
            PlaybackSpeedNumberBox.Value = value;
            _isUpdatingPlaybackSpeed = false;
            if (_isLoaded)
                await _playbackEngine.SetPlaybackSpeedAsync(value);

            ViewModel.StatusText = $"Speed {value:0.##}x";
        }
        catch (Exception ex)
        {
            _isUpdatingPlaybackSpeed = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetAudioDelayAsync(double seconds)
    {
        try
        {
            var value = Math.Clamp(seconds, -30, 30);
            ViewModel.AudioDelaySeconds = value;
            _isUpdatingAudioDelay = true;
            AudioDelaySlider.Value = value;
            _isUpdatingAudioDelay = false;
            if (_isLoaded)
                await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(value));

            ViewModel.StatusText = $"Audio delay {ViewModel.AudioDelayText}";
        }
        catch (Exception ex)
        {
            _isUpdatingAudioDelay = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetSubtitleDelayAsync(int milliseconds)
    {
        try
        {
            var value = Math.Clamp(milliseconds, -10_000, 10_000);
            ViewModel.SubtitleDelayMilliseconds = value;
            ViewModel.UpdatePosition(ViewModel.CurrentPosition, ViewModel.Duration);
            _isUpdatingSubtitleDelay = true;
            SubtitleDelaySlider.Value = value;
            SubtitleDelayNumberBox.Value = value;
            _isUpdatingSubtitleDelay = false;
            if (_isLoaded)
                await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(value));

            ViewModel.StatusText = $"Subtitle delay {ViewModel.SubtitleDelayText}";
        }
        catch (Exception ex)
        {
            _isUpdatingSubtitleDelay = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetHardwareDecodingAsync(bool enabled)
    {
        try
        {
            ViewModel.HardwareDecodingEnabled = enabled;
            _isUpdatingHardwareDecoding = true;
            HardwareDecodingToggle.IsOn = enabled;
            _isUpdatingHardwareDecoding = false;
            await _playbackEngine.SetHardwareDecodingAsync(enabled);
            ViewModel.StatusText = enabled ? "Hardware decoding on" : "Hardware decoding off";
        }
        catch (Exception ex)
        {
            _isUpdatingHardwareDecoding = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetDeinterlaceAsync(bool enabled)
    {
        try
        {
            ViewModel.DeinterlaceEnabled = enabled;
            _isUpdatingDeinterlace = true;
            DeinterlaceToggle.IsOn = enabled;
            _isUpdatingDeinterlace = false;
            if (_isLoaded)
                await _playbackEngine.SetDeinterlaceAsync(enabled);

            ViewModel.StatusText = enabled ? "Deinterlace on" : "Deinterlace off";
        }
        catch (Exception ex)
        {
            _isUpdatingDeinterlace = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task ApplyVideoEnhancementAsync(CancellationToken ct = default)
    {
        await _playbackEngine.SetHDREnhancementAsync(ViewModel.HdrEnhancementEnabled, ct);
        await _playbackEngine.SetVideoEqualizerAsync("brightness", ViewModel.VideoBrightness, ct);
        await _playbackEngine.SetVideoEqualizerAsync("contrast", ViewModel.VideoContrast, ct);
        await _playbackEngine.SetVideoEqualizerAsync("saturation", ViewModel.VideoSaturation, ct);
        await _playbackEngine.SetVideoEqualizerAsync("gamma", ViewModel.VideoGamma, ct);
        await _playbackEngine.SetVideoEqualizerAsync("hue", ViewModel.VideoHue, ct);
    }

    private async Task SetHDREnhancementAsync(bool enabled)
    {
        try
        {
            ViewModel.SetHDREnhancementEnabled(enabled);
            _isUpdatingHdrEnhancement = true;
            HdrEnhancementToggle.IsOn = enabled;
            _isUpdatingHdrEnhancement = false;
            if (_isLoaded)
                await _playbackEngine.SetHDREnhancementAsync(enabled);

            ViewModel.StatusText = enabled ? "HDR enhancement on" : "HDR enhancement off";
        }
        catch (Exception ex)
        {
            _isUpdatingHdrEnhancement = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetVideoEqualizerAsync(string adjustment, double value)
    {
        try
        {
            ViewModel.SetVideoEqualizer(adjustment, value);
            UpdateVideoEqualizerControls(adjustment);
            if (_isLoaded)
                await _playbackEngine.SetVideoEqualizerAsync(adjustment, GetVideoEqualizerValue(adjustment));

            ViewModel.StatusText = $"Video {GetVideoEqualizerLabel(adjustment)} {GetVideoEqualizerText(adjustment)}";
        }
        catch (Exception ex)
        {
            _isUpdatingVideoEqualizer = false;
            ViewModel.StatusText = ex.Message;
        }
    }

    private void UpdateVideoEqualizerControls(string? adjustment = null)
    {
        _isUpdatingVideoEqualizer = true;
        try
        {
            if (adjustment == null || IsVideoEqualizerAdjustment(adjustment, "brightness"))
                VideoBrightnessSlider.Value = ViewModel.VideoBrightness;
            if (adjustment == null || IsVideoEqualizerAdjustment(adjustment, "contrast"))
                VideoContrastSlider.Value = ViewModel.VideoContrast;
            if (adjustment == null || IsVideoEqualizerAdjustment(adjustment, "saturation"))
                VideoSaturationSlider.Value = ViewModel.VideoSaturation;
            if (adjustment == null || IsVideoEqualizerAdjustment(adjustment, "gamma"))
                VideoGammaSlider.Value = ViewModel.VideoGamma;
            if (adjustment == null || IsVideoEqualizerAdjustment(adjustment, "hue"))
                VideoHueSlider.Value = ViewModel.VideoHue;
        }
        finally
        {
            _isUpdatingVideoEqualizer = false;
        }
    }

    private double GetVideoEqualizerValue(string adjustment) =>
        NormalizeVideoEqualizerAdjustment(adjustment) switch
        {
            "brightness" => ViewModel.VideoBrightness,
            "contrast" => ViewModel.VideoContrast,
            "saturation" => ViewModel.VideoSaturation,
            "gamma" => ViewModel.VideoGamma,
            "hue" => ViewModel.VideoHue,
            _ => 0,
        };

    private string GetVideoEqualizerText(string adjustment) =>
        NormalizeVideoEqualizerAdjustment(adjustment) switch
        {
            "brightness" => ViewModel.VideoBrightnessText,
            "contrast" => ViewModel.VideoContrastText,
            "saturation" => ViewModel.VideoSaturationText,
            "gamma" => ViewModel.VideoGammaText,
            "hue" => ViewModel.VideoHueText,
            _ => "0",
        };

    private static bool IsVideoEqualizerAdjustment(string value, string adjustment) =>
        string.Equals(NormalizeVideoEqualizerAdjustment(value), adjustment, StringComparison.Ordinal);

    private static string GetVideoEqualizerLabel(string adjustment) =>
        NormalizeVideoEqualizerAdjustment(adjustment) switch
        {
            "brightness" => "brightness",
            "contrast" => "contrast",
            "saturation" => "saturation",
            "gamma" => "gamma",
            "hue" => "hue",
            _ => "adjustment",
        };

    private static string NormalizeVideoEqualizerAdjustment(string adjustment) =>
        adjustment.Trim().ToLowerInvariant() switch
        {
            "brightness" => "brightness",
            "contrast" => "contrast",
            "saturation" => "saturation",
            "gamma" => "gamma",
            "hue" => "hue",
            _ => "",
        };

    private async Task SetAspectRatioAsync(string value)
    {
        try
        {
            ViewModel.SetAspectRatio(value);
            UpdateAspectRatioSelection();
            if (_isLoaded)
                await _playbackEngine.SetAspectRatioAsync(ViewModel.AspectRatioValue);

            ViewModel.StatusText = $"Aspect ratio {ViewModel.AspectRatioText}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task RotateClockwiseAsync()
    {
        try
        {
            ViewModel.RotateClockwise();
            if (_isLoaded)
                await _playbackEngine.SetVideoRotationAsync(ViewModel.VideoRotationDegrees);

            ViewModel.StatusText = $"Rotation {ViewModel.VideoRotationText}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private void UpdateAspectRatioSelection()
    {
        _isUpdatingAspectRatio = true;
        for (var index = 0; index < AspectRatioComboBox.Items.Count; index++)
        {
            if (AspectRatioComboBox.Items[index] is ComboBoxItem { Tag: string value }
                && string.Equals(value, ViewModel.AspectRatioValue, StringComparison.Ordinal))
            {
                AspectRatioComboBox.SelectedIndex = index;
                break;
            }
        }

        _isUpdatingAspectRatio = false;
    }

    private async Task SetLoopFileEnabledAsync(bool enabled)
    {
        try
        {
            ViewModel.LoopFileEnabled = enabled;
            LoopFileToggle.IsOn = enabled;
            if (_isLoaded)
                await _playbackEngine.SetFileLoopEnabledAsync(enabled);

            ViewModel.StatusText = enabled ? "Loop file on" : "Loop file off";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetABLoopStartAsync(TimeSpan time)
    {
        try
        {
            ViewModel.SetABLoopStart(time);
            if (_isLoaded)
                await _playbackEngine.SetABLoopAsync(null);

            ViewModel.StatusText = $"A point {VideoMiningContextFactory.FormatTimestamp(ViewModel.PendingABLoopStart ?? time)}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task SetABLoopEndAsync(TimeSpan time)
    {
        try
        {
            var loop = ViewModel.TrySetABLoopEnd(time);
            if (loop == null)
            {
                ViewModel.StatusText = "Set A point first";
                return;
            }

            if (_isLoaded)
            {
                await _playbackEngine.SetABLoopAsync(loop);
                if (ViewModel.ShouldRestartABLoopPlayback(loop))
                    await SeekToAsync(loop.Start);
            }

            ViewModel.StatusText = $"A-B loop {ViewModel.ABLoopText}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task ClearABLoopAsync()
    {
        try
        {
            ViewModel.ClearABLoop();
            if (_isLoaded)
                await _playbackEngine.SetABLoopAsync(null);

            ViewModel.StatusText = "A-B loop cleared";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        AppWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);
        FullScreenIcon.Glyph = _isFullScreen ? "\uE73F" : "\uE740";
        ViewModel.StatusText = _isFullScreen
            ? ResourceStringHelper.GetString("VideoPlayerStatusFullScreen", "Full screen")
            : ResourceStringHelper.GetString("VideoPlayerStatusWindowed", "Windowed");
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnPositionTimerTick(object? sender, object e)
    {
        if (_isTicking)
            return;

        _isTicking = true;
        try
        {
            var position = await _playbackEngine.GetPositionAsync();
            var duration = await _playbackEngine.GetDurationAsync();
            if (!_isScrubbing)
            {
                ViewModel.UpdatePosition(position, duration);
                if (ViewModel.IsEmbeddedSubtitleActive && !ViewModel.HasCompleteEmbeddedTranscript)
                    ViewModel.UpdateEmbeddedSubtitleCue(await _playbackEngine.GetCurrentSubtitleCueAsync());
                await SaveCurrentVideoProgressIfDueAsync();
                await TryAutoPlayNextEpisodeAsync();
            }
        }
        catch
        {
        }
        finally
        {
            _isTicking = false;
        }
    }

    private async Task<VideoPlaybackState> LoadPlaybackStateAsync(VideoItem video, CancellationToken ct = default)
    {
        if (!ViewModel.RememberPlaybackState)
            return VideoPlaybackState.FromVideoItem(video);

        var latest = await _videoLibraryService.GetVideoAsync(video.Id, ct);
        return latest.Value != null
            ? VideoPlaybackState.FromVideoItem(latest.Value)
            : VideoPlaybackState.FromVideoItem(video);
    }

    private async Task<bool> RestorePlaybackStateIfNeededAsync(
        VideoPlaybackState state,
        TimeSpan? openedStartPosition,
        CancellationToken ct = default)
    {
        if (!ViewModel.RememberPlaybackState)
            return false;

        var restoredSubtitle = await RestoreSubtitleSelectionAsync(state.SubtitleSelection, ct);
        if (openedStartPosition != null)
        {
            var duration = await _playbackEngine.GetDurationAsync(ct);
            if (duration <= TimeSpan.Zero && state.DurationSeconds > 0)
                duration = TimeSpan.FromSeconds(state.DurationSeconds);

            ViewModel.UpdatePosition(openedStartPosition.Value, duration);
            ViewModel.StatusText = ResourceStringHelper.FormatString(
                "VideoPlayerRestoredStatus",
                "Restored to {0}",
                ViewModel.PositionText);
            return restoredSubtitle;
        }

        var target = await VideoPlaybackRestoreWaiter.WaitForRestoreTargetAsync(
            state,
            _playbackEngine.GetDurationAsync,
            ct: ct);
        if (target == null)
            return restoredSubtitle;

        _protectedRestoreFloor = VideoProgressSaveGuard.CreateProtectedRestoreFloor(target.Position);
        await _playbackEngine.SeekAsync(target.Position, ct);
        var seekApplied = await VideoPlaybackRestoreWaiter.WaitForSeekPositionAsync(
            target.Position,
            _playbackEngine.GetPositionAsync,
            ct: ct);
        if (!seekApplied)
        {
            ViewModel.StatusText = ResourceStringHelper.FormatString(
                "VideoPlayerRestorePendingStatus",
                "Could not verify restore to {0}. Progress will be protected until playback catches up.",
                VideoMiningContextFactory.FormatTimestamp(target.Position));
            return restoredSubtitle;
        }

        _protectedRestoreFloor = null;
        ViewModel.UpdatePosition(target.Position, target.Duration);
        ViewModel.StatusText = ResourceStringHelper.FormatString(
            "VideoPlayerRestoredStatus",
            "Restored to {0}",
            ViewModel.PositionText);
        return restoredSubtitle;
    }

    private async Task<bool> RestoreSubtitleSelectionAsync(
        VideoSubtitleSelection selection,
        CancellationToken ct = default)
    {
        switch (selection.Kind)
        {
            case VideoSubtitleSelectionKind.Off:
                await SelectSubtitleTrackAsync(null, ct);
                return true;
            case VideoSubtitleSelectionKind.ExternalFile:
                if (!string.IsNullOrWhiteSpace(selection.ExternalPath) && File.Exists(selection.ExternalPath))
                {
                    await LoadExternalSubtitleAsync(selection.ExternalPath, ct);
                    return true;
                }

                return false;
            case VideoSubtitleSelectionKind.EmbeddedTrack:
                var track = ViewModel.SubtitleTracks.FirstOrDefault(item => item.Id == selection.TrackId)
                            ?? ViewModel.SubtitleTracks.FirstOrDefault(item =>
                                !string.IsNullOrWhiteSpace(selection.TrackName)
                                && string.Equals(item.DisplayName, selection.TrackName, StringComparison.OrdinalIgnoreCase));
                if (track != null)
                {
                    await SelectSubtitleTrackAsync(track, ct);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private async Task SaveCurrentVideoProgressIfDueAsync(CancellationToken ct = default)
    {
        if (_isOpeningVideo || !ViewModel.RememberPlaybackState || ViewModel.CurrentVideo == null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastProgressSaveAt < TimeSpan.FromSeconds(5))
            return;

        _lastProgressSaveAt = now;
        await SaveCurrentVideoProgressAsync(ct);
    }

    private async Task SaveCurrentVideoProgressAsync(CancellationToken ct = default)
    {
        if (_isOpeningVideo || !ViewModel.RememberPlaybackState || ViewModel.CurrentVideo == null)
            return;

        if (!VideoProgressSaveGuard.ShouldSaveProgress(ViewModel.CurrentPosition, _protectedRestoreFloor))
            return;

        if (_protectedRestoreFloor != null)
            _protectedRestoreFloor = null;

        var selection = ViewModel.GetCurrentSubtitleSelection();
        var shouldPersistProgress = VideoPlaybackState.ShouldPersistProgress(
            ViewModel.CurrentPosition,
            ViewModel.Duration);
        var positionSeconds = ViewModel.CurrentPosition.TotalSeconds;
        var durationSeconds = ViewModel.Duration.TotalSeconds;
        if (!shouldPersistProgress && selection.Kind == VideoSubtitleSelectionKind.None)
            return;

        if (!shouldPersistProgress)
        {
            var latest = await _videoLibraryService.GetVideoAsync(ViewModel.CurrentVideo.Id, ct);
            positionSeconds = latest.Value?.LastPositionSeconds ?? 0;
            durationSeconds = latest.Value?.DurationSeconds ?? 0;
        }
        else if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
        {
            durationSeconds = 0;
        }

        await _videoLibraryService.SavePlaybackStateAsync(
            ViewModel.CurrentVideo.Id,
            new VideoPlaybackState(
                positionSeconds,
                durationSeconds,
                selection),
            ct);
    }

    private async Task TryAutoPlayNextEpisodeAsync(CancellationToken ct = default)
    {
        if (!ViewModel.AutoPlayNextEpisode
            || _isAutoPlayingNextEpisode
            || ViewModel.CurrentVideo == null
            || ViewModel.LoopFileEnabled
            || ViewModel.ABLoop != null)
        {
            return;
        }

        var durationSeconds = ViewModel.Duration.TotalSeconds;
        if (durationSeconds <= 0)
            return;

        if (ViewModel.CurrentPosition.TotalSeconds < Math.Max(0, durationSeconds - 0.75))
            return;

        var next = FindNextEpisodeRow();
        if (next == null)
            return;

        _isAutoPlayingNextEpisode = true;
        await SaveCurrentVideoProgressAsync(ct);
        var playlist = new List<VideoItem>();
        foreach (var row in ViewModel.EpisodeRows)
            playlist.Add(row.Video);

        await OpenVideoAsync(next.Video, playlist, ct);
    }

    private VideoEpisodeRow? FindNextEpisodeRow()
    {
        for (var i = 0; i < ViewModel.EpisodeRows.Count; i++)
        {
            if (!ViewModel.EpisodeRows[i].IsCurrent)
                continue;

            return i + 1 < ViewModel.EpisodeRows.Count
                ? ViewModel.EpisodeRows[i + 1]
                : null;
        }

        return null;
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isScrubbing = true;
        ProgressSlider.CapturePointer(e.Pointer);
    }

    private async void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ProgressSlider.ReleasePointerCapture(e.Pointer);
        await CommitSeekAsync();
    }

    private async void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        await CommitSeekAsync();
    }

    private async Task CommitSeekAsync()
    {
        if (!_isScrubbing)
            return;

        _isScrubbing = false;
        try
        {
            var target = TimeSpan.FromSeconds(Math.Clamp(
                ProgressSlider.Value,
                0,
                Math.Max(0, ViewModel.Duration.TotalSeconds)));
            _protectedRestoreFloor = null;
            await _playbackEngine.SeekAsync(target);
            ViewModel.UpdatePosition(target, ViewModel.Duration);
            ViewModel.StatusText = $"Seeked to {ViewModel.PositionText}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private void VideoSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryBeginWindowResizeFromRootElementPointer(e, VideoSurface))
            return;

        var point = e.GetCurrentPoint(VideoSurface);
        if (point.Properties.IsLeftButtonPressed)
        {
            RunVideoSurfaceSingleClick();
            e.Handled = true;
            return;
        }

        RestoreVideoKeyboardFocus(FocusState.Pointer);
    }

    private void BottomChromePopupRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryBeginWindowResizeFromPopupPointer(e, BottomChromePopupRoot))
            return;

        if (TryDismissLookupPopupFromOutsidePointer(e))
            return;

        ShowBottomChromeForPointerActivity();

        if (IsVideoOverlayInteractiveSource(e.OriginalSource))
            return;

        var point = e.GetCurrentPoint(BottomChromePopupRoot);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        RunVideoSurfaceSingleClick();
        e.Handled = true;
    }

    private void BottomChromePopupRoot_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsVideoOverlayInteractiveSource(e.OriginalSource))
            return;

        ToggleFullScreenFromVideoDoubleClick();
        e.Handled = true;
    }

    private void BottomChromePopupRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowBottomChromeForPointerActivity();
    }

    private void BottomChromePopupRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HideBottomChromeForPointerLeave();
    }

    private void BottomChromeAutoHideTimer_Tick(object? sender, object e)
    {
        HideBottomChromeForInactivity();
    }

    private void ShowBottomChromeForPointerActivity()
    {
        _bottomChromeAutoHideState.ShowForPointerActivity();
        ApplyBottomChromeVisibility();
        RestartBottomChromeAutoHideTimer();
    }

    private void HideBottomChromeForPointerLeave()
    {
        _bottomChromeAutoHideTimer.Stop();
        _bottomChromeAutoHideState.HideForPointerLeave();
        ApplyBottomChromeVisibility();
    }

    private void HideBottomChromeForInactivity()
    {
        _bottomChromeAutoHideTimer.Stop();
        _bottomChromeAutoHideState.HideForInactivity();
        ApplyBottomChromeVisibility();
    }

    private void RestartBottomChromeAutoHideTimer()
    {
        _bottomChromeAutoHideTimer.Stop();
        _bottomChromeAutoHideTimer.Start();
    }

    private void ApplyBottomChromeVisibility()
    {
        if (_bottomChromeAutoHideState.IsVisible)
        {
            BottomChrome.Visibility = Visibility.Visible;
        }
        else
        {
            BottomChrome.Visibility = Visibility.Collapsed;
        }
    }

    private void BottomChrome_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryBeginWindowResizeFromPopupPointer(e, BottomChrome))
            return;

        RestoreVideoKeyboardFocus(FocusState.Pointer);
        e.Handled = true;
    }

    private void SubtitlePanelBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RestoreVideoKeyboardFocusAfterSubtitleInteraction();
    }

    private void SubtitleWebView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RestoreVideoKeyboardFocusAfterSubtitleInteraction();
    }

    private bool IsVideoOverlayInteractiveSource(object? source)
    {
        if (source is not DependencyObject dependencyObject)
            return false;

        return IsDescendantOf(dependencyObject, BottomChrome)
            || IsDescendantOf(dependencyObject, SubtitlePanelBorder)
            || IsDescendantOf(dependencyObject, VideoDictionaryPanelChrome)
            || IsDescendantOf(dependencyObject, PopupOverlayCanvas);
    }

    private bool TryDismissLookupPopupFromOutsidePointer(PointerRoutedEventArgs e)
    {
        if (!_isLookupPopupVisible || _popupOverlay is null)
            return false;

        if (e.OriginalSource is DependencyObject source
            && IsDescendantOf(source, VideoDictionaryPanelChrome))
        {
            return false;
        }

        _popupOverlay.Dismiss();
        e.Handled = true;
        return true;
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        var current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void VideoSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        await HandleVideoSurfacePointerWheelChangedAsync(e, e.GetCurrentPoint(VideoSurface));
    }

    private async void BottomChromePopupRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsVideoOverlayInteractiveSource(e.OriginalSource))
            return;

        var point = e.GetCurrentPoint(BottomChromePopupRoot);
        var position = BottomChromePopupRoot
            .TransformToVisual(VideoSurface)
            .TransformPoint(point.Position);
        await HandleVideoSurfacePointerWheelChangedAsync(
            e,
            point.Properties.MouseWheelDelta,
            position.X,
            position.Y);
    }

    private async Task HandleVideoSurfacePointerWheelChangedAsync(
        PointerRoutedEventArgs e,
        PointerPoint point)
    {
        await HandleVideoSurfacePointerWheelChangedAsync(
            e,
            point.Properties.MouseWheelDelta,
            point.Position.X,
            point.Position.Y);
    }

    private async Task HandleVideoSurfacePointerWheelChangedAsync(
        PointerRoutedEventArgs e,
        int mouseWheelDelta,
        double pointerX,
        double pointerY)
    {
        var adjustment = VideoSurfaceVolumeScroll.TryGetAdjustment(
            deltaX: 0,
            deltaY: mouseWheelDelta,
            hasPreciseScrollingDeltas: false,
            pointerX: pointerX,
            pointerY: pointerY,
            surfaceWidth: VideoSurface.ActualWidth,
            surfaceHeight: VideoSurface.ActualHeight,
            isEnabled: _isLoaded && ViewModel.CurrentVideo != null,
            hasActivePopup: _isLookupPopupVisible,
            excludedRects: GetVideoSurfaceVolumeScrollExcludedRects());
        if (adjustment == null)
            return;

        e.Handled = true;
        await SetVolumeAsync(ViewModel.Volume + adjustment.Value);
    }

    private IEnumerable<VideoSurfaceVolumeScroll.ExcludedRect> GetVideoSurfaceVolumeScrollExcludedRects()
    {
        if (BottomChromePopup.IsOpen
            && TryCreateVolumeScrollExcludedRect(BottomChrome, out var bottomChromeRect))
        {
            yield return bottomChromeRect;
        }

    }

    private bool TryCreateVolumeScrollExcludedRect(
        FrameworkElement element,
        out VideoSurfaceVolumeScroll.ExcludedRect rect)
    {
        rect = default;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        try
        {
            var origin = element.TransformToVisual(VideoSurface)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            rect = new VideoSurfaceVolumeScroll.ExcludedRect(
                origin.X,
                origin.Y,
                element.ActualWidth,
                element.ActualHeight);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _ = TryBeginWindowResizeFromRootElementPointer(e, RootGrid);
    }

    private bool TryBeginWindowResizeFromRootElementPointer(
        PointerRoutedEventArgs e,
        FrameworkElement element)
    {
        var point = e.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed)
            return false;

        var rootPoint = ReferenceEquals(element, RootGrid)
            ? point.Position
            : element.TransformToVisual(RootGrid)
                .TransformPoint(point.Position);
        return TryBeginWindowResizeFromRootPoint(rootPoint.X, rootPoint.Y, e);
    }

    private bool TryBeginWindowResizeFromPopupPointer(
        PointerRoutedEventArgs e,
        FrameworkElement element)
    {
        var point = e.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed)
            return false;

        var popupPoint = ReferenceEquals(element, BottomChromePopupRoot)
            ? point.Position
            : element.TransformToVisual(BottomChromePopupRoot)
                .TransformPoint(point.Position);
        return TryBeginWindowResizeFromRootPoint(
            BottomChromePopup.HorizontalOffset + popupPoint.X,
            BottomChromePopup.VerticalOffset + popupPoint.Y,
            e);
    }

    private bool TryBeginWindowResizeFromRootPoint(
        double pointerX,
        double pointerY,
        PointerRoutedEventArgs? e = null)
    {
        if (!TryGetBottomCornerResizeDirection(
                pointerX,
                pointerY,
                RootGrid.ActualWidth,
                RootGrid.ActualHeight,
                VideoResizeCornerGutter,
                out var direction))
        {
            return false;
        }

        if (!BeginWindowResize(direction))
            return false;

        if (e != null)
            e.Handled = true;

        return true;
    }

    private static bool TryGetBottomCornerResizeDirection(
        double pointerX,
        double pointerY,
        double width,
        double height,
        double gutter,
        out VideoWindowResizeDirection direction)
    {
        direction = default;
        if (width <= 0 || height <= 0 || gutter <= 0)
            return false;

        var cornerGutter = Math.Min(gutter, Math.Min(width, height) / 2);
        if (pointerY < height - cornerGutter || pointerY > height)
            return false;

        if (pointerX >= 0 && pointerX <= cornerGutter)
        {
            direction = VideoWindowResizeDirection.BottomLeft;
            return true;
        }

        if (pointerX >= width - cornerGutter && pointerX <= width)
        {
            direction = VideoWindowResizeDirection.BottomRight;
            return true;
        }

        return false;
    }

    private bool TryBeginWindowResizeFromVideoHostLParam(IntPtr lParam)
    {
        if (_parentHwnd == IntPtr.Zero)
            return false;

        var scale = GetDpiForWindow(_parentHwnd) / 96.0;
        if (scale <= 0)
            scale = 1;

        return TryBeginWindowResizeFromRootPoint(
            GetSignedLoWord(lParam) / scale,
            GetSignedHiWord(lParam) / scale);
    }

    private bool BeginWindowResize(VideoWindowResizeDirection direction)
    {
        if (_parentHwnd == IntPtr.Zero || _isFullScreen)
            return false;

        ReleaseCapture();
        _ = SendMessageW(
            _parentHwnd,
            WM_SYSCOMMAND,
            new UIntPtr(SC_SIZE + (uint)direction),
            IntPtr.Zero);
        return true;
    }

    private static int GetSignedLoWord(IntPtr value) =>
        (short)((long)value & 0xFFFF);

    private static int GetSignedHiWord(IntPtr value) =>
        (short)(((long)value >> 16) & 0xFFFF);

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryResolveVideoShortcut(e.Key, out var action))
            return;

        e.Handled = true;
        await HandleVideoShortcutActionAsync(action!.Id);
    }

    private bool TryResolveVideoShortcut(VirtualKey key, out ShortcutAction? action)
    {
        var binding = KeyboardShortcutBinding.FromVirtualKey(
            key,
            ShortcutInputMapper.GetCurrentModifiers());
        return _shortcutService.TryResolve(ShortcutScope.Video, binding, out action);
    }

    private async Task HandleVideoShortcutActionAsync(string actionId)
    {
        switch (actionId)
        {
            case VideoShortcutActions.PlayPauseId:
                await TogglePlayPauseAsync();
                break;
            case VideoShortcutActions.SeekBackwardId:
                await SeekRelativeAsync(-ViewModel.SeekIntervalSeconds);
                break;
            case VideoShortcutActions.SeekForwardId:
                await SeekRelativeAsync(ViewModel.SeekIntervalSeconds);
                break;
            case VideoShortcutActions.VolumeUpId:
                await SetVolumeAsync(ViewModel.Volume + VolumeStep);
                break;
            case VideoShortcutActions.VolumeDownId:
                await SetVolumeAsync(ViewModel.Volume - VolumeStep);
                break;
            case VideoShortcutActions.PreviousSubtitleId:
                await SeekToSubtitleAsync(ViewModel.GetPreviousSubtitleStart());
                break;
            case VideoShortcutActions.NextSubtitleId:
                await SeekToSubtitleAsync(ViewModel.GetNextSubtitleStart());
                break;
            case VideoShortcutActions.ToggleSubtitlesId:
                ToggleSubtitlesVisible();
                break;
            case VideoShortcutActions.CycleSubtitleTrackId:
                await CycleSubtitleTrackAsync();
                break;
            case VideoShortcutActions.ToggleFullscreenId:
                ToggleFullScreen();
                break;
            case VideoShortcutActions.LookupSubtitleId:
                await LookupCurrentSubtitleAsync();
                break;
            case VideoShortcutActions.ToggleHardwareDecodingId:
                await SetHardwareDecodingAsync(!ViewModel.HardwareDecodingEnabled);
                break;
            case VideoShortcutActions.IncreaseSubtitleSizeId:
                SetSubtitleFontSize(ViewModel.SubtitleFontSize + 1);
                break;
            case VideoShortcutActions.DecreaseSubtitleSizeId:
                SetSubtitleFontSize(ViewModel.SubtitleFontSize - 1);
                break;
            case VideoShortcutActions.CloseOverlayOrFullscreenId:
                if (_isInspectorOpen)
                {
                    _isInspectorOpen = false;
                    InspectorPanel.Visibility = Visibility.Collapsed;
                    RefreshVideoLayoutAfterInspectorChanged();
                    break;
                }

                if (_isFullScreen)
                {
                    ToggleFullScreen();
                }
                break;
        }
    }

    private void ToggleSubtitlesVisible()
    {
        if (ViewModel.AreSubtitlesVisible)
            CancelEmbeddedTranscriptLoad();

        ViewModel.ToggleSubtitlesVisible();
        ApplySubtitleAppearance();
    }

    private async Task CycleSubtitleTrackAsync()
    {
        if (ViewModel.SubtitleTracks.Count == 0)
        {
            ViewModel.StatusText = "No subtitle tracks";
            return;
        }

        var nextTrack = ViewModel.GetNextSubtitleTrackForCycle();
        if (nextTrack == null)
        {
            ViewModel.ToggleSubtitlesVisible();
            if (_isLoaded)
                await _playbackEngine.SelectTrackAsync(VideoTrackType.Subtitle, null);

            UpdateSubtitleTrackSelection();
            return;
        }

        await SelectSubtitleTrackAsync(nextTrack);
    }

    private void PositionVideoHost()
    {
        if (_videoHwnd == IntPtr.Zero || _parentHwnd == IntPtr.Zero)
            return;

        var point = VideoSurface.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = GetDpiForWindow(_parentHwnd) / 96.0;
        SetWindowPos(
            _videoHwnd,
            IntPtr.Zero,
            (int)Math.Round(point.X * scale),
            (int)Math.Round(point.Y * scale),
            Math.Max(1, (int)Math.Round(VideoSurface.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(VideoSurface.ActualHeight * scale)),
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private IntPtr VideoHostSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr idSubclass,
        UIntPtr refData)
    {
        switch (message)
        {
            case WM_SETFOCUS:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                DispatcherQueue.TryEnqueue(() => RestoreVideoKeyboardFocus(FocusState.Pointer));
                break;
            case WM_LBUTTONDOWN:
                if (TryBeginWindowResizeFromVideoHostLParam(lParam))
                    return IntPtr.Zero;

                DispatcherQueue.TryEnqueue(RunVideoSurfaceSingleClick);
                return IntPtr.Zero;
            case WM_LBUTTONDBLCLK:
                DispatcherQueue.TryEnqueue(() => ToggleFullScreenFromVideoDoubleClick());
                return IntPtr.Zero;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                var key = (VirtualKey)wParam.ToUInt32();
                if (TryResolveVideoShortcut(key, out var action))
                {
                    DispatcherQueue.TryEnqueue(async () => await HandleVideoShortcutActionAsync(action!.Id));
                    return IntPtr.Zero;
                }

                break;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void RestoreVideoKeyboardFocus(FocusState focusState = FocusState.Programmatic)
    {
        if (_parentHwnd != IntPtr.Zero)
        {
            SetForegroundWindow(_parentHwnd);
            SetFocus(_parentHwnd);
        }

        RootGrid.Focus(focusState);
    }

    private void RestoreVideoKeyboardFocusAfterSubtitleInteraction()
    {
        DispatcherQueue.TryEnqueue(() => RestoreVideoKeyboardFocus(FocusState.Pointer));
    }

    private void SubtitleWebView_GotFocus(object sender, RoutedEventArgs e)
    {
        RestoreVideoKeyboardFocusAfterSubtitleInteraction();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);
}
