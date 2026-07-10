using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Anki;
using Hoshi.Models.Shortcuts;
using Hoshi.Services.Anki;
using Hoshi.Services.Shortcuts;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;
using Hoshi.Views.Dictionary;

namespace Hoshi.Views.Video;

public sealed partial class VideoPlayerWindow : Window
{
    private const double VolumeStep = 5;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const double DefaultSubtitleFontSize = 36;
    private const int DefaultSubtitleFontWeight = 700;
    private static readonly UIntPtr VideoHostSubclassId = new(1);

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr idSubclass,
        UIntPtr refData);

    private enum VideoInspectorTab
    {
        MiningHistory,
        SubtitleList,
        Chapters,
        Video,
        Audio,
        Subtitles,
    }

    private readonly IVideoPlaybackEngine _playbackEngine;
    private readonly IVideoLibraryService _videoLibraryService;
    private readonly IVideoMiningHistoryStore _miningHistoryStore;
    private readonly IVideoMiningMediaExtractor _mediaExtractor;
    private readonly IVideoSubtitleTranscriptExtractor _subtitleTranscriptExtractor;
    private readonly IShortcutService _shortcutService;
    private readonly VideoSubtitleTranscriptLoadCoordinator _subtitleTranscriptLoadCoordinator;
    private readonly DispatcherTimer _positionTimer = new();
    private readonly DispatcherTimer _bottomChromeAutoHideTimer = new();
    private readonly VideoBottomChromeAutoHideState _bottomChromeAutoHideState = new();
    private readonly HashSet<VideoTranscriptRow> _subscribedTranscriptRows = [];
    private readonly SubclassProc _videoHostSubclassProc;
    private DictionaryPopupOverlay? _popupOverlay;
    private VideoItem? _pendingVideo;
    private IReadOnlyList<VideoItem>? _pendingPlaylist;
    private IntPtr _parentHwnd;
    private IntPtr _videoHwnd;
    private bool _isLoaded;
    private bool _isPaused;
    private bool _isFullScreen;
    private bool _isScrubbing;
    private bool _isTicking;
    private bool _isOpeningVideo;
    private bool _isInspectorOpen;
    private bool _isUpdatingVolume;
    private bool _isUpdatingPlaybackSpeed;
    private bool _isUpdatingAudioDelay;
    private bool _isUpdatingSubtitleDelay;
    private bool _isUpdatingHardwareDecoding;
    private bool _isUpdatingDeinterlace;
    private bool _isUpdatingHdrEnhancement;
    private bool _isUpdatingVideoEqualizer;
    private bool _isUpdatingAspectRatio;
    private bool _isUpdatingSubtitleAppearance;
    private bool _isUpdatingVideoTrackSelection;
    private bool _isUpdatingAudioTrackSelection;
    private bool _isUpdatingSubtitleTrackSelection;
    private bool _isSubtitlePointerOver;
    private bool _isLookupPopupVisible;
    private bool _isSubtitlePointerLookupRunning;
    private bool _isSubtitleWebViewInitialized;
    private bool _isSubtitleWebViewReady;
    private bool _isAutoPlayingNextEpisode;
    private TimeSpan? _protectedRestoreFloor;
    private DateTimeOffset _lastProgressSaveAt = DateTimeOffset.MinValue;
    private int _subtitleMaskBlurRenderGeneration;
    private VideoInspectorTab _selectedInspectorTab = VideoInspectorTab.SubtitleList;

    public VideoPlayerViewModel ViewModel { get; }

    internal event EventHandler? PlaybackStateSaved;

    public VideoPlayerWindow()
    {
        InitializeComponent();
        ViewModel = App.GetService<VideoPlayerViewModel>();
        InspectorSubtitleListContent.Initialize(ViewModel);
        InspectorSubtitleListContent.TranscriptSelected += InspectorSubtitleListContent_TranscriptSelected;
        InspectorSubtitleListContent.SetABLoopStartRequested += InspectorSubtitleListContent_SetABLoopStartRequested;
        InspectorSubtitleListContent.SetABLoopEndRequested += InspectorSubtitleListContent_SetABLoopEndRequested;
        _playbackEngine = App.GetService<IVideoPlaybackEngine>();
        _videoLibraryService = App.GetService<IVideoLibraryService>();
        _miningHistoryStore = App.GetService<IVideoMiningHistoryStore>();
        _mediaExtractor = App.GetService<IVideoMiningMediaExtractor>();
        _subtitleTranscriptExtractor = App.GetService<IVideoSubtitleTranscriptExtractor>();
        _shortcutService = App.GetService<IShortcutService>();
        _subtitleTranscriptLoadCoordinator = new VideoSubtitleTranscriptLoadCoordinator(_subtitleTranscriptExtractor);
        _videoHostSubclassProc = VideoHostSubclassProc;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.TranscriptVisibleRows.CollectionChanged += TranscriptVisibleRows_CollectionChanged;
        ProgressSlider.Minimum = 0;
        VolumeSlider.Minimum = 0;
        VolumeSlider.Maximum = 130;
        InspectorVolumeSlider.Minimum = 0;
        InspectorVolumeSlider.Maximum = 130;
        SubtitleFontSizeSlider.Minimum = 12;
        SubtitleFontSizeSlider.Maximum = 72;

        Title = "Hoshi Video";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1100, 720));

        _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
        _positionTimer.Tick += OnPositionTimerTick;
        _bottomChromeAutoHideTimer.Interval = VideoBottomChromeAutoHideState.DefaultHideDelay;
        _bottomChromeAutoHideTimer.Tick += BottomChromeAutoHideTimer_Tick;

        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);
        VideoSurface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(VideoSurface_PointerWheelChanged), true);
        BottomChrome.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
        BottomChrome.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(BottomChrome_PointerPressed), true);
        SubtitlePanelBorder.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SubtitlePanelBorder_PointerPressed), true);
        SubtitleWebView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SubtitleWebView_PointerPressed), true);
        SubtitleWebView.GotFocus += SubtitleWebView_GotFocus;
        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressSlider_PointerPressed), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressSlider_PointerReleased), true);
        ProgressSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ProgressSlider_PointerCanceled), true);
        ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressSlider_PointerCanceled), true);
        RootGrid.Loaded += OnLoaded;
        RootGrid.SizeChanged += (_, _) =>
        {
            PositionBottomChromeOverlay();
            PositionVideoHost();
        };
        Closed += OnClosed;
        VideoSurface.SizeChanged += (_, _) =>
        {
            PositionBottomChromeOverlay();
            PositionVideoHost();
        };
        SelectInspectorTab(_selectedInspectorTab);
        UpdateSubtitleAppearanceControls();
        ApplySubtitleAppearance();
        UpdateAspectRatioSelection();
        UpdateVideoEqualizerControls();
        UpdateChapterListVisibility();
        UpdateTranscriptListVisibility();
        UpdateSubtitleControlAvailability();
        UpdateVideoTrackSelection();
        UpdateAudioTrackSelection();
        UpdateSubtitleTrackSelection();
        RefreshMiningHistoryRows();
    }

    public Task OpenVideoAsync(VideoItem video, CancellationToken ct = default) =>
        OpenVideoAsync(video, [video], ct);

    public async Task OpenVideoAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
    {
        _pendingVideo = video;
        _pendingPlaylist = playlist.Count > 0 ? playlist.ToList() : [video];
        if (!_isLoaded)
            return;

        await OpenPendingVideoAsync(ct);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            OpenBottomChromeOverlay();
            await InitializeSubtitleWebViewAsync();
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
            SetWindowSubclass(_videoHwnd, _videoHostSubclassProc, VideoHostSubclassId, UIntPtr.Zero);
            PositionVideoHost();

            await _playbackEngine.InitializeAsync(_videoHwnd);
            await _playbackEngine.SetHardwareDecodingAsync(ViewModel.HardwareDecodingEnabled);
            await _playbackEngine.SetDeinterlaceAsync(ViewModel.DeinterlaceEnabled);
            await ApplyVideoEnhancementAsync();
            await _playbackEngine.SetPlaybackSpeedAsync(ViewModel.PlaybackSpeed);
            await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(ViewModel.AudioDelaySeconds));
            await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds));
            await _playbackEngine.SetFileLoopEnabledAsync(ViewModel.LoopFileEnabled);
            await _playbackEngine.SetABLoopAsync(ViewModel.ABLoop);
            await _playbackEngine.SetAspectRatioAsync(ViewModel.AspectRatioValue);
            await _playbackEngine.SetVideoRotationAsync(ViewModel.VideoRotationDegrees);
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
        var playlist = _pendingPlaylist ?? [video];
        _pendingVideo = null;
        _pendingPlaylist = null;
        await SaveCurrentVideoProgressAsync(ct);
        _isOpeningVideo = true;
        _protectedRestoreFloor = null;
        try
        {
            var restoreState = await LoadPlaybackStateAsync(video, ct);
            var restoreStartPosition = ViewModel.RememberPlaybackState
                ? restoreState.ResolveRestorePosition(TimeSpan.Zero)
                : null;
            if (restoreStartPosition != null)
                _protectedRestoreFloor = VideoProgressSaveGuard.CreateProtectedRestoreFloor(restoreStartPosition.Value);

            await ViewModel.LoadVideoAsync(video, ct);
            MaximizeVideoWindowForTesting();
            ViewModel.ReplaceEpisodes(playlist, video);
            await _videoLibraryService.MarkOpenedAsync(video.Id, ct);
            _isAutoPlayingNextEpisode = false;
            _lastProgressSaveAt = DateTimeOffset.UtcNow;
            await _playbackEngine.SetPausedAsync(true, ct);
            await _playbackEngine.OpenAsync(video.FilePath, video.SubtitlePath, restoreStartPosition, ct);
            await _playbackEngine.SetPlaybackSpeedAsync(ViewModel.PlaybackSpeed, ct);
            await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(ViewModel.AudioDelaySeconds), ct);
            await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds), ct);
            await _playbackEngine.SetFileLoopEnabledAsync(ViewModel.LoopFileEnabled, ct);
            await _playbackEngine.SetABLoopAsync(ViewModel.ABLoop, ct);
            await _playbackEngine.SetAspectRatioAsync(ViewModel.AspectRatioValue, ct);
            await _playbackEngine.SetVideoRotationAsync(ViewModel.VideoRotationDegrees, ct);
            await ApplyVideoEnhancementAsync(ct);
            await RefreshChaptersAsync(ct);
            await RefreshMediaTracksAsync(string.IsNullOrWhiteSpace(video.SubtitlePath), ct);
            var restoredSubtitle = await RestorePlaybackStateIfNeededAsync(restoreState, restoreStartPosition, ct);
            if (!restoredSubtitle && string.IsNullOrWhiteSpace(video.SubtitlePath))
                await SelectInitialEmbeddedSubtitleTrackAsync(ct);

            await _playbackEngine.SetPausedAsync(false, ct);
            _isPaused = false;
            _isLookupPopupVisible = false;
            PlayPauseIcon.Glyph = "\uE769";
            ApplySubtitleAppearance();
            UpdateSubtitleControlAvailability();
        }
        finally
        {
            _isOpeningVideo = false;
        }
    }

    private async void LookupCurrentSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        await LookupCurrentSubtitleAsync();
    }

    private async void RecordMiningHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await RecordCurrentMiningHistoryAsync();
    }

    private async Task RecordCurrentMiningHistoryAsync()
    {
        await _miningHistoryStore.UpdateLimitAsync(ViewModel.MiningHistoryLimit);
        var capture = ViewModel.CreateMiningHistoryCapture();
        if (capture == null)
        {
            ViewModel.StatusText = "No subtitle to save";
            return;
        }

        var id = await _miningHistoryStore.RecordAsync(capture);
        if (id == null)
        {
            ViewModel.StatusText = "Mining History disabled";
            RefreshMiningHistoryRows();
            return;
        }

        RefreshMiningHistoryRows();
        ViewModel.StatusText = "Saved to Mining History";
    }

    private async void MiningHistoryListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoMiningHistoryRow row)
            await JumpToMiningHistoryItemAsync(row.Item);
    }

    private async Task JumpToMiningHistoryItemAsync(VideoMiningHistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.VideoPath))
        {
            ViewModel.StatusText = "Open the matching video before using this history item";
            return;
        }

        if (!File.Exists(item.VideoPath))
        {
            ViewModel.StatusText = "The saved video file is no longer available";
            return;
        }

        var currentPath = ViewModel.CurrentVideo?.FilePath;
        if (!string.Equals(NormalizeVideoPath(currentPath ?? ""), NormalizeVideoPath(item.VideoPath), StringComparison.OrdinalIgnoreCase))
        {
            await OpenVideoAsync(new VideoItem
            {
                Id = item.Id,
                Title = Path.GetFileNameWithoutExtension(item.VideoPath),
                FilePath = item.VideoPath,
                SubtitlePath = item.SubtitleSourcePath,
                ImportedAt = item.CreatedAt,
            });
        }

        if (item.SubtitleSelectionKind == VideoSubtitleSelectionKind.ExternalFile
            && !string.IsNullOrWhiteSpace(item.SubtitleSourcePath))
        {
            if (!File.Exists(item.SubtitleSourcePath))
            {
                ViewModel.StatusText = "The saved subtitle file is no longer available";
                return;
            }

            await LoadExternalSubtitleAsync(item.SubtitleSourcePath);
        }
        else if (item.SubtitleSelectionKind == VideoSubtitleSelectionKind.EmbeddedTrack && item.EmbeddedSubtitleTrackId.HasValue)
        {
            var track = ViewModel.SubtitleTracks.FirstOrDefault(track => track.Id == item.EmbeddedSubtitleTrackId.Value);
            if (track != null)
                await SelectSubtitleTrackAsync(track);
        }

        await SeekToAsync(item.CueStart);
        ViewModel.StatusText = "Restored Mining History item";
    }

    private void CopyMiningHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoMiningHistoryRow row)
            return;

        var package = new DataPackage();
        package.SetText(row.Item.SubtitleText);
        Clipboard.SetContent(package);
        ViewModel.StatusText = "Copied subtitle";
    }

    private async void DeleteMiningHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VideoMiningHistoryRow row)
            return;

        await _miningHistoryStore.DeleteAsync(row.Id);
        RefreshMiningHistoryRows();
        ViewModel.StatusText = "Deleted Mining History item";
    }

    private async void ClearMiningHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await _miningHistoryStore.ClearAsync();
        RefreshMiningHistoryRows();
        ViewModel.StatusText = "Cleared Mining History";
    }

    private void RefreshMiningHistoryRows()
    {
        ViewModel.ReplaceMiningHistoryItems(_miningHistoryStore.Items);
        MiningHistoryListView.Visibility = ViewModel.HasMiningHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        MiningHistoryEmptyText.Visibility = ViewModel.HasMiningHistory
            ? Visibility.Collapsed
            : Visibility.Visible;
        ClearMiningHistoryButton.IsEnabled = ViewModel.HasMiningHistory;
    }

    private async void ChapterListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not VideoChapterRow row)
            return;

        try
        {
            await _playbackEngine.SeekChapterAsync(row.Id);
            ViewModel.UpdatePosition(row.StartTime, ViewModel.Duration);
            ViewModel.StatusText = $"Chapter: {row.Title}";
            ScrollCurrentChapterRowIntoView();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoPlayerViewModel.CurrentSubtitleText):
            case nameof(VideoPlayerViewModel.SubtitleFontSize):
            case nameof(VideoPlayerViewModel.SubtitleFontWeight):
            case nameof(VideoPlayerViewModel.SubtitleFontFamily):
            case nameof(VideoPlayerViewModel.SubtitleShadowRadius):
            case nameof(VideoPlayerViewModel.SubtitleBackgroundOpacity):
            case nameof(VideoPlayerViewModel.SubtitleBackgroundDisabled):
            case nameof(VideoPlayerViewModel.SubtitleVerticalPosition):
            case nameof(VideoPlayerViewModel.SubtitleColorHex):
            case nameof(VideoPlayerViewModel.SubtitleLookupHighlightColorHex):
            case nameof(VideoPlayerViewModel.SubtitleLookupHighlightTextColorHex):
            case nameof(VideoPlayerViewModel.SubtitleMaskEnabled):
            case nameof(VideoPlayerViewModel.SubtitleMaskMode):
            case nameof(VideoPlayerViewModel.SubtitleMaskBlurRadius):
            case nameof(VideoPlayerViewModel.SubtitleMaskHiddenOpacity):
                ApplySubtitleAppearance();
                break;
        }
    }

    private async Task RefreshChaptersAsync(CancellationToken ct = default)
    {
        var chapters = await _playbackEngine.GetChaptersAsync(ct);
        ViewModel.ReplaceChapters(chapters);
        UpdateChapterListVisibility();
    }

    private void UpdateChapterListVisibility()
    {
        var hasRows = ViewModel.ChapterRows.Count > 0;
        ChapterListView.Visibility = hasRows
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChapterEmptyText.Visibility = hasRows
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ScrollCurrentChapterRowIntoView()
    {
        if (ChapterListView.Visibility != Visibility.Visible)
            return;

        var currentRow = ViewModel.ChapterRows.FirstOrDefault(row => row.IsCurrent);
        if (currentRow != null)
            DispatcherQueue.TryEnqueue(() => ChapterListView.ScrollIntoView(currentRow, ScrollIntoViewAlignment.Leading));
    }

    private static string NormalizeVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private async void OpenSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _parentHwnd != IntPtr.Zero
                ? _parentHwnd
                : WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".srt");
            picker.FileTypeFilter.Add(".vtt");
            picker.FileTypeFilter.Add(".ass");
            picker.FileTypeFilter.Add(".ssa");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            await LoadExternalSubtitleAsync(file.Path);
            ViewModel.StatusText = $"Loaded subtitles: {file.Name}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task LoadExternalSubtitleAsync(string path, CancellationToken ct = default)
    {
        CancelEmbeddedTranscriptLoad();
        if (_isLoaded)
            await _playbackEngine.SelectTrackAsync(VideoTrackType.Subtitle, null, ct);

        await ViewModel.LoadSubtitleAsync(path, ct);
        await RefreshMediaTracksAsync(ct: ct);
        UpdateSubtitleControlAvailability();
        await SaveCurrentVideoProgressAsync(ct);
    }

    private void ClearSubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        _ = SelectSubtitleTrackAsync(null);
    }

    private async void SubtitleTrackOffButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectSubtitleTrackAsync(null);
    }

    private async void SubtitleTrackListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSubtitleTrackSelection)
            return;

        if (SubtitleTrackListView.SelectedItem is VideoTrackInfo track)
            await SelectSubtitleTrackAsync(track);
    }

    private async void AudioTrackOffButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectAudioTrackAsync(null);
    }

    private async void VideoTrackListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingVideoTrackSelection)
            return;

        if (VideoTrackListView.SelectedItem is VideoTrackInfo track)
            await SelectVideoTrackAsync(track);
    }

    private async void AudioTrackListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingAudioTrackSelection)
            return;

        if (AudioTrackListView.SelectedItem is VideoTrackInfo track)
            await SelectAudioTrackAsync(track);
    }

    private async Task LookupCurrentSubtitleAsync(
        string? queryOverride = null,
        int sentenceOffset = 0,
        Windows.Foundation.Point? anchorPoint = null,
        double? anchorWidth = null,
        double? anchorHeight = null)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(queryOverride)
                ? ViewModel.CurrentSubtitleText
                : queryOverride;
            if (string.IsNullOrWhiteSpace(query))
                return;

            ViewModel.StatusText = "Looking up subtitle";
            await _playbackEngine.SetPausedAsync(true);
            _isPaused = true;
            PlayPauseIcon.Glyph = "\uE768";
            ApplySubtitleAppearance();

            var request = await ViewModel.CreateLookupRequestAsync(
                query,
                sentenceOffset,
                RequestVideoMiningMediaAsync);
            if (request == null)
            {
                _popupOverlay?.Dismiss();
                VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
                _isLookupPopupVisible = false;
                ViewModel.StatusText = "No dictionary results";
                return;
            }

            await HighlightSubtitleWebSelectionAsync(request.Results[0].Matched);

            var overlay = EnsurePopupOverlay();
            _ = overlay.PrewarmAsync(RootGrid.XamlRoot, request.Theme);
            EnsureVideoDictionaryOverlaySurfaceVisible(overlay);
            var point = anchorPoint
                ?? SubtitleWebView.TransformToVisual(PopupOverlayCanvas)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
            ViewModel.StatusText = "Lookup opened";
            await overlay.ShowLookupAsync(
                request.Results,
                request.Styles,
                request.DisplaySettings,
                point.X,
                point.Y,
                anchorWidth ?? Math.Max(1, SubtitleWebView.ActualWidth),
                anchorHeight ?? Math.Max(1, SubtitleWebView.ActualHeight),
                RootGrid.XamlRoot,
                isVertical: false,
                request.Theme,
                request.AudioSettings,
                request.AnkiSettings,
                request.MiningContext);
            _isLookupPopupVisible = true;
            ApplySubtitleAppearance();
        }
        catch (Exception ex)
        {
            _popupOverlay?.Dismiss();
            VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
            _isLookupPopupVisible = false;
            ApplySubtitleAppearance();
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task<VideoMiningMediaResult> RequestVideoMiningMediaAsync(
        VideoMiningMediaRequest request,
        CancellationToken ct)
    {
        if (ViewModel.CurrentVideo == null)
            return new VideoMiningMediaResult();

        var videoPath = ViewModel.CurrentVideo.FilePath;
        var position = ViewModel.CurrentPosition;
        var cue = ViewModel.CurrentCue;
        var audioRange = cue == null
            ? null
            : ResolveVideoAudioClipRange(cue.Start, cue.End);
        if (!string.IsNullOrWhiteSpace(request.DirectMediaDirectory))
        {
            var screenshotFilename = request.CaptureScreenshot
                ? VideoMiningMediaNaming.CreateScreenshotFilename(videoPath, position)
                : null;
            var audioFilename = request.CaptureAudioClip && audioRange != null
                ? VideoMiningMediaNaming.CreateAudioClipFilename(videoPath, audioRange.Value.Start, audioRange.Value.End)
                : null;
            if (screenshotFilename != null || audioFilename != null)
            {
                _ = GenerateDirectVideoMiningMediaAsync(
                    request.DirectMediaDirectory,
                    screenshotFilename,
                    audioFilename,
                    videoPath,
                    position,
                    audioRange);
            }

            return new VideoMiningMediaResult(
                ScreenshotTag: screenshotFilename == null ? null : AnkiMediaMarkup.ForFieldPlaceholder(screenshotFilename),
                AudioClipTag: audioFilename == null ? null : AnkiMediaMarkup.ForFieldPlaceholder(audioFilename),
                AudioClipErrorMessage: request.CaptureAudioClip && audioRange == null
                    ? "Unable to capture the subtitle audio clip."
                    : null);
        }

        return await CaptureFallbackVideoMiningMediaAsync(request, videoPath, position, audioRange, ct);
    }

    private async Task<VideoMiningMediaResult> CaptureFallbackVideoMiningMediaAsync(
        VideoMiningMediaRequest request,
        string videoPath,
        TimeSpan position,
        (TimeSpan Start, TimeSpan End)? audioRange,
        CancellationToken ct)
    {
        var mediaDir = Path.Combine(AppDataHelper.GetDataPath(), "VideoMining");
        Directory.CreateDirectory(mediaDir);

        string? screenshotPath = null;
        if (request.CaptureScreenshot)
        {
            var target = Path.Combine(
                mediaDir,
                VideoMiningMediaNaming.CreateScreenshotFilename(videoPath, position));
            screenshotPath = await _playbackEngine.CaptureScreenshotAsync(target, ct);
        }

        string? audioClipPath = null;
        string? audioClipErrorMessage = null;
        if (request.CaptureAudioClip)
        {
            if (audioRange == null)
            {
                audioClipErrorMessage = "Unable to capture the subtitle audio clip.";
            }
            else
            {
                var target = Path.Combine(
                    mediaDir,
                    VideoMiningMediaNaming.CreateAudioClipFilename(videoPath, audioRange.Value.Start, audioRange.Value.End));
                audioClipPath = await _mediaExtractor.ExportAudioClipAsync(
                    videoPath,
                    target,
                    audioRange.Value.Start,
                    audioRange.Value.End);
            }
        }

        return new VideoMiningMediaResult(screenshotPath, audioClipPath, AudioClipErrorMessage: audioClipErrorMessage);
    }

    private async Task GenerateDirectVideoMiningMediaAsync(
        string mediaDirectory,
        string? screenshotFilename,
        string? audioFilename,
        string videoPath,
        TimeSpan position,
        (TimeSpan Start, TimeSpan End)? audioRange)
    {
        try
        {
            Directory.CreateDirectory(mediaDirectory);
            var tempDir = Path.Combine(AppDataHelper.GetDataPath(), "VideoMining", "Temp");
            Directory.CreateDirectory(tempDir);

            if (screenshotFilename != null)
            {
                var temp = Path.Combine(tempDir, $".{Guid.NewGuid():N}-{screenshotFilename}");
                var captured = await _playbackEngine.CaptureScreenshotAsync(temp);
                if (!string.IsNullOrWhiteSpace(captured) && File.Exists(captured))
                    ReplaceFile(captured, Path.Combine(mediaDirectory, screenshotFilename));
            }

            if (audioFilename != null && audioRange != null)
            {
                var temp = Path.Combine(tempDir, $".{Guid.NewGuid():N}-{audioFilename}");
                var exported = await _mediaExtractor.ExportAudioClipAsync(
                    videoPath,
                    temp,
                    audioRange.Value.Start,
                    audioRange.Value.End);
                if (!string.IsNullOrWhiteSpace(exported) && File.Exists(exported))
                    ReplaceFile(exported, Path.Combine(mediaDirectory, audioFilename));
            }
        }
        catch
        {
        }
    }

    private (TimeSpan Start, TimeSpan End)? ResolveVideoAudioClipRange(TimeSpan cueStart, TimeSpan cueEnd)
    {
        var delay = TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds);
        var start = cueStart + delay;
        var end = cueEnd + delay;
        if (ViewModel.Duration > TimeSpan.Zero)
            end = end > ViewModel.Duration ? ViewModel.Duration : end;

        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (end <= start)
            return null;

        return (start, end);
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        File.Delete(sourcePath);
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay != null)
            return _popupOverlay;

        _popupOverlay = new DictionaryPopupOverlay();
        _popupOverlay.Dismissed += PopupOverlay_Dismissed;
        _popupOverlay.UseCanvas(PopupOverlayCanvas);
        return _popupOverlay;
    }

    private void EnsureVideoDictionaryOverlaySurfaceVisible(DictionaryPopupOverlay? overlay = null)
    {
        VideoDictionaryPanelChrome.Visibility = Visibility.Visible;
        VideoDictionaryPanelChrome.UpdateLayout();
        (overlay ?? _popupOverlay)?.UpdateRootSize(PopupOverlayCanvas.ActualWidth, PopupOverlayCanvas.ActualHeight);
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _positionTimer.Stop();
        _bottomChromeAutoHideTimer.Stop();
        _bottomChromeAutoHideTimer.Tick -= BottomChromeAutoHideTimer_Tick;

        try
        {
            await SaveCurrentVideoProgressAsync();
            PlaybackStateSaved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            CancelEmbeddedTranscriptLoad();
            InspectorSubtitleListContent.TranscriptSelected -= InspectorSubtitleListContent_TranscriptSelected;
            InspectorSubtitleListContent.SetABLoopStartRequested -= InspectorSubtitleListContent_SetABLoopStartRequested;
            InspectorSubtitleListContent.SetABLoopEndRequested -= InspectorSubtitleListContent_SetABLoopEndRequested;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.TranscriptVisibleRows.CollectionChanged -= TranscriptVisibleRows_CollectionChanged;
            foreach (var row in _subscribedTranscriptRows)
                row.PropertyChanged -= TranscriptRow_PropertyChanged;
            _subscribedTranscriptRows.Clear();
            BottomChromePopup.IsOpen = false;
            SubtitleWebView.GotFocus -= SubtitleWebView_GotFocus;
            if (SubtitleWebView.CoreWebView2 != null)
                SubtitleWebView.CoreWebView2.WebMessageReceived -= OnSubtitleWebMessageReceived;
            if (_popupOverlay != null)
                _popupOverlay.Dismissed -= PopupOverlay_Dismissed;
            _popupOverlay?.Dispose();
            _popupOverlay = null;
            _subtitleTranscriptLoadCoordinator.Dispose();
            _playbackEngine.Dispose();

            if (_videoHwnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_videoHwnd, _videoHostSubclassProc, VideoHostSubclassId);
                DestroyWindow(_videoHwnd);
                _videoHwnd = IntPtr.Zero;
            }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr idSubclass,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr idSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);
}
