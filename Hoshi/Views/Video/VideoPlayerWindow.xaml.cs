using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;
using Hoshi.Views.Dictionary;

namespace Hoshi.Views.Video;

public sealed partial class VideoPlayerWindow : Window
{
    private const double SeekStepSeconds = 5;
    private const double VolumeStep = 5;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly IVideoPlaybackEngine _playbackEngine;
    private readonly IVideoMiningMediaExtractor _mediaExtractor;
    private readonly DispatcherTimer _positionTimer = new();
    private DictionaryPopupOverlay? _popupOverlay;
    private VideoItem? _pendingVideo;
    private IntPtr _parentHwnd;
    private IntPtr _videoHwnd;
    private bool _isLoaded;
    private bool _isPaused;
    private bool _isFullScreen;
    private bool _isScrubbing;
    private bool _isTicking;
    private bool _isSubtitleSettingsOpen;
    private bool _isUpdatingVolume;
    private bool _isUpdatingHardwareDecoding;

    public VideoPlayerViewModel ViewModel { get; }

    public VideoPlayerWindow()
    {
        InitializeComponent();
        ViewModel = App.GetService<VideoPlayerViewModel>();
        _playbackEngine = App.GetService<IVideoPlaybackEngine>();
        _mediaExtractor = App.GetService<IVideoMiningMediaExtractor>();
        ProgressSlider.Minimum = 0;
        VolumeSlider.Minimum = 0;
        VolumeSlider.Maximum = 130;
        SubtitleFontSizeSlider.Minimum = 14;
        SubtitleFontSizeSlider.Maximum = 36;

        Title = "Hoshi Video";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1100, 720));

        _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
        _positionTimer.Tick += OnPositionTimerTick;

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(RootGrid_PointerWheelChanged), true);
        RootGrid.Loaded += OnLoaded;
        RootGrid.SizeChanged += (_, _) => PositionSubtitleSettingsPopup();
        BottomChrome.SizeChanged += (_, _) => PositionSubtitleSettingsPopup();
        Closed += OnClosed;
        VideoSurface.SizeChanged += (_, _) => PositionVideoHost();
    }

    public async Task OpenVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        _pendingVideo = video;
        if (!_isLoaded)
            return;

        await OpenPendingVideoAsync(ct);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _videoHwnd = CreateWindowExW(
                0,
                "STATIC",
                "",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0,
                0,
                100,
                100,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            PositionVideoHost();

            await _playbackEngine.InitializeAsync(_videoHwnd);
            await _playbackEngine.SetHardwareDecodingAsync(ViewModel.HardwareDecodingEnabled);
            await _playbackEngine.SetVolumeAsync(ViewModel.Volume);
            _isLoaded = true;
            _positionTimer.Start();
            RootGrid.Focus(FocusState.Programmatic);
            await OpenPendingVideoAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task OpenPendingVideoAsync(CancellationToken ct = default)
    {
        if (_pendingVideo == null)
            return;

        var video = _pendingVideo;
        _pendingVideo = null;
        await ViewModel.LoadVideoAsync(video, ct);
        await _playbackEngine.OpenAsync(video.FilePath, video.SubtitlePath, ct);
        _isPaused = false;
        PlayPauseIcon.Glyph = "\uE769";
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
            ViewModel.StatusText = _isPaused ? "Paused" : "Playing";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async void RewindButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekRelativeAsync(-SeekStepSeconds);
    }

    private async void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        await SeekRelativeAsync(SeekStepSeconds);
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

    private void SubtitleStyleButton_Click(object sender, RoutedEventArgs e)
    {
        _isSubtitleSettingsOpen = !_isSubtitleSettingsOpen;
        if (_isSubtitleSettingsOpen)
        {
            PositionSubtitleSettingsPopup();
            SubtitleSettingsPopup.IsOpen = true;
        }
        else
        {
            SubtitleSettingsPopup.IsOpen = false;
        }

        RootGrid.Focus(FocusState.Programmatic);
    }

    private void SubtitleSettingsPopup_Closed(object? sender, object e)
    {
        _isSubtitleSettingsOpen = false;
    }

    private void PositionSubtitleSettingsPopup()
    {
        if (!_isSubtitleSettingsOpen && !SubtitleSettingsPopup.IsOpen)
            return;

        SubtitleSettingsPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = SubtitleSettingsPanel.DesiredSize;
        var right = 16;
        var bottom = Math.Max(12, BottomChrome.ActualHeight + 12);
        SubtitleSettingsPopup.HorizontalOffset = Math.Max(16, RootGrid.ActualWidth - desired.Width - right);
        SubtitleSettingsPopup.VerticalOffset = Math.Max(16, RootGrid.ActualHeight - desired.Height - bottom);
    }

    private async void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingVolume || !_isLoaded)
            return;

        await SetVolumeAsync(e.NewValue);
    }

    private void SubtitleFontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null)
            return;

        SetSubtitleFontSize(e.NewValue);
    }

    private async void HardwareDecodingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || !_isLoaded || _isUpdatingHardwareDecoding)
            return;

        await SetHardwareDecodingAsync(HardwareDecodingToggle.IsOn);
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

    private void SetSubtitleFontSize(double value)
    {
        var fontSize = Math.Clamp(value, 14, 36);
        ViewModel.SubtitleFontSize = fontSize;
        ViewModel.RefreshSubtitlePanelHeight();
        SubtitleFontSizeSlider.Value = fontSize;
        ViewModel.StatusText = $"Subtitle size {fontSize:0}";
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

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        AppWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);
        FullScreenIcon.Glyph = _isFullScreen ? "\uE73F" : "\uE740";
        ViewModel.StatusText = _isFullScreen ? "Full screen" : "Windowed";
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void LookupButton_Click(object sender, RoutedEventArgs e)
    {
        await LookupCurrentSubtitleAsync();
    }

    private async Task LookupCurrentSubtitleAsync()
    {
        try
        {
            var selected = SubtitleTextBox.SelectedText;
            var query = string.IsNullOrWhiteSpace(selected)
                ? ViewModel.CurrentSubtitleText
                : selected;
            var sentenceOffset = string.IsNullOrWhiteSpace(selected)
                ? 0
                : SubtitleTextBox.SelectionStart;
            if (string.IsNullOrWhiteSpace(query))
                return;

            ViewModel.StatusText = "Looking up subtitle";
            await _playbackEngine.SetPausedAsync(true);
            _isPaused = true;
            PlayPauseIcon.Glyph = "\uE768";

            var (screenshotPath, audioClipPath) = await CaptureMiningMediaAsync();
            var request = await ViewModel.CreateLookupRequestAsync(
                query,
                screenshotPath,
                audioClipPath,
                sentenceOffset);
            if (request == null)
            {
                ViewModel.StatusText = "No dictionary results";
                return;
            }

            var point = LookupButton.TransformToVisual(PopupOverlayCanvas)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var overlay = EnsurePopupOverlay();
            _ = overlay.PrewarmAsync(RootGrid.XamlRoot);
            ViewModel.StatusText = "Lookup opened";
            await overlay.ShowLookupAsync(
                request.Results,
                request.Styles,
                request.DisplaySettings,
                point.X,
                point.Y,
                LookupButton.ActualWidth,
                LookupButton.ActualHeight,
                RootGrid.XamlRoot,
                isVertical: false,
                request.Theme,
                request.AudioSettings,
                request.AnkiSettings,
                request.MiningContext);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task<(string? screenshotPath, string? audioClipPath)> CaptureMiningMediaAsync()
    {
        if (ViewModel.CurrentVideo == null)
            return (null, null);

        var mediaDir = Path.Combine(AppDataHelper.GetDataPath(), "VideoMining");
        Directory.CreateDirectory(mediaDir);

        var screenshotPath = Path.Combine(
            mediaDir,
            VideoMiningMediaNaming.CreateScreenshotFilename(
                ViewModel.CurrentVideo.FilePath,
                ViewModel.CurrentPosition));
        screenshotPath = await _playbackEngine.CaptureScreenshotAsync(screenshotPath);

        string? audioClipPath = null;
        if (ViewModel.CurrentCue != null)
        {
            var target = Path.Combine(
                mediaDir,
                VideoMiningMediaNaming.CreateAudioClipFilename(
                    ViewModel.CurrentVideo.FilePath,
                    ViewModel.CurrentCue.Start,
                    ViewModel.CurrentCue.End));
            audioClipPath = await _mediaExtractor.ExportAudioClipAsync(
                ViewModel.CurrentVideo.FilePath,
                target,
                ViewModel.CurrentCue.Start,
                ViewModel.CurrentCue.End);
        }

        return (screenshotPath, audioClipPath);
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay != null)
            return _popupOverlay;

        _popupOverlay = new DictionaryPopupOverlay();
        _popupOverlay.UseCanvas(PopupOverlayCanvas);
        return _popupOverlay;
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
                ViewModel.UpdatePosition(position, duration);
        }
        catch
        {
        }
        finally
        {
            _isTicking = false;
        }
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isScrubbing = true;
    }

    private async void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
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
        RootGrid.Focus(FocusState.Pointer);
    }

    private async void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        e.Handled = true;
        await SetVolumeAsync(ViewModel.Volume + (delta > 0 ? VolumeStep : -VolumeStep));
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                e.Handled = true;
                await TogglePlayPauseAsync();
                break;
            case VirtualKey.Left:
                e.Handled = true;
                await SeekRelativeAsync(-SeekStepSeconds);
                break;
            case VirtualKey.Right:
                e.Handled = true;
                await SeekRelativeAsync(SeekStepSeconds);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                await SetVolumeAsync(ViewModel.Volume + VolumeStep);
                break;
            case VirtualKey.Down:
                e.Handled = true;
                await SetVolumeAsync(ViewModel.Volume - VolumeStep);
                break;
            case VirtualKey.PageUp:
                e.Handled = true;
                await SeekToSubtitleAsync(ViewModel.GetPreviousSubtitleStart());
                break;
            case VirtualKey.PageDown:
                e.Handled = true;
                await SeekToSubtitleAsync(ViewModel.GetNextSubtitleStart());
                break;
            case VirtualKey.F:
                e.Handled = true;
                ToggleFullScreen();
                break;
            case VirtualKey.F2:
            case VirtualKey.F9:
                e.Handled = true;
                await LookupCurrentSubtitleAsync();
                break;
            case VirtualKey.F8:
                e.Handled = true;
                await SetHardwareDecodingAsync(!ViewModel.HardwareDecodingEnabled);
                break;
            case VirtualKey.H:
                e.Handled = true;
                await SetHardwareDecodingAsync(!ViewModel.HardwareDecodingEnabled);
                break;
            case VirtualKey.L:
                e.Handled = true;
                await LookupCurrentSubtitleAsync();
                break;
            case VirtualKey.Add:
                e.Handled = true;
                SetSubtitleFontSize(ViewModel.SubtitleFontSize + 1);
                break;
            case VirtualKey.Subtract:
                e.Handled = true;
                SetSubtitleFontSize(ViewModel.SubtitleFontSize - 1);
                break;
            case VirtualKey.Escape:
                if (_isSubtitleSettingsOpen)
                {
                    e.Handled = true;
                    SubtitleSettingsPopup.IsOpen = false;
                    break;
                }

                if (_isFullScreen)
                {
                    e.Handled = true;
                    ToggleFullScreen();
                }
                break;
        }
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _positionTimer.Stop();
        _popupOverlay?.Dispose();
        _popupOverlay = null;
        _playbackEngine.Dispose();

        if (_videoHwnd != IntPtr.Zero)
        {
            DestroyWindow(_videoHwnd);
            _videoHwnd = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);
}
