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
using Niratan.Helpers;
using Niratan.Enums;
using Niratan.Models;
using Niratan.Models.Anki;
using Niratan.Models.DTO;
using Niratan.Models.Settings;
using Niratan.Models.Shortcuts;
using Niratan.Services.Anki;
using Niratan.Services.Settings;
using Niratan.Services.Shortcuts;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;
using Niratan.Views.Dictionary;
using Serilog;

namespace Niratan.Views.Video;

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
    private static readonly UIntPtr VideoWindowSubclassId = new(2);

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
    private readonly ISettingsService _settingsService;
    private readonly VideoSubtitleTranscriptLoadCoordinator _subtitleTranscriptLoadCoordinator;
    private readonly DispatcherTimer _positionTimer = new();
    private readonly DispatcherTimer _bottomChromeAutoHideTimer = new();
    private readonly VideoBottomChromeAutoHideState _bottomChromeAutoHideState = new();
    private readonly HashSet<VideoTranscriptRow> _subscribedTranscriptRows = [];
    private readonly SubclassProc _videoHostSubclassProc;
    private readonly SubclassProc _videoWindowSubclassProc;
    private DictionaryPopupOverlay? _popupOverlay;
    private VideoItem? _pendingVideo;
    private IReadOnlyList<VideoItem>? _pendingPlaylist;
    private ResolvedRemoteVideoSource? _pendingRemoteSource;
    private readonly RemoteVideoPlaybackSession _remotePlaybackSession;
    private CancellationTokenSource? _remoteOperationCts;
    private CancellationTokenSource? _remoteSubtitleOperationCts;
    private CancellationTokenSource? _subtitleHoverLookupCts;
    private long _remoteSubtitleSelectionVersion;
    private int _remoteRecoveryStage;
    private bool _isRecoveringRemotePlayback;
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
    private DateTimeOffset _lastVideoMetricsRefreshAt = DateTimeOffset.MinValue;
    private double? _videoAspectRatio;
    private DateTimeOffset _lastBottomChromeTimerRestartAt = DateTimeOffset.MinValue;
    private bool _isUpdatingSubtitleAppearance;
    private bool _isUpdatingVideoTrackSelection;
    private bool _isUpdatingAudioTrackSelection;
    private bool _isUpdatingSubtitleTrackSelection;
    private bool _isUpdatingRemoteSubtitleSelection;
    private bool _isSubtitlePointerOver;
    private bool _isLookupPopupVisible;
    private bool _isSubtitleWebViewInitialized;
    private bool _isSubtitleWebViewReady;
    private VideoViewportGeometry? _subtitleVideoViewport;
    private Windows.Foundation.Rect? _subtitleVisibleBounds;
    private int _subtitleSelectionStart = -1;
    private int _subtitleSelectionLength;
    private int _lastSubtitleHoverCharacterIndex = -1;
    private Windows.Foundation.Point? _lastSubtitlePointerPoint;
    private bool _isAutoPlayingNextEpisode;
    private double _volumeBeforeMute = 100;
    private bool _subtitleGapFastForwardEnabled;
    private bool _isSubtitleGapFastForwardActive;
    private TimeSpan? _protectedRestoreFloor;
    private DateTimeOffset _lastProgressSaveAt = DateTimeOffset.MinValue;
    private VideoInspectorTab _selectedInspectorTab = VideoInspectorTab.SubtitleList;
    private readonly VideoSubtitleLookupRequestCoordinator _subtitleLookupCoordinator = new();

    public VideoPlayerViewModel ViewModel { get; }

    internal event EventHandler? PlaybackStateSaved;

    public VideoPlayerWindow()
    {
        InitializeComponent();
        _settingsService = App.GetService<ISettingsService>();
        ApplyInspectorTheme(_settingsService.Current.Theme);
        _settingsService.SettingChanged += SettingsService_SettingChanged;
        ViewModel = App.GetService<VideoPlayerViewModel>();
        InspectorSubtitleListContent.Initialize(ViewModel);
        InspectorSubtitleListContent.TranscriptSelected += InspectorSubtitleListContent_TranscriptSelected;
        InspectorSubtitleListContent.SetABLoopStartRequested += InspectorSubtitleListContent_SetABLoopStartRequested;
        InspectorSubtitleListContent.SetABLoopEndRequested += InspectorSubtitleListContent_SetABLoopEndRequested;
        _playbackEngine = App.GetService<IVideoPlaybackEngine>();
        _remotePlaybackSession = new RemoteVideoPlaybackSession(App.GetService<IRemoteVideoResolver>());
        _playbackEngine.MediaLoaded += PlaybackEngine_MediaLoaded;
        _playbackEngine.MediaFailed += PlaybackEngine_MediaFailed;
        _videoLibraryService = App.GetService<IVideoLibraryService>();
        _miningHistoryStore = App.GetService<IVideoMiningHistoryStore>();
        _mediaExtractor = App.GetService<IVideoMiningMediaExtractor>();
        _subtitleTranscriptExtractor = App.GetService<IVideoSubtitleTranscriptExtractor>();
        _shortcutService = App.GetService<IShortcutService>();
        _subtitleTranscriptLoadCoordinator = new VideoSubtitleTranscriptLoadCoordinator(_subtitleTranscriptExtractor);
        _videoHostSubclassProc = VideoHostSubclassProc;
        _videoWindowSubclassProc = VideoWindowSubclassProc;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.TranscriptVisibleRows.CollectionChanged += TranscriptVisibleRows_CollectionChanged;
        ProgressSlider.Minimum = 0;
        VolumeSlider.Minimum = 0;
        VolumeSlider.Maximum = 130;
        InspectorVolumeSlider.Minimum = 0;
        InspectorVolumeSlider.Maximum = 130;
        SubtitleFontSizeSlider.Minimum = 12;
        SubtitleFontSizeSlider.Maximum = 72;

        Title = "Niratan Video";
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

    private void SettingsService_SettingChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
            ApplyInspectorTheme(e.NewValue is ThemeMode theme ? theme : ThemeMode.System);
    }

    private void ApplyInspectorTheme(ThemeMode theme) =>
        InspectorPanel.RequestedTheme = theme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    public Task OpenVideoAsync(VideoItem video, CancellationToken ct = default) =>
        OpenVideoAsync(video, [video], ct);

    public async Task OpenVideoAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
        => await OpenVideoAsync(new VideoPlaybackLaunchRequest(video, playlist), ct);

    public async Task OpenVideoAsync(VideoPlaybackLaunchRequest request, CancellationToken ct = default)
    {
        _remoteOperationCts?.Cancel();
        _remoteOperationCts?.Dispose();
        _remoteOperationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _remotePlaybackSession.Invalidate();
        _remoteRecoveryStage = 0;
        _pendingVideo = request.Video;
        _pendingPlaylist = request.Playlist.Count > 0 ? request.Playlist.ToList() : [request.Video];
        _pendingRemoteSource = request.ResolvedRemoteSource;
        if (!_isLoaded)
            return;

        var operationToken = _remoteOperationCts.Token;
        try
        {
            await OpenPendingVideoAsync(operationToken);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // Opening is generation-scoped. Replacing the source or closing the
            // window cancels the old generation and must not reach WinUI as a crash.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowSubclass(_parentHwnd, _videoWindowSubclassProc, VideoWindowSubclassId, UIntPtr.Zero);
            OpenBottomChromeOverlay();
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
            await _playbackEngine.SetVolumeAsync(ViewModel.Volume);
            _isLoaded = true;
            _positionTimer.Start();
            RootGrid.Focus(FocusState.Programmatic);
            await OpenPendingVideoAsync(_remoteOperationCts?.Token ?? default);
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
        var preResolvedRemoteSource = _pendingRemoteSource;
        _pendingVideo = null;
        _pendingPlaylist = null;
        _pendingRemoteSource = null;
        _videoAspectRatio = null;
        await SaveCurrentVideoProgressAsync(ct);
        _isOpeningVideo = true;
        _protectedRestoreFloor = null;
        try
        {
            var restoreStateTask = LoadPlaybackStateAsync(video, ct);
            var loadVideoTask = ViewModel.LoadVideoAsync(video, ct);
            var restoreState = await restoreStateTask;
            var restoreStartPosition = ViewModel.RememberPlaybackState
                ? restoreState.ResolveRestorePosition(TimeSpan.Zero)
                : null;
            if (restoreStartPosition is null or { Ticks: 0 })
                restoreStartPosition = preResolvedRemoteSource?.RequestedStartPosition;
            if (restoreStartPosition != null)
                _protectedRestoreFloor = VideoProgressSaveGuard.CreateProtectedRestoreFloor(restoreStartPosition.Value);

            VideoPlaybackRequest playbackRequest;
            var hasInteractiveSubtitle = !string.IsNullOrWhiteSpace(video.SubtitlePath);
            RemoteVideoSubtitleOption? remoteSubtitleToLoad = null;
            if (video.IsRemote)
            {
                var remoteSource = await _remotePlaybackSession.InitializeAsync(video, preResolvedRemoteSource, ct);
                ViewModel.ConfigureRemoteSource(remoteSource);
                SyncYouTubeSubtitleComboBoxSelection();
                playbackRequest = _remotePlaybackSession.CreatePlaybackRequest(restoreStartPosition);
                var savedRemoteLanguage = restoreState.SubtitleSelection.Kind == VideoSubtitleSelectionKind.RemoteLanguage
                    ? restoreState.SubtitleSelection.RemoteLanguageCode
                    : video.RemoteSubtitleLanguage;
                var subtitle = remoteSource.PreferredSubtitle([savedRemoteLanguage]);
                if (subtitle != null && restoreState.SubtitleSelection.Kind != VideoSubtitleSelectionKind.Off)
                {
                    remoteSubtitleToLoad = subtitle;
                }
            }
            else
            {
                playbackRequest = VideoPlaybackRequest.Local(video.FilePath, video.SubtitlePath, restoreStartPosition);
            }

            ViewModel.ReplaceEpisodes(playlist, video);
            _isAutoPlayingNextEpisode = false;
            _lastProgressSaveAt = DateTimeOffset.UtcNow;
            await _playbackEngine.SetPausedAsync(true, ct);
            if (video.IsRemote)
                await _playbackEngine.OpenAsync(playbackRequest, ct);
            else
                await _playbackEngine.OpenAsync(video.FilePath, video.SubtitlePath, restoreStartPosition, ct);

            // loadfile is asynchronous. Unpause immediately so decoding can begin
            // while the remaining per-video properties and sidebar data are applied.
            await _playbackEngine.SetPausedAsync(false, ct);
            _isPaused = false;
            _isLookupPopupVisible = false;
            PlayPauseIcon.Glyph = "\uE769";

            await _playbackEngine.SetPlaybackSpeedAsync(ViewModel.PlaybackSpeed, ct);
            await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(ViewModel.AudioDelaySeconds), ct);
            await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds), ct);
            await _playbackEngine.SetFileLoopEnabledAsync(ViewModel.LoopFileEnabled, ct);
            await _playbackEngine.SetABLoopAsync(ViewModel.ABLoop, ct);
            await _playbackEngine.SetAspectRatioAsync(ViewModel.AspectRatioValue, ct);
            await _playbackEngine.SetVideoRotationAsync(ViewModel.VideoRotationDegrees, ct);
            await ApplyVideoEnhancementAsync(ct);

            await loadVideoTask;
            if (remoteSubtitleToLoad != null)
            {
                hasInteractiveSubtitle = await LoadRemoteSubtitleOptionAsync(
                    remoteSubtitleToLoad,
                    persistSelection: false,
                    ct);
            }

            await _videoLibraryService.MarkOpenedAsync(video.Id, ct);
            await RefreshChaptersAsync(ct);
            await RefreshMediaTracksAsync(!hasInteractiveSubtitle, ct);
            var restoredSubtitle = await RestorePlaybackStateIfNeededAsync(restoreState, restoreStartPosition, ct);
            if (!restoredSubtitle && string.IsNullOrWhiteSpace(video.SubtitlePath))
                await SelectInitialEmbeddedSubtitleTrackAsync(ct);

            ApplySubtitleAppearance();
            UpdateSubtitleControlAvailability();
        }
        catch (RemoteVideoResolverException ex)
        {
            ViewModel.StatusText = MapRemoteVideoError(ex.Error);
        }
        finally
        {
            _isOpeningVideo = false;
        }
    }

    private void PlaybackEngine_MediaFailed(object? sender, VideoMediaFailedEventArgs e)
    {
        if (ViewModel.CurrentVideo?.IsRemote != true)
            return;

        DispatcherQueue.TryEnqueue(async () => await RecoverRemotePlaybackAsync());
    }

    private void PlaybackEngine_MediaLoaded(object? sender, VideoMediaLoadedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await SynchronizeVideoWindowAspectRatioAsync();

                // WebView2 is not needed to produce the first frame. Initialize the
                // subtitle document after mpv reports the file loaded so it cannot
                // extend the video host's startup critical path.
                await InitializeSubtitleWebViewAsync();

                if (ViewModel.CurrentVideo?.IsRemote == true
                    && _remotePlaybackSession.Source?.AudioStream != null
                    && _remoteOperationCts != null)
                {
                    await Task.Delay(200, _remoteOperationCts.Token);
                    var tracks = await _playbackEngine.GetTracksAsync(_remoteOperationCts.Token);
                    if (!tracks.Any(track => track.Type == VideoTrackType.Audio))
                        await RecoverRemotePlaybackAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Video] Deferred media-loaded initialization failed");
            }
        });
    }

    private async Task RecoverRemotePlaybackAsync()
    {
        if (_isOpeningVideo
            || _isRecoveringRemotePlayback
            || ViewModel.CurrentVideo?.IsRemote != true
            || _remoteOperationCts == null)
            return;

        _isRecoveringRemotePlayback = true;
        var ct = _remoteOperationCts.Token;
        try
        {
            var position = await _playbackEngine.GetPositionAsync(ct);
            var paused = _isPaused;
            if (_remoteRecoveryStage == 0)
            {
                _remoteRecoveryStage = 1;
                var refreshed = await _remotePlaybackSession.RefreshAsync(
                    _remotePlaybackSession.Source?.SelectedHeight,
                    ct);
                ViewModel.ConfigureRemoteSource(refreshed);
                SyncYouTubeSubtitleComboBoxSelection();
            }
            else if (_remoteRecoveryStage == 1 && _remotePlaybackSession.SelectMuxedFallback())
            {
                _remoteRecoveryStage = 2;
            }
            else
            {
                ViewModel.StatusText = ResourceStringHelper.GetString(
                    "YouTubePlaybackFailed",
                    "This YouTube video could not be played. Try again later.");
                return;
            }

            await _playbackEngine.OpenAsync(_remotePlaybackSession.CreatePlaybackRequest(position), ct);
            await _playbackEngine.SetPlaybackSpeedAsync(ViewModel.PlaybackSpeed, ct);
            await _playbackEngine.SetVolumeAsync(ViewModel.Volume, ct);
            await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(ViewModel.AudioDelaySeconds), ct);
            await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds), ct);
            await _playbackEngine.SetFileLoopEnabledAsync(ViewModel.LoopFileEnabled, ct);
            await _playbackEngine.SetABLoopAsync(ViewModel.ABLoop, ct);
            await _playbackEngine.SetPausedAsync(paused, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (RemoteVideoResolverException ex)
        {
            ViewModel.StatusText = MapRemoteVideoError(ex.Error);
        }
        catch
        {
            ViewModel.StatusText = ResourceStringHelper.GetString(
                "YouTubePlaybackFailed",
                "This YouTube video could not be played. Try again later.");
        }
        finally
        {
            _isRecoveringRemotePlayback = false;
        }
    }

    private async void YouTubeQualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isOpeningVideo
            || (sender as ComboBox)?.SelectedItem is not RemoteVideoQualityOption option
            || option.Id == ViewModel.SelectedRemoteQualityId
            || !_remotePlaybackSession.SelectQuality(option.Id)
            || _remoteOperationCts == null)
        {
            return;
        }

        var ct = _remoteOperationCts.Token;
        try
        {
            var position = await _playbackEngine.GetPositionAsync(ct);
            var paused = _isPaused;
            await _playbackEngine.SetPausedAsync(true, ct);
            await _playbackEngine.OpenAsync(_remotePlaybackSession.CreatePlaybackRequest(position), ct);
            await _playbackEngine.SetPlaybackSpeedAsync(ViewModel.PlaybackSpeed, ct);
            await _playbackEngine.SetVolumeAsync(ViewModel.Volume, ct);
            await _playbackEngine.SetAudioDelayAsync(TimeSpan.FromSeconds(ViewModel.AudioDelaySeconds), ct);
            await _playbackEngine.SetSubtitleDelayAsync(TimeSpan.FromMilliseconds(ViewModel.SubtitleDelayMilliseconds), ct);
            await _playbackEngine.SetFileLoopEnabledAsync(ViewModel.LoopFileEnabled, ct);
            await _playbackEngine.SetABLoopAsync(ViewModel.ABLoop, ct);
            await _playbackEngine.SetAspectRatioAsync(ViewModel.AspectRatioValue, ct);
            await _playbackEngine.SetVideoRotationAsync(ViewModel.VideoRotationDegrees, ct);
            await ApplyVideoEnhancementAsync(ct);
            await _playbackEngine.SetPausedAsync(paused, ct);
            ViewModel.SelectedRemoteQualityId = option.Id;
            ViewModel.StatusText = $"YouTube quality: {option.DisplayName}";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ViewModel.StatusText = ResourceStringHelper.GetString(
                "YouTubeQualitySwitchFailed",
                "Could not switch YouTube quality.");
        }
    }

    private async void YouTubeSubtitleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isOpeningVideo
            || _isUpdatingRemoteSubtitleSelection
            || (sender as ComboBox)?.SelectedItem is not RemoteVideoSubtitleOption option
            || option.Id == ViewModel.SelectedRemoteSubtitleId
            || _remoteOperationCts == null)
        {
            return;
        }

        await LoadRemoteSubtitleOptionAsync(option, persistSelection: true, _remoteOperationCts.Token);
    }

    private void SyncYouTubeSubtitleComboBoxSelection()
    {
        _isUpdatingRemoteSubtitleSelection = true;
        try
        {
            YouTubeSubtitleComboBox.SelectedItem = ViewModel.RemoteSubtitleOptions.FirstOrDefault(option =>
                option.Id == ViewModel.SelectedRemoteSubtitleId);
        }
        finally
        {
            _isUpdatingRemoteSubtitleSelection = false;
        }
    }

    private async Task<bool> LoadRemoteSubtitleOptionAsync(
        RemoteVideoSubtitleOption option,
        bool persistSelection,
        CancellationToken parentToken)
    {
        _remoteSubtitleOperationCts?.Cancel();
        _remoteSubtitleOperationCts?.Dispose();
        _remoteSubtitleOperationCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var ct = _remoteSubtitleOperationCts.Token;
        var version = Interlocked.Increment(ref _remoteSubtitleSelectionVersion);
        ViewModel.StatusText = ResourceStringHelper.GetString(
            "YouTubeSubtitleLoading",
            "Loading publisher subtitles...");
        Log.Information(
            "[YouTubeSubtitle] Loading publisher track {Language} ({TrackId})",
            option.Language,
            option.Id);
        try
        {
            var directory = Path.Combine(
                AppDataHelper.GetTemporaryDataPath(),
                "remote-video-subtitles");
            var path = await _remotePlaybackSession.DownloadSubtitleAsync(option, directory, ct);
            ct.ThrowIfCancellationRequested();
            if (version != Interlocked.Read(ref _remoteSubtitleSelectionVersion))
                return false;

            await ViewModel.LoadRemoteSubtitleAsync(path, option, ct);
            ct.ThrowIfCancellationRequested();
            if (version != Interlocked.Read(ref _remoteSubtitleSelectionVersion))
                return false;

            ApplySubtitleAppearance();
            SyncYouTubeSubtitleComboBoxSelection();
            if (persistSelection)
                await SaveCurrentVideoProgressAsync(ct);
            Log.Information(
                "[YouTubeSubtitle] Loaded publisher track {Language} ({TrackId})",
                option.Language,
                option.Id);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (version == Interlocked.Read(ref _remoteSubtitleSelectionVersion))
            {
                ViewModel.StatusText = ResourceStringHelper.GetString(
                    "YouTubeSubtitleLoadFailed",
                    "Could not load this publisher subtitle track.");
                SyncYouTubeSubtitleComboBoxSelection();
                Log.Warning(
                    ex,
                    "[YouTubeSubtitle] Failed publisher track {Language} ({TrackId})",
                    option.Language,
                    option.Id);
            }

            return false;
        }
    }

    private static string MapRemoteVideoError(RemoteVideoResolverError error) => error switch
    {
        RemoteVideoResolverError.UnsupportedUrl => ResourceStringHelper.GetString("YouTubeInvalidUrl", "Enter a valid YouTube video link."),
        RemoteVideoResolverError.SignInRequired or RemoteVideoResolverError.RegionRestricted or RemoteVideoResolverError.ContentUnavailable => ResourceStringHelper.GetString("YouTubeRestricted", "This video requires access that Niratan does not support."),
        RemoteVideoResolverError.NoPlayableStream => ResourceStringHelper.GetString("YouTubeNoPlayableStream", "No compatible stream up to 1080p is available."),
        _ => ResourceStringHelper.GetString("YouTubeResolveFailed", "The YouTube video could not be resolved. Try again later."),
    };

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

        var isRemoteHistoryItem = YouTubeUrlParser.IsRemoteKey(item.VideoPath);
        if (!isRemoteHistoryItem && !File.Exists(item.VideoPath))
        {
            ViewModel.StatusText = "The saved video file is no longer available";
            return;
        }

        var currentPath = ViewModel.CurrentVideo?.FilePath;
        if (!string.Equals(NormalizeVideoPath(currentPath ?? ""), NormalizeVideoPath(item.VideoPath), StringComparison.OrdinalIgnoreCase))
        {
            if (isRemoteHistoryItem)
            {
                var stored = await _videoLibraryService.GetVideoAsync(item.VideoPath);
                if (!stored.IsSuccess || stored.Value == null)
                {
                    ViewModel.StatusText = "Add the YouTube video to the library before reopening this history item";
                    return;
                }

                await OpenVideoAsync(stored.Value);
            }
            else
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
                _subtitleVisibleBounds = null;
                ClearSubtitleCanvasSelection();
                ApplySubtitleAppearance();
                break;
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
            case nameof(VideoPlayerViewModel.IsVideoShaderDownloadRequired):
            case nameof(VideoPlayerViewModel.IsVideoShaderDownloadInProgress):
                UpdateAnime4KDownloadControls();
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

    private Task StartSubtitleLookupAsync(
        string? queryOverride = null,
        int sentenceOffset = 0,
        Windows.Foundation.Point? anchorPoint = null,
        double? anchorWidth = null,
        double? anchorHeight = null)
    {
        var lookupRequest = _subtitleLookupCoordinator.BeginRequest();
        Log.Information(
            "[VideoLookup] request version={RequestVersion}",
            lookupRequest.Version);
        return LookupCurrentSubtitleAsync(
            lookupRequest,
            queryOverride,
            sentenceOffset,
            anchorPoint,
            anchorWidth,
            anchorHeight);
    }

    private bool IsCurrentSubtitleLookup(VideoSubtitleLookupRequest lookupRequest) =>
        _subtitleLookupCoordinator.IsCurrent(lookupRequest);

    private async Task LookupCurrentSubtitleAsync(
        VideoSubtitleLookupRequest lookupRequest,
        string? queryOverride = null,
        int sentenceOffset = 0,
        Windows.Foundation.Point? anchorPoint = null,
        double? anchorWidth = null,
        double? anchorHeight = null)
    {
        DictionaryPopupOverlay? lookupOverlay = null;
        string? lookupCommitId = null;
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
            if (!IsCurrentSubtitleLookup(lookupRequest))
                return;

            var popupRequest = await ViewModel.CreateLookupRequestAsync(
                query,
                sentenceOffset,
                RequestVideoMiningMediaAsync,
                lookupRequest.CancellationToken);
            if (!IsCurrentSubtitleLookup(lookupRequest))
                return;
            if (popupRequest == null)
            {
                if (!_isLookupPopupVisible
                    && !_subtitleLookupCoordinator.HasPopupCommitCandidates)
                {
                    ClearSubtitleCanvasSelection();
                    VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
                }

                ViewModel.StatusText = "No dictionary results";
                return;
            }

            lookupOverlay = EnsurePopupOverlay();
            lookupCommitId = _subtitleLookupCoordinator.CreatePopupCommitIdentity(
                lookupRequest,
                popupRequest.TraceId);
            EnsureVideoDictionaryOverlaySurfaceVisible(lookupOverlay);
            var point = anchorPoint
                ?? SubtitleCanvas.TransformToVisual(PopupOverlayCanvas)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
            _subtitleLookupCoordinator.StagePopupCommit(
                lookupRequest,
                lookupCommitId,
                sentenceOffset,
                popupRequest.Results[0].Matched);
            await lookupOverlay.ShowLookupAsync(
                popupRequest.Results,
                popupRequest.Styles,
                popupRequest.DisplaySettings,
                point.X,
                point.Y,
                anchorWidth ?? Math.Max(1, SubtitleCanvas.ActualWidth),
                anchorHeight ?? Math.Max(1, SubtitleCanvas.ActualHeight),
                RootGrid.XamlRoot,
                isVertical: false,
                popupRequest.Theme,
                popupRequest.AudioSettings,
                popupRequest.AnkiSettings,
                popupRequest.MiningContext,
                traceId: lookupCommitId,
                cancellationToken: lookupRequest.CancellationToken);
            if (!IsCurrentSubtitleLookup(lookupRequest))
            {
                ResolvePopupShowCancellation(lookupOverlay, lookupCommitId);
                return;
            }
        }
        catch (OperationCanceledException) when (lookupRequest.CancellationToken.IsCancellationRequested)
        {
            ResolvePopupShowCancellation(lookupOverlay, lookupCommitId);
        }
        catch (Exception ex)
        {
            if (!IsCurrentSubtitleLookup(lookupRequest))
            {
                ResolvePopupShowCancellation(lookupOverlay, lookupCommitId);
                Log.Debug(
                    ex,
                    "[VideoLookup] stale request version={RequestVersion} failed after supersession",
                    lookupRequest.Version);
                return;
            }

            _subtitleLookupCoordinator.CancelCurrentRequest();
            ResolvePopupShowCancellation(lookupOverlay, lookupCommitId);
            if (!_isLookupPopupVisible
                && !_subtitleLookupCoordinator.HasPopupCommitCandidates)
            {
                _popupOverlay?.Dismiss();
                ClearSubtitleCanvasSelection();
                VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
                ApplySubtitleAppearance();
            }

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
        var requestedCueStart = request.CueStart ?? cue?.Start;
        var requestedCueEnd = request.CueEnd ?? cue?.End;
        var audioRange = requestedCueStart.HasValue && requestedCueEnd.HasValue
            ? ResolveVideoAudioClipRange(requestedCueStart.Value, requestedCueEnd.Value)
            : null;
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
                return await GenerateDirectVideoMiningMediaAsync(
                    request.DirectMediaDirectory,
                    screenshotFilename,
                    audioFilename,
                    videoPath,
                    position,
                    audioRange,
                    request.CaptureScreenshot,
                    request.CaptureAudioClip,
                    ct);
            }

            return new VideoMiningMediaResult(
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
                    ResolveMiningMediaSource(videoPath),
                    target,
                    audioRange.Value.Start,
                    audioRange.Value.End,
                    ct);
            }
        }

        return new VideoMiningMediaResult(
            screenshotPath,
            audioClipPath,
            AudioClipErrorMessage: audioClipErrorMessage,
            ScreenshotErrorMessage: request.CaptureScreenshot && !HasOutput(screenshotPath)
                ? "Unable to capture the video screenshot."
                : null);
    }

    private async Task<VideoMiningMediaResult> GenerateDirectVideoMiningMediaAsync(
        string mediaDirectory,
        string? screenshotFilename,
        string? audioFilename,
        string videoPath,
        TimeSpan position,
        (TimeSpan Start, TimeSpan End)? audioRange,
        bool captureScreenshot,
        bool captureAudioClip,
        CancellationToken ct)
    {
        string? screenshotTag = null;
        string? audioTag = null;
        string? screenshotError = null;
        string? audioError = null;
        try
        {
            Directory.CreateDirectory(mediaDirectory);
            var tempDir = Path.Combine(AppDataHelper.GetDataPath(), "VideoMining", "Temp");
            Directory.CreateDirectory(tempDir);

            if (screenshotFilename != null)
            {
                var temp = Path.Combine(tempDir, $".{Guid.NewGuid():N}-{screenshotFilename}");
                var captured = await _playbackEngine.CaptureScreenshotAsync(temp, ct);
                var destination = Path.Combine(mediaDirectory, screenshotFilename);
                if (HasOutput(captured))
                {
                    ReplaceFile(captured!, destination);
                    if (HasOutput(destination))
                        screenshotTag = AnkiMediaMarkup.ForFieldPlaceholder(screenshotFilename);
                }

                if (screenshotTag == null)
                    screenshotError = "Unable to capture the video screenshot.";
            }
            else if (captureScreenshot)
            {
                screenshotError = "Unable to capture the video screenshot.";
            }

            if (audioFilename != null && audioRange != null)
            {
                var temp = Path.Combine(tempDir, $".{Guid.NewGuid():N}-{audioFilename}");
                var exported = await _mediaExtractor.ExportAudioClipAsync(
                    ResolveMiningMediaSource(videoPath),
                    temp,
                    audioRange.Value.Start,
                    audioRange.Value.End,
                    ct);
                var destination = Path.Combine(mediaDirectory, audioFilename);
                if (HasOutput(exported))
                {
                    ReplaceFile(exported!, destination);
                    if (HasOutput(destination))
                        audioTag = AnkiMediaMarkup.ForFieldPlaceholder(audioFilename);
                }

                if (audioTag == null)
                    audioError = "Unable to capture the subtitle audio clip.";
            }
            else if (captureAudioClip)
            {
                audioError = "Unable to capture the subtitle audio clip.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoMining] Failed to generate direct mining media");
            if (captureScreenshot && screenshotTag == null)
                screenshotError = "Unable to capture the video screenshot.";
            if (captureAudioClip && audioTag == null)
                audioError = "Unable to capture the subtitle audio clip.";
        }

        return new VideoMiningMediaResult(
            ScreenshotTag: screenshotTag,
            AudioClipTag: audioTag,
            AudioClipErrorMessage: audioError,
            ScreenshotErrorMessage: screenshotError);
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

    private VideoMiningMediaSource ResolveMiningMediaSource(string localFallback)
    {
        var remote = _remotePlaybackSession.Source;
        if (ViewModel.CurrentVideo?.IsRemote == true && remote != null)
            return VideoMiningMediaSource.Remote(remote.AudioStream ?? remote.MiningStream);

        return VideoMiningMediaSource.Local(localFallback);
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        File.Delete(sourcePath);
    }

    private static bool HasOutput(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && new FileInfo(path).Length > 0;

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay != null)
            return _popupOverlay;

        _popupOverlay = new DictionaryPopupOverlay();
        _popupOverlay.Dismissed += PopupOverlay_Dismissed;
        _popupOverlay.RootContentCommitted += PopupOverlay_RootContentCommitted;
        _popupOverlay.RootContentAborted += PopupOverlay_RootContentAborted;
        _popupOverlay.RootShowDropped += PopupOverlay_RootShowDropped;
        _popupOverlay.UseCanvas(
            PopupOverlayCanvas,
            DictionaryPopupCanvasInputMode.VisibleHostsOnly);
        _popupOverlay.UseInPlaceDialogHost(VideoModalOverlayHost);
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
            _settingsService.SettingChanged -= SettingsService_SettingChanged;
            _remoteOperationCts?.Cancel();
            _remoteOperationCts?.Dispose();
            _remoteOperationCts = null;
            _remoteSubtitleOperationCts?.Cancel();
            _remoteSubtitleOperationCts?.Dispose();
            _remoteSubtitleOperationCts = null;
            _subtitleHoverLookupCts?.Cancel();
            _subtitleHoverLookupCts?.Dispose();
            _subtitleHoverLookupCts = null;
            _remotePlaybackSession.Invalidate();
            _playbackEngine.MediaLoaded -= PlaybackEngine_MediaLoaded;
            _playbackEngine.MediaFailed -= PlaybackEngine_MediaFailed;
            if (_parentHwnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_parentHwnd, _videoWindowSubclassProc, VideoWindowSubclassId);
                _parentHwnd = IntPtr.Zero;
            }
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
            if (SubtitleWebView.CoreWebView2 != null)
                SubtitleWebView.CoreWebView2.WebMessageReceived -= OnSubtitleWebMessageReceived;
            if (_popupOverlay != null)
            {
                _popupOverlay.Dismissed -= PopupOverlay_Dismissed;
                _popupOverlay.RootContentCommitted -= PopupOverlay_RootContentCommitted;
                _popupOverlay.RootContentAborted -= PopupOverlay_RootContentAborted;
                _popupOverlay.RootShowDropped -= PopupOverlay_RootShowDropped;
            }
            _subtitleLookupCoordinator.Dispose();
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
