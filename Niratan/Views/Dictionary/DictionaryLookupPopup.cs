using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Anki;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Anki;
using Niratan.Services.Audio;
using Niratan.Services.Dictionary;
using Niratan.Views.Dialogs;
using Serilog;

namespace Niratan.Views.Dictionary;

public sealed record DictionaryPopupRedirectRequest(
    string Query,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    string? Source = null,
    double? ClientNow = null,
    double? SelectMs = null,
    double? RectMs = null,
    int? SelectedLength = null);

public sealed class DictionaryPopupContentCommittedEventArgs(
    long generation,
    string? traceId) : EventArgs
{
    public long Generation { get; } = generation;
    public string? TraceId { get; } = traceId;
}

public sealed class DictionaryPopupShowDroppedEventArgs(
    string? traceId) : EventArgs
{
    public string? TraceId { get; } = traceId;
}

public sealed class DictionaryLookupPopup : IDisposable
{
    private sealed record DictionaryPopupShowRequest(
        List<DictionaryLookupResult> Results,
        Dictionary<string, string> Styles,
        DictionaryDisplaySettings DisplaySettings,
        ThemeMode ThemeMode,
        AudioSettings AudioSettings,
        AnkiSettings AnkiSettings,
        AnkiMiningContext MiningContext,
        SasayakiPopupControls? SasayakiControls,
        string? TraceId,
        CancellationToken CancellationToken,
        Action<long>? GenerationStarted)
    {
        public DictionaryPopupShowRequestState State { get; } = new();
    }

    private sealed record DictionaryPopupNativeContext(
        long Generation,
        long DocumentEpoch,
        string? TraceId,
        DictionaryDisplaySettings DisplaySettings,
        ThemeMode ThemeMode,
        AudioSettings AudioSettings,
        AnkiSettings AnkiSettings,
        AnkiMiningContext MiningContext,
        SasayakiPopupControls? SasayakiControls);

    public event EventHandler<DictionaryPopupRedirectRequest>? RedirectRequested;
    public event EventHandler? TapOutsideRequested;
    public event EventHandler? DismissRequested;
    public event EventHandler? Scrolled;
    public event EventHandler? ContentReady;
    public event EventHandler<DictionaryPopupContentCommittedEventArgs>? ContentCommitted;
    public event EventHandler<DictionaryPopupContentCommittedEventArgs>? ContentCommitAborted;
    public event EventHandler<DictionaryPopupShowDroppedEventArgs>? QueuedShowDropped;

    private readonly Grid _surfaceRoot;
    private readonly Grid _controlsRow;
    private readonly SolidColorBrush _surfaceBrush;
    private readonly SolidColorBrush _outlineBrush;
    private readonly CommandBar _actionBar;
    private readonly AppBarButton _popupBackButton;
    private readonly AppBarButton _popupForwardButton;
    private readonly AppBarButton _popupCloseButton;
    private readonly CommandBar _sasayakiControlsBar;
    private readonly AppBarButton _sasayakiPopupPlayPauseButton;
    private readonly AppBarButton _sasayakiPopupReplayCueButton;
    private readonly AppBarButton _sasayakiPopupJumpCueButton;
    private readonly FontIcon _sasayakiPopupPlayPauseIcon;
    private readonly WebView2 _contentWebView;
    private readonly InfoBar _miningToast;
    private readonly PopupHtmlGenerator _htmlGenerator;
    private readonly IDictionaryLookupService _lookupService;
    private readonly IAudioService _audioService;
    private readonly IAnkiService _ankiService;
    private readonly DictionaryPopupDisplayTransaction _displayTransaction = new();
    private readonly DictionaryPopupNavigationStateCoordinator _navigationStateCoordinator = new();
    private readonly DictionaryPopupWarmCoordinator _warmCoordinator = new();
    private readonly DictionaryPopupRecoveryCoordinator _recoveryCoordinator = new();
    private readonly SemaphoreSlim _webViewInitializationGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<bool>> _shellReadyWaiters = new();
    private readonly Dictionary<(long Generation, int EntryIndex), long> _openableAnkiNotes = [];
    private AnkiMiningContext _miningContext = new();
    private AnkiMiningContext _nextMiningContext = new();
    private SasayakiPopupControls? _sasayakiPopupControls;
    private AudioSettings _audioSettings = new();
    private AnkiSettings _ankiSettings = new();
    private DictionaryPopupNativeContext? _stagedNativeContext;
    private DictionaryPopupRecoveryTicket? _activeRecoveryTicket;
    private readonly DictionaryPopupLatestRequestQueue<DictionaryPopupShowRequest> _queuedShowRequests = new();
    private bool _isCompletingContentReady;
    private bool _webViewReady;
    private bool _webViewEventsSubscribed;
    private bool _actionBarPreference;
    private long _rendererEpochCounter;
    private long _rendererEpoch;
    private long _displayGeneration;
    private long? _pendingContentGeneration;
    private CancellationToken _pendingContentCancellationToken;
    private Stopwatch? _pendingContentStopwatch;
    private string? _currentTraceId;
    private double _readyOpacity = 1;
    private double _popupCornerRadius = 8;
    private bool _animateOpacityTransitions = true;
    private Storyboard? _opacityStoryboard;
    private EventHandler<object>? _opacityStoryboardCompletedHandler;
    private TaskCompletionSource<bool>? _opacityAnimationCompletion;
    private long _opacityAnimationGeneration;

    private const int MaxResolvedAudioUrlCacheEntries = 512;
    private const string AudioSourcePlaceholderPattern = "[^/?#&]+";
    // Niratan uses SwiftUI .default.speed(2.2) for presentation and
    // .default.speed(2.4) for dismissal. These durations preserve the same
    // slightly-slower entrance and slightly-faster exit on WinUI.
    private static readonly TimeSpan PopupEntranceFadeDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PopupExitFadeDuration = TimeSpan.FromMilliseconds(145);
    private static readonly TimeSpan PopupCommitTimeout = TimeSpan.FromSeconds(2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_resolvedAudioUrls = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string?>>> s_audioResolutionTasks = new(StringComparer.Ordinal);
    private static readonly HttpClient s_audioResolveHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly LocalAudioSourceListResolver s_localAudioSourceListResolver = new();
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _deferredResultsCts;
    private CancellationTokenSource? _miningToastCts;
    private MiningContextSelectionDialog? _contextMiningDialog;
    private Panel? _inPlaceDialogHost;

    public Border VisualRoot { get; }
    public bool IsWarmed => _warmCoordinator.IsWarm;
    public bool HasCommittedContent => _displayTransaction.HasCommittedContent;
    public long? CommittedGeneration => _displayTransaction.CommittedGeneration;

    public DictionaryLookupPopup()
    {
        _htmlGenerator = new PopupHtmlGenerator();
        _lookupService = App.GetService<IDictionaryLookupService>();
        _audioService = App.GetService<IAudioService>();
        _ankiService = App.GetService<IAnkiService>();

        var initialSurfaceColor =
            DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System);
        var initialOutlineColor =
            DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System);
        _surfaceBrush = new SolidColorBrush(initialSurfaceColor);
        _outlineBrush = new SolidColorBrush(initialOutlineColor);

        _contentWebView = new WebView2
        {
            DefaultBackgroundColor = initialSurfaceColor,
            IsTabStop = false,
            UseSystemFocusVisuals = false,
        };

        _miningToast = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 420,
            Margin = new Thickness(12),
        };
        AutomationProperties.SetAutomationId(_miningToast, "AnkiMiningToast");

        _popupBackButton = CreateCommandButton(
            "DictionaryPopupBackButton",
            "DictionaryPopupBackButton.AutomationProperties.Name",
            "Back",
            CreateCommandIcon("\uE72B"),
            PopupBackButton_Click);
        _popupForwardButton = CreateCommandButton(
            "DictionaryPopupForwardButton",
            "DictionaryPopupForwardButton.AutomationProperties.Name",
            "Forward",
            CreateCommandIcon("\uE72A"),
            PopupForwardButton_Click);
        _popupCloseButton = CreateCommandButton(
            "DictionaryPopupCloseButton",
            "DictionaryPopupCloseButton.AutomationProperties.Name",
            "Close",
            CreateCommandIcon("\uE711"),
            PopupCloseButton_Click);
        _popupBackButton.IsEnabled = false;
        _popupForwardButton.IsEnabled = false;

        _actionBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(8, 0, 0, 0),
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsDynamicOverflowEnabled = false,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_actionBar, "DictionaryPopupActionBar");
        AutomationProperties.SetName(
            _actionBar,
            ResourceStringHelper.GetString(
                "DictionaryPopupActionBar.AutomationProperties.Name",
                "Popup actions"));
        _actionBar.PrimaryCommands.Add(_popupBackButton);
        _actionBar.PrimaryCommands.Add(_popupForwardButton);

        _sasayakiPopupPlayPauseIcon = CreateCommandIcon("\uE768");
        _sasayakiPopupPlayPauseButton = CreateCommandButton(
            "NovelReaderPopupSasayakiPlayPauseButton",
            "NovelReaderPopupSasayakiPlayPauseButton.AutomationProperties.Name",
            "Play/Pause",
            _sasayakiPopupPlayPauseIcon,
            SasayakiPopupPlayPauseButton_Click);
        _sasayakiPopupReplayCueButton = CreateCommandButton(
            "NovelReaderPopupSasayakiReplayCueButton",
            "NovelReaderPopupSasayakiReplayCueButton.AutomationProperties.Name",
            "Replay Cue",
            CreateCommandIcon("\uE72C"),
            SasayakiPopupReplayCueButton_Click);
        _sasayakiPopupJumpCueButton = CreateCommandButton(
            "NovelReaderPopupSasayakiJumpCueButton",
            "NovelReaderPopupSasayakiJumpCueButton.AutomationProperties.Name",
            "Jump Cue",
            CreateCommandIcon("\uE8AD"),
            SasayakiPopupJumpCueButton_Click);

        _sasayakiControlsBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0),
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsDynamicOverflowEnabled = false,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_sasayakiControlsBar, "NovelReaderPopupSasayakiControls");
        AutomationProperties.SetName(
            _sasayakiControlsBar,
            ResourceStringHelper.GetString(
                "NovelReaderPopupSasayakiControls.AutomationProperties.Name",
                "Sasayaki controls"));
        _sasayakiControlsBar.PrimaryCommands.Add(_sasayakiPopupPlayPauseButton);
        _sasayakiControlsBar.PrimaryCommands.Add(_sasayakiPopupReplayCueButton);
        _sasayakiControlsBar.PrimaryCommands.Add(_sasayakiPopupJumpCueButton);

        _popupCloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        _popupCloseButton.VerticalAlignment = VerticalAlignment.Center;
        _popupCloseButton.Margin = new Thickness(0, 0, 8, 0);
        _popupCloseButton.Visibility = Visibility.Collapsed;

        _controlsRow = new Grid
        {
            MinHeight = 36,
            Visibility = Visibility.Collapsed,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
            Children =
            {
                _actionBar,
                _sasayakiControlsBar,
                _popupCloseButton,
            },
        };
        Grid.SetColumn(_actionBar, 0);
        Grid.SetColumn(_sasayakiControlsBar, 1);
        Grid.SetColumn(_popupCloseButton, 2);

        _surfaceRoot = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
            Children =
            {
                _controlsRow,
                _contentWebView,
                _miningToast,
            },
        };
        Grid.SetRow(_controlsRow, 0);
        Grid.SetRow(_contentWebView, 1);
        Grid.SetRow(_miningToast, 1);
        Canvas.SetZIndex(_miningToast, 1);

        VisualRoot = new Border
        {
            Background = _surfaceBrush,
            BorderBrush = _outlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_popupCornerRadius),
            Child = _surfaceRoot,
            Visibility = Visibility.Visible,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        UpdatePopupShellGeometry();
    }

    private static FontIcon CreateCommandIcon(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 16,
    };

    private static AppBarButton CreateCommandButton(
        string automationId,
        string nameResourceKey,
        string fallbackName,
        IconElement icon,
        RoutedEventHandler clickHandler)
    {
        var name = ResourceStringHelper.GetString(nameResourceKey, fallbackName);
        var button = new AppBarButton
        {
            Icon = icon,
            Label = name,
            IsCompact = true,
            MinWidth = 44,
            MinHeight = 32,
            Height = 32,
            Padding = new Thickness(0),
        };
        button.Click += clickHandler;
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void UpdateActionBarVisibility(DictionaryDisplaySettings displaySettings)
    {
        _actionBarPreference = displaySettings.PopupActionBar;
        UpdateActionBarVisibility();
    }

    private void UpdateActionBarVisibility()
    {
        var isVisible = _actionBarPreference
            || _navigationStateCoordinator.CanGoBack
            || _navigationStateCoordinator.CanGoForward;
        _navigationStateCoordinator.SetVisibility(isVisible);
        _actionBar.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _popupCloseButton.Visibility = _actionBar.Visibility;
        UpdateControlsRowVisibility();
    }

    private void UpdateControlsRowVisibility() =>
        _controlsRow.Visibility = _actionBar.Visibility == Visibility.Visible
            || _sasayakiControlsBar.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void ResetActionBarNavigationState()
    {
        _popupBackButton.IsEnabled = false;
        _popupForwardButton.IsEnabled = false;
    }

    private async void PopupBackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateBackAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DictPopup] Back navigation failed");
        }
    }

    private async void PopupForwardButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await NavigateForwardAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DictPopup] Forward navigation failed");
        }
    }

    private void PopupCloseButton_Click(object sender, RoutedEventArgs e) =>
        DismissRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateSasayakiPopupControls()
    {
        var controls = _sasayakiPopupControls;
        if (controls == null)
        {
            _sasayakiControlsBar.Visibility = Visibility.Collapsed;
            UpdateControlsRowVisibility();
            return;
        }

        _sasayakiControlsBar.Visibility = Visibility.Visible;
        UpdateControlsRowVisibility();
        var canControl = controls.CanControl?.Invoke() ?? true;
        _sasayakiPopupPlayPauseButton.IsEnabled = canControl;
        _sasayakiPopupReplayCueButton.IsEnabled = canControl;
        _sasayakiPopupJumpCueButton.IsEnabled = canControl;
        _sasayakiPopupPlayPauseIcon.Glyph = controls.IsPlaying?.Invoke() == true
            ? "\uE769"
            : "\uE768";
    }

    private async void SasayakiPopupPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleSasayakiPopupPlayPauseAsync();
    }

    private async void SasayakiPopupReplayCueButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleSasayakiPopupReplayCueAsync();
    }

    private async void SasayakiPopupJumpCueButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleSasayakiPopupJumpCueAsync();
    }

    private async Task HandleSasayakiPopupPlayPauseAsync()
    {
        var controls = _sasayakiPopupControls;
        if (controls == null || controls.CanControl?.Invoke() == false)
            return;

        await controls.TogglePlaybackAsync();
        UpdateSasayakiPopupControls();
    }

    private async Task HandleSasayakiPopupReplayCueAsync()
    {
        var controls = _sasayakiPopupControls;
        if (controls == null || controls.CanControl?.Invoke() == false)
            return;

        await controls.ReplayCueAsync();
        UpdateSasayakiPopupControls();
    }

    private async Task HandleSasayakiPopupJumpCueAsync()
    {
        var controls = _sasayakiPopupControls;
        if (controls == null || controls.CanControl?.Invoke() == false)
            return;

        await controls.JumpToCueAsync();
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UseStandaloneWindowVisuals()
    {
        _contentWebView.Margin = new Thickness(-1);
        SetPopupCornerRadius(0);
    }

    public void UseNakedFloatingWindowVisuals()
    {
        _contentWebView.Margin = new Thickness(0);
        SetPopupCornerRadius(8);
    }

    public void UseImmediateOpacityTransitions()
    {
        _animateOpacityTransitions = false;
        CancelOpacityAnimation();
        if (VisualRoot.IsHitTestVisible)
            VisualRoot.Opacity = _readyOpacity;
    }

    public Task WarmAsync(
        ThemeMode themeMode = ThemeMode.System,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null)
    {
        var normalizedAudioSettings = audioSettings ?? new AudioSettings();
        var normalizedAnkiSettings = ankiSettings ?? new AnkiSettings();
        return _warmCoordinator.EnsureWarmAsync(lease => WarmCoreAsync(
            themeMode,
            normalizedAudioSettings,
            normalizedAnkiSettings,
            traceId,
            lease));
    }

    private async Task WarmCoreAsync(
        ThemeMode themeMode,
        AudioSettings audioSettings,
        AnkiSettings ankiSettings,
        string? traceId,
        DictionaryPopupWarmLease lease)
    {
        lease.ThrowIfInvalid();
        ApplySurfaceTheme(themeMode);
        var sw = Stopwatch.StartNew();

        await EnsureWebViewAsync(lease);
        lease.ThrowIfInvalid();
        Log.Information(
            "[LookupTrace] trace={TraceId} popup warm EnsureWebView2 completed in {Ms}ms",
            traceId ?? "-", sw.ElapsedMilliseconds);
        var documentEpoch = Interlocked.Increment(ref _rendererEpochCounter);
        var shellReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_shellReadyWaiters.TryAdd(documentEpoch, shellReady))
            throw new InvalidOperationException($"Duplicate popup document epoch {documentEpoch}.");
        try
        {
            lease.ThrowIfInvalid();
            _contentWebView.CoreWebView2.NavigateToString(_htmlGenerator.GenerateShellHtml(
                themeMode,
                audioSettings: audioSettings,
                ankiSettings: ankiSettings,
                hidden: true,
                documentEpoch: documentEpoch));
            Log.Information(
                "[LookupTrace] trace={TraceId} popup warm NavigateToString returned in {Ms}ms epoch={Epoch}",
                traceId ?? "-", sw.ElapsedMilliseconds, documentEpoch);
            await WaitForShellReadyAsync(documentEpoch, shellReady.Task, lease);
            lease.ThrowIfInvalid();
            _rendererEpoch = documentEpoch;
            await ApplyPopupCornerRadiusToWebViewAsync();
            lease.ThrowIfInvalid();
        }
        finally
        {
            _shellReadyWaiters.TryRemove(documentEpoch, out _);
        }
        Log.Information("[DictPopup] Warm WebView2 initialized in {Ms}ms", sw.ElapsedMilliseconds);
    }

    public void SetMiningContext(AnkiMiningContext? context)
    {
        _nextMiningContext = context ?? new AnkiMiningContext();
    }

    public void SetInPlaceDialogHost(Panel? host)
    {
        _inPlaceDialogHost = host;
    }

    public void SetReadyOpacity(double opacity)
    {
        _readyOpacity = Math.Clamp(opacity, 0, 1);
        if (VisualRoot.Opacity > 0)
            VisualRoot.Opacity = _readyOpacity;
    }

    public Windows.Foundation.Point GetWebContentOffset()
    {
        try
        {
            return _contentWebView
                .TransformToVisual(VisualRoot)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return new Windows.Foundation.Point(0, 0);
        }
    }

    private void SetPopupCornerRadius(double radius)
    {
        _popupCornerRadius = Math.Max(0, radius);
        UpdatePopupShellGeometry();
        _ = ApplyPopupCornerRadiusToWebViewAsync();
    }

    private void UpdatePopupShellGeometry()
    {
        var guardInset = DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius);
        VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius);
        _surfaceRoot.Margin = new Thickness(guardInset);
    }

    private void ApplySurfaceTheme(ThemeMode themeMode)
    {
        var surfaceColor = DictionaryPopupMaterial.GetOpaqueSurfaceColor(themeMode);
        _surfaceBrush.Color = surfaceColor;
        _outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);
        _contentWebView.DefaultBackgroundColor = surfaceColor;
    }

    public async Task ShowResultsWarmAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        ThemeMode themeMode,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null,
        CancellationToken cancellationToken = default,
        Action<long>? generationStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedAudioSettings = audioSettings ?? new AudioSettings();
        var normalizedAnkiSettings = ankiSettings ?? new AnkiSettings();
        var miningContext = _nextMiningContext;
        var request = new DictionaryPopupShowRequest(
            results,
            styles,
            displaySettings,
            themeMode,
            normalizedAudioSettings,
            normalizedAnkiSettings,
            miningContext,
            miningContext.SasayakiPopupControls,
            traceId,
            cancellationToken,
            generationStarted);

        if (_displayTransaction.CommitInFlightGeneration is not null
            || _isCompletingContentReady)
        {
            QueueShowRequest(request);
            RetryForcedShellRecoveryIfNeeded();
            return;
        }

        await ShowResultsWarmCoreAsync(request);
    }

    private async Task ShowResultsWarmCoreAsync(DictionaryPopupShowRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        CancelPrefetch();
        CancelDeferredResults();
        if (!IsWarmed)
        {
            await WarmAsync(
                request.ThemeMode,
                request.AudioSettings,
                request.AnkiSettings,
                request.TraceId);
        }
        request.CancellationToken.ThrowIfCancellationRequested();

        if (_displayTransaction.CommitInFlightGeneration is not null
            || _isCompletingContentReady)
        {
            QueueShowRequest(request);
            RetryForcedShellRecoveryIfNeeded();
            return;
        }

        var ranges = DictionaryPopupBatchPlanner.Create(request.Results.Count);
        var initialRange = ranges[0];
        var initialResults = request.Results.GetRange(initialRange.Offset, initialRange.Count);
        CancelPendingContentBeforeStartingNextGeneration();
        var generation = PrepareForPendingContent(
            request.CancellationToken,
            request.TraceId);
        if (!request.State.TryStartGeneration())
        {
            CancelPendingContent(generation, request.TraceId);
            return;
        }

        try
        {
            request.GenerationStarted?.Invoke(generation);
            _stagedNativeContext = new DictionaryPopupNativeContext(
                generation,
                _rendererEpoch,
                request.TraceId,
                request.DisplaySettings,
                request.ThemeMode,
                request.AudioSettings,
                request.AnkiSettings,
                request.MiningContext,
                request.SasayakiControls);
            _pendingContentStopwatch = Stopwatch.StartNew();
            var serializeSw = Stopwatch.StartNew();
            var injectionScript = _htmlGenerator.GenerateInjectionScript(initialResults,
                request.Styles,
                request.DisplaySettings,
                request.ThemeMode,
                generation,
                request.AudioSettings,
                request.AnkiSettings,
                traceId: request.TraceId,
                totalResultCount: request.Results.Count,
                documentEpoch: _rendererEpoch,
                contextMiningAvailable: request.MiningContext.ContextSelection != null);
            var payloadBytes = Encoding.UTF8.GetByteCount(injectionScript);
            Log.Information(
                "[LookupTrace] trace={TraceId} popup initial serialized in {Ms}ms bytes={Bytes} entries={EntryCount} total={TotalCount}",
                request.TraceId ?? "-",
                serializeSw.ElapsedMilliseconds,
                payloadBytes,
                initialResults.Count,
                request.Results.Count);
            var executeSw = Stopwatch.StartNew();
            await _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
            request.CancellationToken.ThrowIfCancellationRequested();
            Log.Information(
                "[LookupTrace] trace={TraceId} popup initial ExecuteScriptAsync finished in {Ms}ms total={TotalMs}ms gen={Gen} entries={EntryCount}",
                request.TraceId ?? "-", executeSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, generation, initialResults.Count);
            Log.Information("[Lifecycle] Popup initial content injected: entries={EntryCount} total={TotalCount} gen={Gen}",
                initialResults.Count, request.Results.Count, generation);
            PrefetchAudioUrls(request.Results, request.AudioSettings, request.TraceId);

            if (ranges.Count > 1)
            {
                var deferredCts = request.CancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken)
                    : new CancellationTokenSource();
                _deferredResultsCts = deferredCts;
                _ = AppendDeferredResultsAsync(
                    request.Results,
                    ranges.Skip(1).ToArray(),
                    request.Results.Count,
                    generation,
                    _rendererEpoch,
                    request.TraceId,
                    deferredCts);
            }
        }
        catch
        {
            CancelPendingContent(generation, request.TraceId);
            throw;
        }
    }

    public async Task<bool> ShowRedirectResultsAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        ThemeMode themeMode,
        long expectedCommittedGeneration,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        if (CommittedGeneration != expectedCommittedGeneration)
            return false;
        var normalizedAudioSettings = audioSettings ?? new AudioSettings();
        var normalizedAnkiSettings = ankiSettings ?? new AnkiSettings();
        if (!IsWarmed)
            await WarmAsync(themeMode, normalizedAudioSettings, normalizedAnkiSettings);
        cancellationToken.ThrowIfCancellationRequested();
        if (CommittedGeneration != expectedCommittedGeneration)
            return false;

        CancelPrefetch();
        CancelDeferredResults();
        var injectionScript = _htmlGenerator.GenerateRedirectInjectionScript(
            results,
            styles,
            displaySettings,
            themeMode,
            expectedCommittedGeneration,
            normalizedAudioSettings,
            normalizedAnkiSettings,
            traceId,
            contextMiningAvailable: _miningContext.ContextSelection != null);
        var appliedJson = await _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
        cancellationToken.ThrowIfCancellationRequested();
        if (CommittedGeneration != expectedCommittedGeneration
            || !string.Equals(appliedJson, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        ApplySurfaceTheme(themeMode);
        _currentTraceId = traceId;
        _audioSettings = normalizedAudioSettings;
        _ankiSettings = normalizedAnkiSettings;
        UpdateActionBarVisibility(displaySettings);
        UpdateSasayakiPopupControls();
        PrefetchAudioUrls(results, normalizedAudioSettings, traceId);
        return true;
    }

    public async Task NavigateBackAsync()
    {
        if (_contentWebView.CoreWebView2 is not null)
            await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateBack?.()");
    }

    public async Task NavigateForwardAsync()
    {
        if (_contentWebView.CoreWebView2 is not null)
            await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateForward?.()");
    }

    private async Task AppendDeferredResultsAsync(
        List<DictionaryLookupResult> results,
        IReadOnlyList<DictionaryPopupBatchRange> ranges,
        int totalResultCount,
        long generation,
        long documentEpoch,
        string? traceId,
        CancellationTokenSource owner)
    {
        var ct = owner.Token;
        try
        {
            await Task.Yield();
            for (var batchIndex = 0; batchIndex < ranges.Count; batchIndex++)
            {
                ct.ThrowIfCancellationRequested();
                if (generation != _displayGeneration)
                    return;

                var range = ranges[batchIndex];
                var batch = results.GetRange(range.Offset, range.Count);
                var serializeSw = Stopwatch.StartNew();
                var script = _htmlGenerator.GenerateAppendResultsScript(
                    batch,
                    totalResultCount,
                    generation,
                    documentEpoch);
                var payloadBytes = Encoding.UTF8.GetByteCount(script);
                Log.Information(
                    "[LookupTrace] trace={TraceId} popup deferred batch serialized in {Ms}ms bytes={Bytes} batch={BatchIndex} entries={EntryCount} gen={Gen}",
                    traceId ?? "-",
                    serializeSw.ElapsedMilliseconds,
                    payloadBytes,
                    batchIndex,
                    batch.Count,
                    generation);

                var executeSw = Stopwatch.StartNew();
                var rawResult = await _contentWebView.CoreWebView2.ExecuteScriptAsync(script);
                var appendStatus = JsonSerializer.Deserialize<string>(rawResult) ?? "unknown";
                if (!string.Equals(appendStatus, "appended", StringComparison.Ordinal))
                {
                    if (string.Equals(appendStatus, "stale", StringComparison.Ordinal))
                    {
                        Log.Debug(
                            "[LookupTrace] trace={TraceId} popup deferred batch rejected status={Status} batch={BatchIndex} gen={Gen}",
                            traceId ?? "-",
                            appendStatus,
                            batchIndex,
                            generation);
                    }
                    else
                    {
                        Log.Warning(
                            "[LookupTrace] trace={TraceId} popup deferred batch rejected status={Status} batch={BatchIndex} gen={Gen}",
                            traceId ?? "-",
                            appendStatus,
                            batchIndex,
                            generation);
                    }
                    return;
                }
                Log.Information(
                    "[LookupTrace] trace={TraceId} popup deferred batch transferred in {Ms}ms batch={BatchIndex} entries={EntryCount} gen={Gen}",
                    traceId ?? "-",
                    executeSw.ElapsedMilliseconds,
                    batchIndex,
                    batch.Count,
                    generation);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Debug(
                "[LookupTrace] trace={TraceId} popup deferred batches cancelled gen={Gen}",
                traceId ?? "-",
                generation);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[LookupTrace] trace={TraceId} popup deferred batch failed gen={Gen}",
                traceId ?? "-",
                generation);
        }
        finally
        {
            if (ReferenceEquals(_deferredResultsCts, owner))
                _deferredResultsCts = null;
            owner.Dispose();
        }
    }

    public void Hide()
    {
        _ = HideAnimatedAsync();
    }

    public Task<bool> HideAnimatedAsync()
    {
        Log.Information("[Lifecycle] Popup hidden: wasGen={Gen}", _displayGeneration);
        CancelPrefetch();
        CancelDeferredResults();
        if (_stagedNativeContext is { } recoveringContext
            && _recoveryCoordinator.Cancel(
                recoveringContext.Generation,
                recoveringContext.DocumentEpoch))
        {
            _activeRecoveryTicket = null;
            _warmCoordinator.Reset();
        }
        NotifyQueuedShowDropped(_queuedShowRequests.Clear());
        _stagedNativeContext = null;
        _displayTransaction.Dismiss();
        _navigationStateCoordinator.Reset();
        _openableAnkiNotes.Clear();
        ResetActionBarNavigationState();
        _displayGeneration++;
        _pendingContentGeneration = null;
        _pendingContentCancellationToken = default;
        _pendingContentStopwatch = null;
        VisualRoot.IsHitTestVisible = false;
        return AnimateOpacityAsync(0, PopupExitFadeDuration);
    }

    public bool CancelPendingContent(long generation, string? traceId)
    {
        var documentEpoch = _stagedNativeContext?.Generation == generation
            ? _stagedNativeContext.DocumentEpoch
            : _rendererEpoch;
        if (_pendingContentGeneration != generation
            || !_displayTransaction.TryCancelPending(
                generation,
                traceId,
                out var aborted))
        {
            return false;
        }

        _pendingContentGeneration = null;
        _pendingContentCancellationToken = default;
        _pendingContentStopwatch = null;
        if (_stagedNativeContext?.Generation == generation)
            _stagedNativeContext = null;

        if (_contentWebView.CoreWebView2 is not null)
        {
            _ = _contentWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.niratanCancelPopupRender?.({documentEpoch}, {generation});");
        }

        ContentCommitAborted?.Invoke(
            this,
            new DictionaryPopupContentCommittedEventArgs(
                aborted.Generation,
                aborted.TraceId));

        if (!_displayTransaction.HasCommittedContent)
            Hide();

        return true;
    }

    public void SetSize(double width, double height)
    {
        if (width > 0) VisualRoot.Width = width;
        if (height > 0) VisualRoot.Height = height;
    }

    private async Task ApplyPopupCornerRadiusToWebViewAsync()
    {
        try
        {
            if (!_webViewReady || _contentWebView.CoreWebView2 == null)
                return;

            var radius = $"{_popupCornerRadius:0.###}px";
            var script = $$"""
                (() => {
                    document.documentElement.style.setProperty('--popup-corner-radius', {{JsonSerializer.Serialize(radius)}});
                })();
                """;
            await _contentWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictPopup] Applying popup corner radius failed");
        }
    }

    public async Task<bool> HighlightSelectionAsync(
        string matchedText,
        long expectedCommittedGeneration)
    {
        if (!_webViewReady
            || _contentWebView.CoreWebView2 == null
            || string.IsNullOrEmpty(matchedText)
            || _displayTransaction.CommittedGeneration != expectedCommittedGeneration)
        {
            return false;
        }

        var highlightCount = matchedText.EnumerateRunes().Count();
        if (highlightCount <= 0)
            return false;

        var result = await _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.niratanHighlightPopupSelection?.({highlightCount}, {expectedCommittedGeneration});");
        return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureWebViewAsync(DictionaryPopupWarmLease lease)
    {
        lease.ThrowIfInvalid();
        if (_webViewReady) return;

        await _webViewInitializationGate.WaitAsync(lease.CancellationToken);
        try
        {
            lease.ThrowIfInvalid();
            if (_webViewReady) return;

            var environment = await WebView2EnvironmentHelper.GetOrCreateAsync();
            lease.ThrowIfInvalid();
            await _contentWebView.EnsureCoreWebView2Async(environment);
            lease.ThrowIfInvalid();
            _contentWebView.DefaultBackgroundColor = _surfaceBrush.Color;
            var coreWebView = _contentWebView.CoreWebView2;
            if (coreWebView == null)
                throw new InvalidOperationException("Dictionary popup WebView2 initialization was cancelled.");

            coreWebView.Settings.IsScriptEnabled = true;
            coreWebView.Settings.IsWebMessageEnabled = true;
            if (!_webViewEventsSubscribed)
            {
                lease.ThrowIfInvalid();
                coreWebView.WebMessageReceived += OnPopupWebMessageReceived;
                coreWebView.AddWebResourceRequestedFilter(
                    "https://niratan-dictionary-media.local/*",
                    CoreWebView2WebResourceContext.Image);
                coreWebView.AddWebResourceRequestedFilter(
                    "https://niratan-audio-resolver.local/*",
                    CoreWebView2WebResourceContext.All);
                coreWebView.WebResourceRequested += OnPopupWebResourceRequested;
                coreWebView.ProcessFailed += OnPopupWebViewProcessFailed;
                coreWebView.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown")
                    .DevToolsProtocolEventReceived += (s, a) =>
                        Log.Error("[DictPopup] JS exception: {Event}", a.ParameterObjectAsJson);
                _webViewEventsSubscribed = true;
            }

            try
            {
                await coreWebView.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DictPopup] Failed to enable DevTools protocol");
            }

            lease.ThrowIfInvalid();
            _webViewReady = true;
        }
        finally
        {
            _webViewInitializationGate.Release();
        }
    }

    private void OnPopupWebViewProcessFailed(
        CoreWebView2 sender,
        CoreWebView2ProcessFailedEventArgs args)
    {
        Log.Error(
            "[DictPopup] WebView2 ProcessFailed: Kind={Kind}, ExitCode={ExitCode}, Reason={Reason}",
            args.ProcessFailedKind,
            args.ExitCode,
            args.Reason);
        _warmCoordinator.Reset();
        var failure = new InvalidOperationException(
            "Dictionary popup WebView2 process failed.");
        foreach (var shellReady in _shellReadyWaiters.Values)
            shellReady.TrySetException(failure);

        if (_contentWebView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_displayTransaction.CommitInFlightGeneration is long acceptedGeneration
                && _stagedNativeContext is { } acceptedContext
                && acceptedContext.Generation == acceptedGeneration)
            {
                StartForcedShellRecovery(
                    acceptedGeneration,
                    acceptedContext.DocumentEpoch);
                return;
            }

            if (_pendingContentGeneration is long pendingGeneration)
            {
                var traceId = _stagedNativeContext?.Generation == pendingGeneration
                    ? _stagedNativeContext.TraceId
                    : null;
                if (CancelPendingContent(pendingGeneration, traceId))
                {
                    _ = ProcessQueuedShowAsync();
                }
            }
        }))
        {
            return;
        }
    }

    private async void OnPopupWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
            return;

        if (string.Equals(uri.Host, "niratan-audio-resolver.local", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, "/resolve", StringComparison.OrdinalIgnoreCase))
        {
            var audioDeferral = args.GetDeferral();
            try
            {
                await HandleAudioResolverRequestAsync(sender, args, uri);
            }
            finally
            {
                audioDeferral.Complete();
            }

            return;
        }

        if (!string.Equals(uri.Host, "niratan-dictionary-media.local", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, "/image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deferral = args.GetDeferral();
        var sw = Stopwatch.StartNew();
        try
        {
            var dictionary = GetQueryParameter(uri, "dictionary");
            var path = GetQueryParameter(uri, "path");
            if (string.IsNullOrWhiteSpace(dictionary) || string.IsNullOrWhiteSpace(path))
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    Stream.Null.AsRandomAccessStream(),
                    400,
                    "Bad Request",
                    "Access-Control-Allow-Origin: *\r\n");
                return;
            }

            var bytes = await _lookupService.GetMediaFileAsync(dictionary, path);
            if (bytes is not { Length: > 0 })
            {
                Log.Information(
                    "[LookupTrace] trace={TraceId} dictionary media miss in {Ms}ms dictionary='{Dictionary}' path='{Path}'",
                    _currentTraceId ?? "-", sw.ElapsedMilliseconds, dictionary, path);
                args.Response = sender.Environment.CreateWebResourceResponse(
                    Stream.Null.AsRandomAccessStream(),
                    404,
                    "Not Found",
                    "Access-Control-Allow-Origin: *\r\n");
                return;
            }

            var stream = new MemoryStream(bytes);
            args.Response = sender.Environment.CreateWebResourceResponse(
                stream.AsRandomAccessStream(),
                200,
                "OK",
                $"Content-Type: {GetImageMimeType(path)}\r\nAccess-Control-Allow-Origin: *\r\n");
            Log.Information(
                "[LookupTrace] trace={TraceId} dictionary media served in {Ms}ms bytes={Bytes} dictionary='{Dictionary}' path='{Path}'",
                _currentTraceId ?? "-", sw.ElapsedMilliseconds, bytes.Length, dictionary, path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DictPopup] Failed to serve dictionary media {Uri}", args.Request.Uri);
            args.Response = sender.Environment.CreateWebResourceResponse(
                Stream.Null.AsRandomAccessStream(),
                500,
                "Internal Server Error",
                "Access-Control-Allow-Origin: *\r\n");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleAudioResolverRequestAsync(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args,
        Uri uri)
    {
        var sw = Stopwatch.StartNew();
        var traceId = GetQueryParameter(uri, "lookupTraceId") ?? _currentTraceId ?? "-";
        var audioTraceId = GetQueryParameter(uri, "audioTraceId") ?? "-";
        try
        {
            var url = GetQueryParameter(uri, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    Stream.Null.AsRandomAccessStream(),
                    400,
                    "Bad Request",
                    "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\n");
                return;
            }

            url = NormalizeAudioSourceUrl(url);
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} resolver request received url='{Url}'",
                traceId,
                audioTraceId,
                url);
            if (!IsAllowedAudioResolverUrl(url))
            {
                Log.Warning("[DictPopup] Rejected audio resolver URL outside configured sources: {Url}", url);
                args.Response = sender.Environment.CreateWebResourceResponse(
                    Stream.Null.AsRandomAccessStream(),
                    403,
                    "Forbidden",
                    "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\n");
                return;
            }

            var resolvedUrl = await TryResolveAudioUrlAsync(url, traceId, audioTraceId);
            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                var emptyJson = """{"type":"audioSourceList","audioSources":[]}""";
                var emptyBytes = Encoding.UTF8.GetBytes(emptyJson);
                args.Response = sender.Environment.CreateWebResourceResponse(
                    new MemoryStream(emptyBytes).AsRandomAccessStream(),
                    200,
                    "OK",
                    "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\n");
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} resolver request completed in {Ms}ms hit=false",
                    traceId,
                    audioTraceId,
                    sw.ElapsedMilliseconds);
                return;
            }

            var json = JsonSerializer.Serialize(new
            {
                type = "audioSourceList",
                audioSources = new[] { new { url = resolvedUrl } },
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            args.Response = sender.Environment.CreateWebResourceResponse(
                new MemoryStream(bytes).AsRandomAccessStream(),
                200,
                "OK",
                "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\n");
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} resolver request completed in {Ms}ms hit=true resolved='{ResolvedUrl}'",
                traceId,
                audioTraceId,
                sw.ElapsedMilliseconds,
                resolvedUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DictPopup] Failed to resolve audio URL {Uri}", args.Request.Uri);
            args.Response = sender.Environment.CreateWebResourceResponse(
                Stream.Null.AsRandomAccessStream(),
                500,
                "Internal Server Error",
                "Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\n");
        }
    }

    private bool IsAllowedAudioResolverUrl(string url) =>
        IsAllowedAudioResolverUrl(url, _audioSettings);

    private static bool IsAllowedAudioResolverUrl(string url, AudioSettings audioSettings)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
            return false;

        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        if (audioSettings.EnableLocalAudio && LocalAudioSourceListResolver.IsLocalAudioSourceListUrl(url))
            return true;

        foreach (var template in audioSettings.EnabledAudioSourceUrls)
        {
            var normalizedTemplate = NormalizeAudioSourceUrl(template);
            if (string.IsNullOrWhiteSpace(normalizedTemplate))
                continue;

            if (!normalizedTemplate.Contains("{term}", StringComparison.Ordinal)
                && !normalizedTemplate.Contains("{reading}", StringComparison.Ordinal))
            {
                if (string.Equals(url, normalizedTemplate, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            var escapedTemplate = Regex.Escape(normalizedTemplate)
                .Replace("\\{term}", AudioSourcePlaceholderPattern, StringComparison.Ordinal)
                .Replace("\\{reading}", AudioSourcePlaceholderPattern, StringComparison.Ordinal);
            if (Regex.IsMatch(
                    url,
                    "^" + escapedTemplate + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPopupWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var versionElement)
                || versionElement.GetInt32() != 1)
                return;

            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.GetString() != "popupMessage")
                return;

            var payload = root.GetProperty("payload");
            var name = payload.GetProperty("name").GetString();

            switch (name)
            {
                case "shellReady":
                    Log.Information("[DictPopup] Shell ready: {Payload}", payload.GetRawText());
                    if (TryGetContentEpoch(payload, out var shellEpoch)
                        && _shellReadyWaiters.TryGetValue(shellEpoch, out var shellReady))
                    {
                        shellReady.TrySetResult(true);
                    }
                    break;

                case "contentPrepared":
                    Log.Information("[DictPopup] Content prepared: {Payload}", payload.GetRawText());
                    if (!IsCurrentContentReady(payload))
                    {
                        Log.Debug("[DictPopup] Ignored stale contentPrepared: {Payload}", payload.GetRawText());
                        break;
                    }

                    var preparedGeneration = _pendingContentGeneration!.Value;
                    if (!TryGetContentEpoch(payload, out var preparedEpoch)
                        || !CanShowReadyContent(preparedGeneration)
                        || _displayTransaction.PendingGeneration != preparedGeneration
                        || _stagedNativeContext?.Generation != preparedGeneration
                        || _stagedNativeContext.DocumentEpoch != preparedEpoch
                        || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, preparedEpoch)
                        || _contentWebView.CoreWebView2 is null
                        || !_displayTransaction.TryAcceptCommit(preparedGeneration))
                        break;

                    _ = ObservePopupCommitAsync(preparedGeneration);
                    break;

                case "contentReady":
                    Log.Information("[DictPopup] Content ready: {Payload}", payload.GetRawText());
                    Log.Information(
                        "[LookupTrace] trace={TraceId} popup contentReady message received gen={Gen} elapsedSinceInject={Ms}ms",
                        _stagedNativeContext?.TraceId ?? "-", _pendingContentGeneration, _pendingContentStopwatch?.ElapsedMilliseconds ?? -1);
                    if (!TryGetContentGeneration(payload, out var readyGeneration)
                        || !TryGetContentEpoch(payload, out var readyEpoch)
                        || _stagedNativeContext?.DocumentEpoch != readyEpoch
                        || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, readyEpoch)
                        || _displayTransaction.CommitInFlightGeneration != readyGeneration)
                    {
                        Log.Debug("[DictPopup] Ignored stale contentReady: {Payload}", payload.GetRawText());
                        break;
                    }

                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        CompleteAcceptedCommit(readyGeneration, readyEpoch));
                    break;

                case "popupDiagnostic":
                    LogPopupDiagnostic(payload);
                    break;

                case "navigationState":
                    UpdateNavigationState(payload);
                    break;

                case "tapOutside":
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        TapOutsideRequested?.Invoke(this, EventArgs.Empty));
                    break;

                case "lookupRedirect":
                    var request = ParseRedirectRequest(payload);
                    Log.Information(
                        "[LookupTrace] trace={TraceId} popup lookupRedirect received query='{Query}' source={Source} selectedLength={SelectedLength} selectMs={SelectMs:F1} rectMs={RectMs:F1} clientNow={ClientNow:F1}",
                        _currentTraceId ?? "-",
                        request.Query,
                        request.Source ?? "-",
                        request.SelectedLength,
                        request.SelectMs,
                        request.RectMs,
                        request.ClientNow);
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        RedirectRequested?.Invoke(this, request));
                    break;

                case "openLink":
                    var url = payload.GetProperty("body").GetString() ?? "";
                    _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
                    });
                    break;

                case "popupError":
                    var errorBody = payload.GetProperty("body").GetString() ?? "";
                    Log.Error("[DictPopup] Popup render error: {Error}", errorBody);
                    break;

                case "popupScrolled":
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        Scrolled?.Invoke(this, EventArgs.Empty));
                    break;

                case "playWordAudio":
                    // Extract values synchronously — payload is a JsonElement that references
                    // the JsonDocument, which is disposed when this handler returns. Passing the
                    // element into TryEnqueue would access a disposed object and throw.
                    {
                        var audioUrl = payload.TryGetProperty("body", out var audioBody)
                            && audioBody.ValueKind == JsonValueKind.Object
                            && audioBody.TryGetProperty("url", out var urlEl)
                            ? urlEl.GetString() ?? ""
                            : "";
                        var audioModeStr = audioBody.ValueKind == JsonValueKind.Object
                            && audioBody.TryGetProperty("mode", out var modeEl)
                            ? modeEl.GetString() ?? "interrupt"
                            : "interrupt";
                        var audioMode = audioModeStr switch
                        {
                            "duck" => AudioPlaybackMode.Duck,
                            "mix" => AudioPlaybackMode.Mix,
                            _ => AudioPlaybackMode.Interrupt,
                        };
                        var traceId = audioBody.ValueKind == JsonValueKind.Object
                            && audioBody.TryGetProperty("lookupTraceId", out var traceEl)
                            ? traceEl.GetString() ?? _currentTraceId
                            : _currentTraceId;
                        var audioTraceId = audioBody.ValueKind == JsonValueKind.Object
                            && audioBody.TryGetProperty("audioTraceId", out var audioTraceEl)
                            ? audioTraceEl.GetString() ?? "-"
                            : "-";
                        _contentWebView.DispatcherQueue.TryEnqueue(() =>
                            HandlePlayWordAudio(audioUrl, audioMode, traceId, audioTraceId));
                    }
                    break;

                case "mineEntry":
                    HandleMineEntry(payload);
                    break;

                case "prepareContextMining":
                    HandlePrepareContextMining(payload);
                    break;

                case "openAnkiNote":
                    HandleOpenAnkiNote(payload);
                    break;

                case "duplicateCheck":
                    HandleDuplicateCheck(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictPopup] Failed to process WebMessage");
        }
    }

    private void LogPopupDiagnostic(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body)
            || body.ValueKind != JsonValueKind.Object)
        {
            Log.Information("[DictPopup] Diagnostic: {Payload}", payload.GetRawText());
            return;
        }

        var kind = body.TryGetProperty("kind", out var kindElement)
            ? kindElement.GetString()
            : null;

        if (kind == "popupTrace")
        {
            var popupTraceId = body.TryGetProperty("lookupTraceId", out var popupTraceElement)
                ? popupTraceElement.GetString()
                : _currentTraceId;
            var popupStage = body.TryGetProperty("stage", out var popupStageElement)
                ? popupStageElement.GetString()
                : "-";
            var popupClientNow = body.TryGetProperty("now", out var popupNowElement)
                && popupNowElement.ValueKind == JsonValueKind.Number
                ? popupNowElement.GetDouble()
                : 0;
            var popupDetails = body.TryGetProperty("details", out var popupDetailsElement)
                ? popupDetailsElement.GetRawText()
                : "{}";

            Log.Information(
                "[LookupTrace] trace={TraceId} popup-js stage={Stage} clientNow={ClientNow:F1} details={Details}",
                string.IsNullOrWhiteSpace(popupTraceId) ? _currentTraceId ?? "-" : popupTraceId,
                popupStage ?? "-",
                popupClientNow,
                popupDetails);
            return;
        }

        if (kind != "audioTrace")
        {
            Log.Information("[DictPopup] Diagnostic: {Payload}", payload.GetRawText());
            return;
        }

        var traceId = body.TryGetProperty("lookupTraceId", out var traceElement)
            ? traceElement.GetString()
            : _currentTraceId;
        var audioTraceId = body.TryGetProperty("audioTraceId", out var audioTraceElement)
            ? audioTraceElement.GetString()
            : "-";
        var stage = body.TryGetProperty("stage", out var stageElement)
            ? stageElement.GetString()
            : "-";
        var clientNow = body.TryGetProperty("now", out var nowElement)
            && nowElement.ValueKind == JsonValueKind.Number
            ? nowElement.GetDouble()
            : 0;
        var details = body.TryGetProperty("details", out var detailsElement)
            ? detailsElement.GetRawText()
            : "{}";

        Log.Information(
            "[AudioTrace] lookup={TraceId} audio={AudioTraceId} popup-js stage={Stage} clientNow={ClientNow:F1} details={Details}",
            string.IsNullOrWhiteSpace(traceId) ? _currentTraceId ?? "-" : traceId,
            string.IsNullOrWhiteSpace(audioTraceId) ? "-" : audioTraceId,
            stage ?? "-",
            clientNow,
            details);
    }

    private long PrepareForPendingContent(
        CancellationToken cancellationToken,
        string? traceId)
    {
        var generation = ++_displayGeneration;
        if (!_displayTransaction.TryBeginPending(
                generation,
                traceId,
                out var preserveCommittedContent))
        {
            throw new InvalidOperationException(
                $"Popup generation {generation} could not acquire pending ownership.");
        }

        _pendingContentGeneration = generation;
        _pendingContentCancellationToken = cancellationToken;
        _pendingContentStopwatch = null;

        if (!preserveCommittedContent)
        {
            CancelOpacityAnimation();
            VisualRoot.Visibility = Visibility.Visible;
            VisualRoot.Opacity = 0;
            VisualRoot.IsHitTestVisible = false;
        }

        return generation;
    }

    private void CancelPendingContentBeforeStartingNextGeneration()
    {
        if (!_displayTransaction.TryGetPending(out var pending))
            return;

        if (!CancelPendingContent(pending.Generation, pending.TraceId))
        {
            throw new InvalidOperationException(
                $"Popup generation {pending.Generation} retained unexpected pending ownership.");
        }
    }

    private static async Task WaitForShellReadyAsync(
        long documentEpoch,
        Task readyTask,
        DictionaryPopupWarmLease lease)
    {
        var completed = await Task.WhenAny(
            readyTask,
            Task.Delay(TimeSpan.FromSeconds(3), lease.CancellationToken));
        lease.ThrowIfInvalid();
        if (!ReferenceEquals(completed, readyTask))
            throw new TimeoutException($"Dictionary popup shellReady timed out for epoch {documentEpoch}.");

        await readyTask;
        lease.ThrowIfInvalid();
    }

    private void ShowReadyContent()
    {
        var shouldAnimateEntrance = !VisualRoot.IsHitTestVisible || VisualRoot.Opacity <= 0;
        _pendingContentGeneration = null;
        _pendingContentCancellationToken = default;
        _pendingContentStopwatch = null;
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.IsHitTestVisible = true;
        if (!shouldAnimateEntrance)
        {
            CancelOpacityAnimation();
            VisualRoot.Opacity = _readyOpacity;
            return;
        }

        VisualRoot.Opacity = 0;
        _ = AnimateOpacityAsync(_readyOpacity, PopupEntranceFadeDuration);
    }

    private Task<bool> AnimateOpacityAsync(double targetOpacity, TimeSpan duration)
    {
        if (!_animateOpacityTransitions)
            duration = TimeSpan.Zero;

        var fromOpacity = VisualRoot.Opacity;
        CancelOpacityAnimation();
        VisualRoot.Opacity = fromOpacity;
        var animationGeneration = ++_opacityAnimationGeneration;
        if (Math.Abs(fromOpacity - targetOpacity) < 0.001 || duration <= TimeSpan.Zero)
        {
            VisualRoot.Opacity = targetOpacity;
            return Task.FromResult(true);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = fromOpacity,
            To = targetOpacity,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, VisualRoot);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        EventHandler<object>? completedHandler = null;
        completedHandler = (_, _) =>
        {
            storyboard.Completed -= completedHandler;
            if (animationGeneration != _opacityAnimationGeneration)
            {
                completion.TrySetResult(false);
                return;
            }

            storyboard.Stop();
            VisualRoot.Opacity = targetOpacity;
            _opacityStoryboard = null;
            _opacityStoryboardCompletedHandler = null;
            _opacityAnimationCompletion = null;
            completion.TrySetResult(true);
        };
        storyboard.Completed += completedHandler;
        _opacityStoryboard = storyboard;
        _opacityStoryboardCompletedHandler = completedHandler;
        _opacityAnimationCompletion = completion;
        storyboard.Begin();
        return completion.Task;
    }

    private void CancelOpacityAnimation()
    {
        _opacityAnimationGeneration++;
        if (_opacityStoryboard is { } storyboard)
        {
            if (_opacityStoryboardCompletedHandler is { } completedHandler)
                storyboard.Completed -= completedHandler;
            storyboard.Stop();
        }

        _opacityStoryboard = null;
        _opacityStoryboardCompletedHandler = null;
        _opacityAnimationCompletion?.TrySetResult(false);
        _opacityAnimationCompletion = null;
    }

    private Task ObservePopupCommitAsync(long generation)
    {
        var documentEpoch = _stagedNativeContext?.Generation == generation
            ? _stagedNativeContext.DocumentEpoch
            : _rendererEpoch;
        return DictionaryPopupCommitCoordinator.ObserveAsync(
            generation,
            async () =>
            {
                var core = _contentWebView.CoreWebView2
                    ?? throw new InvalidOperationException("Dictionary popup WebView2 is unavailable.");
                var raw = await core.ExecuteScriptAsync(
                    $"window.niratanCommitPopupRender?.({documentEpoch}, {generation}) ?? false;");
                return JsonSerializer.Deserialize<bool>(raw);
            },
            async () =>
            {
                var core = _contentWebView.CoreWebView2
                    ?? throw new InvalidOperationException("Dictionary popup WebView2 is unavailable.");
                var raw = await core.ExecuteScriptAsync(
                    $"window.niratanGetCommittedPopupGeneration?.({documentEpoch}) ?? null;");
                return JsonSerializer.Deserialize<long?>(raw);
            },
            async () =>
            {
                var core = _contentWebView.CoreWebView2
                    ?? throw new InvalidOperationException("Dictionary popup WebView2 is unavailable.");
                await core.ExecuteScriptAsync(
                    $"window.niratanDiscardPopupRender?.({documentEpoch}, {generation}) ?? false;");
            },
            resolution => EnqueueCommitResolution(generation, documentEpoch, resolution),
            PopupCommitTimeout);
    }

    private void EnqueueCommitResolution(
        long generation,
        long documentEpoch,
        DictionaryPopupCommitResolution resolution)
    {
        if (_contentWebView.DispatcherQueue.TryEnqueue(() =>
            ResolveCommitResolution(generation, documentEpoch, resolution)))
        {
            return;
        }

        // The UI dispatcher is gone. Preserve exact native ownership and the
        // latest queued request so a future host/recovery attempt can retry.
        _warmCoordinator.Reset();
    }

    private void ResolveCommitResolution(
        long generation,
        long documentEpoch,
        DictionaryPopupCommitResolution resolution)
    {
        if (resolution is DictionaryPopupCommitResolution.Committed
            or DictionaryPopupCommitResolution.ReconciledCommitted)
        {
            CompleteAcceptedCommit(generation, documentEpoch);
            return;
        }

        if (resolution == DictionaryPopupCommitResolution.RendererUnavailable)
        {
            StartForcedShellRecovery(generation, documentEpoch);
            return;
        }
        AbortAcceptedCommit(generation, documentEpoch);
    }

    private void StartForcedShellRecovery(long generation, long failedEpoch)
    {
        if (_displayTransaction.CommitInFlightGeneration != generation
            || _stagedNativeContext is not { } context
            || context.Generation != generation
            || context.DocumentEpoch != failedEpoch
            || !_recoveryCoordinator.TryStartAttempt(
                generation,
                failedEpoch,
                out var ticket))
        {
            return;
        }

        _activeRecoveryTicket = ticket;
        _warmCoordinator.Reset();
        _ = RecoverAcceptedCommitAsync(ticket, context);
    }

    private void RetryForcedShellRecoveryIfNeeded()
    {
        if (_displayTransaction.CommitInFlightGeneration is not long generation
            || _stagedNativeContext is not { } context
            || context.Generation != generation
            || !_recoveryCoordinator.IsRecovering(
                generation,
                context.DocumentEpoch))
        {
            return;
        }

        StartForcedShellRecovery(generation, context.DocumentEpoch);
    }

    private async Task RecoverAcceptedCommitAsync(
        DictionaryPopupRecoveryTicket ticket,
        DictionaryPopupNativeContext context)
    {
        try
        {
            await WarmAsync(
                context.ThemeMode,
                context.AudioSettings,
                context.AnkiSettings,
                context.TraceId);
            var freshEpoch = _rendererEpoch;
            if (!_contentWebView.DispatcherQueue.TryEnqueue(() =>
                CompleteForcedShellRecovery(ticket, freshEpoch)))
            {
                FailForcedShellRecovery(ticket, null);
            }
        }
        catch (Exception ex)
        {
            if (!_contentWebView.DispatcherQueue.TryEnqueue(() =>
                FailForcedShellRecovery(ticket, ex)))
            {
                FailForcedShellRecovery(ticket, ex);
            }
        }
    }

    private void CompleteForcedShellRecovery(
        DictionaryPopupRecoveryTicket ticket,
        long freshEpoch)
    {
        if (_activeRecoveryTicket != ticket
            || _displayTransaction.CommitInFlightGeneration != ticket.Generation
            || _stagedNativeContext is not { } context
            || context.Generation != ticket.Generation
            || context.DocumentEpoch != ticket.FailedEpoch
            || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, freshEpoch)
            || !_recoveryCoordinator.TryComplete(ticket, freshEpoch))
        {
            return;
        }

        _activeRecoveryTicket = null;
        AbortAcceptedCommit(ticket.Generation, ticket.FailedEpoch);
    }

    private void FailForcedShellRecovery(
        DictionaryPopupRecoveryTicket ticket,
        Exception? exception)
    {
        if (_activeRecoveryTicket != ticket)
            return;

        _recoveryCoordinator.FailAttempt(ticket);
        _activeRecoveryTicket = null;
        if (exception is not null)
        {
            Log.Warning(
                exception,
                "[DictPopup] Forced shell recovery failed gen={Gen} epoch={Epoch} attempt={Attempt}; ownership retained",
                ticket.Generation,
                ticket.FailedEpoch,
                ticket.Attempt);
        }
    }

    private void CompleteAcceptedCommit(long generation, long documentEpoch)
    {
        if (_displayTransaction.CommittedGeneration == generation)
            return;
        if (_displayTransaction.CommitInFlightGeneration != generation
            || _stagedNativeContext is not { } context
            || context.Generation != generation
            || context.DocumentEpoch != documentEpoch
            || !_recoveryCoordinator.CanCompleteAccepted(generation, documentEpoch)
            || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, documentEpoch))
        {
            return;
        }

        _isCompletingContentReady = true;
        try
        {
            if (!_displayTransaction.TryCompleteCommit(generation, out var commit))
                return;

            PromoteNativeContext(context);
            ShowReadyContent();
            ContentReady?.Invoke(this, EventArgs.Empty);
            if (_displayTransaction.CommittedGeneration != generation)
                return;
            if (_displayTransaction.PendingGeneration is not null
                || _displayGeneration != generation)
                return;
            ContentCommitted?.Invoke(
                this,
                new DictionaryPopupContentCommittedEventArgs(
                    commit.Generation,
                    commit.TraceId));
        }
        finally
        {
            _isCompletingContentReady = false;
            _ = ProcessQueuedShowAsync();
        }
    }

    private void AbortAcceptedCommit(long generation, long documentEpoch)
    {
        if (_stagedNativeContext is not { } context
            || context.Generation != generation
            || context.DocumentEpoch != documentEpoch)
            return;
        if (!_displayTransaction.TryAbortCommit(generation))
            return;

        ClearPendingGeneration(generation);
        ContentCommitAborted?.Invoke(
            this,
            new DictionaryPopupContentCommittedEventArgs(
                generation,
                context.TraceId));
        if (!_displayTransaction.HasCommittedContent)
        {
            VisualRoot.Opacity = 0;
            VisualRoot.IsHitTestVisible = false;
        }
        _ = ProcessQueuedShowAsync();
    }

    private void ClearPendingGeneration(long generation)
    {
        if (_pendingContentGeneration == generation)
        {
            _pendingContentGeneration = null;
            _pendingContentCancellationToken = default;
            _pendingContentStopwatch = null;
        }
        if (_stagedNativeContext?.Generation == generation)
            _stagedNativeContext = null;
    }

    private void PromoteNativeContext(DictionaryPopupNativeContext context)
    {
        ApplySurfaceTheme(context.ThemeMode);
        _currentTraceId = context.TraceId;
        _navigationStateCoordinator.CommitRoot(context.DocumentEpoch, context.Generation);
        UpdateActionBarVisibility(context.DisplaySettings);
        ResetActionBarNavigationState();
        _audioSettings = context.AudioSettings;
        _ankiSettings = context.AnkiSettings;
        _miningContext = context.MiningContext;
        _sasayakiPopupControls = context.SasayakiControls;
        _openableAnkiNotes.Clear();
        _stagedNativeContext = null;
        UpdateSasayakiPopupControls();
    }

    private async Task ProcessQueuedShowAsync()
    {
        if (!_queuedShowRequests.TryTake(out var request))
            return;
        var queuedRequest = request!;

        if (queuedRequest.CancellationToken.IsCancellationRequested)
        {
            NotifyQueuedShowDropped(queuedRequest);
            return;
        }

        try
        {
            await ShowResultsWarmCoreAsync(queuedRequest);
        }
        catch (OperationCanceledException) when (queuedRequest.CancellationToken.IsCancellationRequested)
        {
            NotifyQueuedShowDropped(queuedRequest);
            Log.Debug(
                "[LookupTrace] trace={TraceId} queued popup show cancelled",
                queuedRequest.TraceId ?? "-");
        }
        catch (Exception ex)
        {
            NotifyQueuedShowDropped(queuedRequest);
            Log.Warning(
                ex,
                "[LookupTrace] trace={TraceId} queued popup show failed",
                queuedRequest.TraceId ?? "-");
        }
    }

    private void QueueShowRequest(DictionaryPopupShowRequest request)
    {
        NotifyQueuedShowDropped(_queuedShowRequests.Replace(request));
    }

    private void NotifyQueuedShowDropped(DictionaryPopupShowRequest? request)
    {
        if (request is null || !request.State.TryDropBeforeGeneration())
            return;

        QueuedShowDropped?.Invoke(
            this,
            new DictionaryPopupShowDroppedEventArgs(request.TraceId));
    }

    private bool IsCurrentContentReady(JsonElement payload)
    {
        if (_pendingContentGeneration is not long expected)
            return false;
        if (_pendingContentCancellationToken.IsCancellationRequested)
            return false;

        return TryGetContentGeneration(payload, out var generation)
            && generation == expected;
    }

    private static bool TryGetContentGeneration(JsonElement payload, out long generation)
    {
        generation = default;
        return payload.TryGetProperty("body", out var body)
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("generation", out var generationElement)
            && generationElement.TryGetInt64(out generation);
    }

    private static bool TryGetContentEpoch(JsonElement payload, out long epoch)
    {
        epoch = default;
        return payload.TryGetProperty("body", out var body)
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("epoch", out var epochElement)
            && epochElement.TryGetInt64(out epoch);
    }

    private bool CanShowReadyContent(long generation) =>
        _pendingContentGeneration == generation
        && !_pendingContentCancellationToken.IsCancellationRequested;

    private void UpdateNavigationState(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body)
            || body.ValueKind != JsonValueKind.Object
            || !TryGetContentEpoch(payload, out var epoch)
            || !body.TryGetProperty("generation", out var generationElement)
            || !generationElement.TryGetInt64(out var generation)
            || _displayTransaction.CommittedGeneration != generation
            || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, epoch)
            || !body.TryGetProperty("canGoBack", out var canGoBackElement)
            || canGoBackElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !body.TryGetProperty("canGoForward", out var canGoForwardElement)
            || canGoForwardElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        var canGoBack = canGoBackElement.GetBoolean();
        var canGoForward = canGoForwardElement.GetBoolean();
        if (!_navigationStateCoordinator.TryUpdate(
                epoch,
                generation,
                canGoBack,
                canGoForward))
        {
            return;
        }

        _contentWebView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_displayTransaction.CommittedGeneration != generation
                || !DictionaryPopupDocumentEpoch.Matches(_rendererEpoch, epoch)
                || !_navigationStateCoordinator.IsCurrent(epoch, generation))
            {
                return;
            }

            _popupBackButton.IsEnabled = canGoBack;
            _popupForwardButton.IsEnabled = canGoForward;
            UpdateActionBarVisibility();
        });
    }

    private static DictionaryPopupRedirectRequest ParseRedirectRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body))
            return new DictionaryPopupRedirectRequest("");

        if (body.ValueKind == JsonValueKind.String)
            return new DictionaryPopupRedirectRequest(body.GetString() ?? "");

        if (body.ValueKind != JsonValueKind.Object)
            return new DictionaryPopupRedirectRequest("");

        var query = body.TryGetProperty("query", out var queryElement)
            ? queryElement.GetString() ?? ""
            : "";
        if (!body.TryGetProperty("rect", out var rect) || rect.ValueKind != JsonValueKind.Object)
            return new DictionaryPopupRedirectRequest(
                query,
                Source: TryGetString(body, "source"),
                ClientNow: TryGetDouble(body, "clientNow"),
                SelectMs: TryGetDouble(body, "selectMs"),
                RectMs: TryGetDouble(body, "rectMs"),
                SelectedLength: TryGetInt(body, "selectedLength"));

        return new DictionaryPopupRedirectRequest(
            query,
            TryGetDouble(rect, "x"),
            TryGetDouble(rect, "y"),
            TryGetDouble(rect, "width"),
            TryGetDouble(rect, "height"),
            TryGetString(body, "source"),
            TryGetDouble(body, "clientNow"),
            TryGetDouble(body, "selectMs"),
            TryGetDouble(body, "rectMs"),
            TryGetInt(body, "selectedLength"));
    }

    private static double? TryGetDouble(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private void HandlePlayWordAudio(
        string url,
        AudioPlaybackMode mode,
        string? traceId = null,
        string? audioTraceId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Debug("[DictPopup] playWordAudio: empty URL");
                return;
            }

            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} native message received url='{Url}' mode={Mode}",
                traceId ?? "-", audioTraceId ?? "-", url, mode);
            Log.Information("[AudioPlay] user-click: url='{Url}' mode={Mode}", url, mode);

            _ = ResolveAndPlayAsync(url, mode, traceId, audioTraceId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictPopup] HandlePlayWordAudio sync crash for '{Url}'", url);
        }
    }

    private async Task ResolveAndPlayAsync(
        string url,
        AudioPlaybackMode mode,
        string? traceId = null,
        string? audioTraceId = null)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var resolvedUrl = await TryResolveAudioUrlAsync(url, traceId, audioTraceId) ?? url;
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} url resolve completed in {Ms}ms input='{InputUrl}' resolved='{ResolvedUrl}'",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, resolvedUrl);
            Log.Information("[DictPopup] Audio resolve took {Ms}ms", sw.ElapsedMilliseconds);

            var playSw = Stopwatch.StartNew();
            await _audioService.PlayAsync(resolvedUrl, mode, traceId, audioTraceId);
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} audio service returned in {Ms}ms total={TotalMs}ms",
                traceId ?? "-", audioTraceId ?? "-", playSw.ElapsedMilliseconds, sw.ElapsedMilliseconds);
            Log.Information("[DictPopup] Audio play took {Ms}ms (total {Total}ms)",
                playSw.ElapsedMilliseconds, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictPopup] ResolveAndPlayAsync failed for '{Url}'", url);
        }
    }

    private void CancelPrefetch()
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;
    }

    private void CancelDeferredResults()
    {
        _deferredResultsCts?.Cancel();
        _deferredResultsCts?.Dispose();
        _deferredResultsCts = null;
    }

    /// <summary>
    /// Prefetch audio URL resolution for the primary entry only (first result).
    /// Never blocks the UI thread — runs on a background task with cancellation.
    /// </summary>
    private void PrefetchAudioUrls(
        List<DictionaryLookupResult> results,
        AudioSettings audioSettings,
        string? traceId)
    {
        var sources = audioSettings.EnabledAudioSourceUrls;
        if (sources.Count == 0) return;
        if (results.Count == 0) return;

        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;

        // Only prefetch the first result (primary visible entry), first source
        var first = results[0];
        var expression = first.Term.Expression;
        var reading = first.Term.Reading;

        _ = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var resolverUrl = ExpandAudioTemplate(sources[0], expression, reading);
                if (!IsAllowedAudioResolverUrl(resolverUrl, audioSettings))
                    return;
                await TryResolveAudioUrlAsync(resolverUrl, traceId ?? "prefetch", "prefetch");
                Log.Information("[AudioPrefetch] auto: '{Expr}'/'{Reading}', entries={Count}",
                    expression, reading, results.Count);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("[AudioPrefetch] cancelled for gen change");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[AudioPrefetch] failed for '{Expr}'/'{Reading}'",
                    expression, reading);
            }
        }, ct);
    }

    private static string ExpandAudioTemplate(string template, string expression, string reading)
    {
        return template
            .Replace("{term}", Uri.EscapeDataString(expression))
            .Replace("{reading}", Uri.EscapeDataString(reading));
    }

    /// <summary>
    /// Returns a task that resolves <paramref name="url"/> to a playable MP3 URL.
    /// Only one HTTP request per URL is ever in-flight — concurrent callers await the same task.
    /// </summary>
    private static Task<string?> TryResolveAudioUrlAsync(
        string url,
        string? traceId = null,
        string? audioTraceId = null)
    {
        url = NormalizeAudioSourceUrl(url);
        if (s_resolvedAudioUrls.TryGetValue(url, out var cached))
        {
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} url resolve cache hit input='{InputUrl}' resolved='{ResolvedUrl}'",
                traceId ?? "-", audioTraceId ?? "-", url, cached);
            return Task.FromResult<string?>(cached);
        }

        var operation = s_audioResolutionTasks.GetOrAdd(
            url,
            key => new Lazy<Task<string?>>(() => ResolveAndCacheUrlAsync(key, traceId, audioTraceId)));
        return AwaitAudioResolutionAsync(url, operation, traceId, audioTraceId);
    }

    private static async Task<string?> AwaitAudioResolutionAsync(
        string url,
        Lazy<Task<string?>> operation,
        string? traceId,
        string? audioTraceId)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await operation.Value;
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} url resolve task awaited in {Ms}ms input='{InputUrl}' hit={Hit}",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, result != null);
            return result;
        }
        finally
        {
            if (s_audioResolutionTasks.TryGetValue(url, out var current) && ReferenceEquals(current, operation))
                s_audioResolutionTasks.TryRemove(url, out _);
        }
    }

    private static async Task<string?> ResolveAndCacheUrlAsync(
        string url,
        string? traceId,
        string? audioTraceId)
    {
        var result = await ResolveAudioUrlCoreAsync(url, traceId, audioTraceId);
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (s_resolvedAudioUrls.Count >= MaxResolvedAudioUrlCacheEntries)
                s_resolvedAudioUrls.Clear();

            s_resolvedAudioUrls[url] = result;
        }
        return result;
    }

    private static async Task<string?> ResolveAudioUrlCoreAsync(
        string url,
        string? traceId,
        string? audioTraceId)
    {
        var sw = Stopwatch.StartNew();
        if (LocalAudioSourceListResolver.IsLocalAudioSourceListUrl(url))
        {
            var localResult = await s_localAudioSourceListResolver.ResolveAsync(url);
            if (localResult == null)
            {
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} local audioSourceList resolve completed in {Ms}ms hit=false input='{InputUrl}'",
                    traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url);
                return null;
            }

            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} local audioSourceList resolve completed in {Ms}ms hit=true input='{InputUrl}' resolved='{ResolvedUrl}'",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, localResult.AudioUrl);
            return localResult.AudioUrl;
        }

        try
        {
            using var response = await s_audioResolveHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} remote audioSourceList headers completed in {Ms}ms status={StatusCode} input='{InputUrl}'",
                    traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, (int)response.StatusCode, url);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase);
            if (!isJson)
            {
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} remote audio direct content-type='{ContentType}' in {Ms}ms input='{InputUrl}'",
                    traceId ?? "-", audioTraceId ?? "-", contentType, sw.ElapsedMilliseconds, url);
                return url;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body) || body[0] != '{')
            {
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} remote audio text response read in {Ms}ms input='{InputUrl}' json=false bytes={Bytes}",
                    traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, body?.Length ?? 0);
                return url;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() != "audioSourceList"
                || !root.TryGetProperty("audioSources", out var sources)
                || sources.GetArrayLength() == 0)
            {
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} remote audioSourceList empty in {Ms}ms input='{InputUrl}' bytes={Bytes}",
                    traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, body.Length);
                return null;
            }

            var first = sources[0];
            if (first.TryGetProperty("url", out var urlEl))
            {
                var resolved = NormalizeAudioSourceUrl(urlEl.GetString() ?? url);
                if (!Uri.TryCreate(resolved, UriKind.Absolute, out _)
                    && response.RequestMessage?.RequestUri is Uri baseUri
                    && Uri.TryCreate(baseUri, resolved, out var relative))
                {
                    resolved = relative.ToString();
                }

                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} remote audioSourceList resolved in {Ms}ms input='{InputUrl}' resolved='{ResolvedUrl}'",
                    traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url, resolved);
                return resolved;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictPopup] ResolveAudioUrlCore: resolution failed");
        }

        return null;
    }

    private static string NormalizeAudioSourceUrl(string url)
    {
        return AudioSourceUrlNormalizer.Normalize(url);
    }

    private void HandleMineEntry(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return;

        var rawPayload = body.GetRawText();
        var miningPayload = AnkiMiningPayload.FromJson(rawPayload);
        var entryIndex = miningPayload.EntryIndex;
        var renderGeneration = miningPayload.RenderGeneration;
        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                Log.Information("[Lifecycle] Anki mine started");
                var result = await MineEntryCoreAsync(rawPayload, _miningContext);
                ShowMiningToast(result);
                await SendMiningResultToWebAsync(
                    "onMineComplete", entryIndex, renderGeneration, result);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DictPopup] MineEntry failed");
                var result = AnkiMiningResult.Failed(ex.Message);
                ShowMiningToast(result);
                await SendMiningResultToWebAsync(
                    "onMineComplete", entryIndex, renderGeneration, result);
            }
        });
    }

    private void HandlePrepareContextMining(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return;

        var rawPayload = body.GetRawText();
        var miningPayload = AnkiMiningPayload.FromJson(rawPayload);
        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var baseMiningContext = ResolveMiningContext(miningPayload.RenderGeneration);
                if (baseMiningContext == null)
                {
                    await NotifyContextMiningPreparedAsync(miningPayload.EntryIndex);
                    return;
                }

                await NotifyContextMiningPreparedAsync(miningPayload.EntryIndex);
                var selection = baseMiningContext.ContextSelection;
                var xamlRoot = VisualRoot.XamlRoot;
                if (selection == null || xamlRoot == null)
                {
                    var unavailable = AnkiMiningResult.Failed("Sentence context is unavailable.");
                    ShowMiningToast(unavailable);
                    return;
                }

                if (_contextMiningDialog != null)
                    return;

                var dialog = new MiningContextSelectionDialog(
                    selection,
                    miningPayload.Matched.Length,
                    async range =>
                    {
                        var selectedContext = MiningContextSelectionResolver.Apply(
                            baseMiningContext,
                            selection,
                            range);
                        var result = await MineEntryCoreAsync(rawPayload, selectedContext);
                        ShowMiningToast(result);
                        await SendMiningResultToWebAsync(
                            "onContextMineComplete",
                            miningPayload.EntryIndex,
                            miningPayload.RenderGeneration,
                            result);
                        return result;
                    })
                {
                    XamlRoot = xamlRoot,
                };
                _contextMiningDialog = dialog;
                var dialogHost = _inPlaceDialogHost;
                try
                {
                    if (dialogHost != null)
                    {
                        dialogHost.Children.Add(dialog);
                        await dialog.ShowAsync(ContentDialogPlacement.InPlace);
                    }
                    else
                    {
                        await dialog.ShowAsync(ContentDialogPlacement.Popup);
                    }
                }
                finally
                {
                    if (dialogHost?.Children.Contains(dialog) == true)
                        dialogHost.Children.Remove(dialog);
                    _contextMiningDialog = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DictPopup] Context mining dialog failed to open");
                ShowMiningToast(AnkiMiningResult.Failed("Unable to open sentence context."));
                await NotifyContextMiningPreparedAsync(miningPayload.EntryIndex);
            }
        });
    }

    private AnkiMiningContext? ResolveMiningContext(long renderGeneration)
    {
        if (renderGeneration < 0 || CommittedGeneration == renderGeneration)
            return _miningContext;

        return _stagedNativeContext is { } staged
            && staged.Generation == renderGeneration
                ? staged.MiningContext
                : null;
    }

    private Task NotifyContextMiningPreparedAsync(int entryIndex) =>
        _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"if (typeof window.onContextMiningPrepared === 'function') window.onContextMiningPrepared({entryIndex});")
            .AsTask();

    private async Task<AnkiMiningResult> MineEntryCoreAsync(
        string rawPayload,
        AnkiMiningContext miningContext)
    {
        try
        {
            var preflight = await _ankiService.PreflightMiningAsync(rawPayload, miningContext);
            if (!preflight.CanMine)
            {
                return preflight.IsDuplicate
                    ? AnkiMiningResult.Duplicate()
                    : AnkiMiningResult.Failed(preflight.ErrorMessage ?? "Failed to add card.");
            }

            var videoMediaError = await RequestVideoMiningMediaAsync(preflight, miningContext);
            if (!string.IsNullOrWhiteSpace(videoMediaError))
                return AnkiMiningResult.Failed(videoMediaError);

            var sasayakiMediaError = await RequestSasayakiMiningMediaAsync(preflight, miningContext);
            if (!string.IsNullOrWhiteSpace(sasayakiMediaError))
                return AnkiMiningResult.Failed(sasayakiMediaError);

            var noteId = await _ankiService.MineEntryAsync(rawPayload, miningContext);
            return noteId is long addedNoteId && addedNoteId > 0
                ? AnkiMiningResult.Added(addedNoteId)
                : AnkiMiningResult.Failed("Failed to add card.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictPopup] Anki mining pipeline failed");
            return AnkiMiningResult.Failed(ex.Message);
        }
    }

    private async Task<string?> RequestVideoMiningMediaAsync(
        AnkiMiningPreflightResult preflight,
        AnkiMiningContext miningContext)
    {
        if (!preflight.MediaNeeds.NeedsVideoMedia || miningContext.VideoMediaProvider == null)
            return null;

        var cueStart = TimeSpan.TryParse(miningContext.VideoCueStart, out var parsedStart)
            ? parsedStart
            : (TimeSpan?)null;
        var cueEnd = TimeSpan.TryParse(miningContext.VideoCueEnd, out var parsedEnd)
            ? parsedEnd
            : (TimeSpan?)null;

        var result = await miningContext.VideoMediaProvider(
            new VideoMiningMediaRequest(
                preflight.MediaNeeds.NeedsVideoScreenshot,
                preflight.MediaNeeds.NeedsVideoAudioClip,
                preflight.DirectMediaDirectory,
                cueStart,
                cueEnd),
            CancellationToken.None);

        miningContext.VideoScreenshotPath = result.ScreenshotPath;
        miningContext.VideoAudioClipPath = result.AudioClipPath;
        miningContext.VideoScreenshotTag = result.ScreenshotTag;
        miningContext.VideoAudioClipTag = result.AudioClipTag;
        if (preflight.MediaNeeds.NeedsVideoScreenshot
            && string.IsNullOrWhiteSpace(result.ScreenshotPath)
            && string.IsNullOrWhiteSpace(result.ScreenshotTag))
        {
            return result.ScreenshotErrorMessage ?? "Unable to capture the video screenshot.";
        }

        if (preflight.MediaNeeds.NeedsVideoAudioClip
            && string.IsNullOrWhiteSpace(result.AudioClipPath)
            && string.IsNullOrWhiteSpace(result.AudioClipTag))
        {
            return result.AudioClipErrorMessage ?? "Unable to capture the subtitle audio clip.";
        }

        return null;
    }

    private async Task<string?> RequestSasayakiMiningMediaAsync(
        AnkiMiningPreflightResult preflight,
        AnkiMiningContext miningContext)
    {
        if (!preflight.MediaNeeds.NeedsSasayakiAudio || miningContext.SasayakiAudioProvider == null)
            return null;

        var result = await miningContext.SasayakiAudioProvider(
            new SasayakiMiningAudioRequest(
                preflight.MediaNeeds.NeedsSasayakiAudio,
                preflight.DirectMediaDirectory,
                miningContext.Sentence),
            CancellationToken.None);

        miningContext.SasayakiAudioPath = result.AudioClipPath;
        miningContext.SasayakiAudioTag = result.AudioClipTag;
        return result.ErrorMessage;
    }

    private void HandleOpenAnkiNote(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body)
            || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("entryIndex", out var entryIndexElement)
            || !entryIndexElement.TryGetInt32(out var entryIndex)
            || entryIndex < 0
            || !body.TryGetProperty("renderGeneration", out var generationElement)
            || !generationElement.TryGetInt64(out var renderGeneration)
            || renderGeneration < 0
            || !body.TryGetProperty("noteID", out var noteIdElement)
            || !noteIdElement.TryGetInt64(out var noteId)
            || noteId <= 0)
        {
            Log.Warning("[DictPopup] Rejected malformed openAnkiNote message");
            return;
        }

        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            var allowed = CommittedGeneration == renderGeneration
                && _openableAnkiNotes.TryGetValue(
                    (renderGeneration, entryIndex),
                    out var allowedNoteId)
                && allowedNoteId == noteId;
            var opened = false;
            if (allowed)
            {
                opened = await _ankiService.OpenNoteInAnkiAsync(noteId);
                if (!opened)
                    ShowMiningToast(AnkiMiningResult.Failed("Unable to open note in Anki."));
            }
            else
            {
                Log.Warning(
                    "[DictPopup] Rejected stale or unrecognized Anki note request gen={Generation} entry={EntryIndex} noteId={NoteId}",
                    renderGeneration,
                    entryIndex,
                    noteId);
            }

            if (CommittedGeneration != renderGeneration)
                return;

            await _contentWebView.CoreWebView2.ExecuteScriptAsync(
                $"if (typeof window.onOpenAnkiNoteComplete === 'function') window.onOpenAnkiNoteComplete({entryIndex}, {(opened ? "true" : "false")});");
        });
    }

    private void HandleDuplicateCheck(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return;

        var expression = body.TryGetProperty("expression", out var expressionElement)
            ? expressionElement.GetString() ?? ""
            : "";
        var entryIndex = body.TryGetProperty("entryIndex", out var entryIndexElement)
            && entryIndexElement.TryGetInt32(out var parsedEntryIndex)
                ? parsedEntryIndex
                : -1;
        var renderGeneration = body.TryGetProperty("renderGeneration", out var generationElement)
            && generationElement.TryGetInt64(out var parsedGeneration)
                ? parsedGeneration
                : -1;
        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var isDuplicate = await _ankiService.DuplicateCheckExpressionAsync(expression);
                if (renderGeneration >= 0
                    && CommittedGeneration != renderGeneration
                    && _displayTransaction.CommitInFlightGeneration != renderGeneration)
                    return;
                var script = $"if (typeof window.onDuplicateCheck === 'function') window.onDuplicateCheck({entryIndex}, {(isDuplicate ? "true" : "false")});";

                await _contentWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DictPopup] DuplicateCheck failed");
            }
        });
    }

    private async Task SendMiningResultToWebAsync(
        string callback,
        int entryIndex,
        long renderGeneration,
        AnkiMiningResult result)
    {
        if (renderGeneration >= 0 && CommittedGeneration != renderGeneration)
            return;

        if (result is { Status: AnkiMiningStatus.Added, NoteId: > 0 })
            _openableAnkiNotes[(renderGeneration, entryIndex)] = result.NoteId.Value;

        var payload = JsonSerializer.Serialize(new
        {
            status = result.WebStatus,
            message = result.Message,
            noteID = result.NoteId,
        });
        await _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"if (typeof window.{callback} === 'function') window.{callback}({entryIndex}, {payload});");
    }

    private void ShowMiningToast(AnkiMiningResult result)
    {
        _miningToastCts?.Cancel();
        _miningToastCts?.Dispose();
        _miningToastCts = new CancellationTokenSource();
        _miningToast.Title = result.Status switch
        {
            AnkiMiningStatus.Added => "Card Added",
            AnkiMiningStatus.Duplicate => "Duplicate Found",
            AnkiMiningStatus.Pending => "Sent to Anki",
            _ => "Add Failed",
        };
        _miningToast.Message = result.Message;
        _miningToast.Severity = result.Status switch
        {
            AnkiMiningStatus.Added => InfoBarSeverity.Success,
            AnkiMiningStatus.Duplicate => InfoBarSeverity.Warning,
            AnkiMiningStatus.Pending => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Error,
        };
        _miningToast.IsOpen = true;
        _ = HideMiningToastAsync(_miningToastCts.Token);
    }

    private async Task HideMiningToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2200), cancellationToken);
            _contentWebView.DispatcherQueue.TryEnqueue(() => _miningToast.IsOpen = false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        CancelOpacityAnimation();
        _miningToastCts?.Cancel();
        _miningToastCts?.Dispose();
        CancelPrefetch();
        CancelDeferredResults();
        if (_contentWebView.CoreWebView2 != null)
        {
            _contentWebView.CoreWebView2.WebMessageReceived -= OnPopupWebMessageReceived;
            _contentWebView.CoreWebView2.WebResourceRequested -= OnPopupWebResourceRequested;
            _contentWebView.CoreWebView2.ProcessFailed -= OnPopupWebViewProcessFailed;
        }
        _contentWebView.Close();
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = WebUtility.UrlDecode(pair[0]);
            if (!string.Equals(key, name, StringComparison.Ordinal))
                continue;

            return pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "";
        }

        return null;
    }

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".heic" => "image/heic",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
}
