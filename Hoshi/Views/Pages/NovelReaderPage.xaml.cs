using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using WinRT.Interop;
using Hoshi.Helpers;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Anki;
using Hoshi.Services.Settings;
using Hoshi.Views.Dialogs;
using Serilog;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Shortcuts;
using Hoshi.Models.Settings;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Anki;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Shortcuts;
using Hoshi.Services.UI;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;
using Hoshi.Views.Controls;
using Hoshi.Views.Dictionary;

namespace Hoshi.Views.Pages;

public sealed partial class NovelReaderPage : Page
{
    private const string NovelBookHostName = "hoshi-novel-book.local";
    private const string ArtifactDirectoryEnvironmentVariable =
        "HOSHI_NOVEL_READER_ARTIFACT_DIR";
    private static readonly TimeSpan ReaderKeyDownCharacterSuppressWindow =
        TimeSpan.FromMilliseconds(250);

    public NovelReaderPageViewModel ViewModel { get; set; }
    public SasayakiViewModel SasayakiPanelViewModel => _sasayakiVM;
    private EpubBook? _epubBook;
    private string _readerJs = "";
    private string _selectionJs = "";
    private string _highlightsJs = "";
    private string _currentReaderCss = "";
    private IReadOnlyList<int> _chapterCharacterCounts = [];
    private IReadOnlyList<int> _chapterStartCharacterCounts = [];
    private ReaderSearchDocument? _searchDocument;
    private double _currentProgress;
    private int _previousChapterIndex = -1;
    private bool _renderAttemptPendingViewport;
    private bool _terminalFailureRecoveryInProgress;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _searchCts;
    private long _searchRequestVersion;
    private readonly ReaderNavigationHistory _navigationHistory = new();
    private string? _pendingProgrammaticFragment;
    private readonly NovelReaderRenderState _renderState = new();
    private DictionaryPopupOverlay? _popupOverlay;
    private readonly SemaphoreSlim _lookupSemaphore = new(1, 1);
    private readonly Dictionary<KeyboardAccelerator, string> _keyboardAcceleratorActionIds = [];
    private readonly IShortcutService _shortcutService;
    private readonly IMessenger _messenger;
    private DispatcherQueueTimer? _statisticsProjectionTimer;
    private long _lookupRequestVersion;
    private bool _readerFocusMode;
    private KeyboardShortcutBinding _lastKeyDownShortcutBinding;
    private DateTimeOffset _lastKeyDownShortcutHandledAt = DateTimeOffset.MinValue;
    private ContentDialog? _activeReaderPanelDialog;

    private enum ReaderSearchPanelStatus
    {
        Prompt,
        Loading,
        NoMatches,
        Failed,
        Hidden,
    }

    // Sasayaki fields
    private readonly DispatcherQueue _dispatcherQueue;
    private SasayakiPlayer? _sasayakiPlayer;
    private readonly SasayakiParser _sasayakiParser = new();
    private readonly SasayakiMatcher _sasayakiMatcher = new();
    private readonly SasayakiCueNavigationController _sasayakiNav = new();
    private SasayakiViewModel _sasayakiVM = new();
    private SasayakiMatchData? _sasayakiMatchData;
    private int _lastHighlightedCue = -1;
    private long _sasayakiHighlightGeneration;
    private double _sasayakiDelay;
    private double _lastSasayakiPlaybackSavePosition = -1;
    private double? _sasayakiStopPlaybackAtSeconds;
    private bool _isRefreshingSasayakiPanel = true;
    private bool _isSasayakiPanelOpen;

    private static ISasayakiSidecarService SasayakiSidecarService =>
        App.GetService<ISasayakiSidecarService>();

    private static SasayakiSettings CurrentSasayakiSettings =>
        App.GetService<ISettingsService>().Current.SasayakiSettings;

    private static NovelStatisticsSettings CurrentStatisticsSettings =>
        App.GetService<ISettingsService>().Current.StatisticsSettings;

    public NovelReaderPage()
    {
        InitializeComponent();
        _isRefreshingSasayakiPanel = false;
        ViewModel = App.GetService<NovelReaderPageViewModel>();
        ViewModel.PropertyChanged += OnReaderViewModelPropertyChanged;
        DataContext = ViewModel;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _shortcutService = App.GetService<IShortcutService>();
        _messenger = App.GetService<IMessenger>();
        _shortcutService.ShortcutsChanged += OnReaderShortcutsChanged;
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(NovelReaderPage_KeyDown), true);
        RegisterReaderKeyboardAccelerators();
        ApplyReaderShortcutLabels();
    }

    private void ApplyReaderShortcutLabels()
    {
        ApplyShortcutLabel(
            NovelReaderBackButton,
            ShortcutTitle(ReaderShortcutActions.Close),
            ShortcutLabel(ReaderShortcutActions.Close));
        ApplyShortcutLabel(
            NovelReaderSearchButton,
            "Search",
            "/");
        ApplyShortcutLabel(
            NovelReaderStatisticsButton,
            ShortcutTitle(ReaderShortcutActions.ToggleStatistics),
            ShortcutLabel(ReaderShortcutActions.ToggleStatistics));
        ApplyShortcutLabel(
            SasayakiPreviousCueMenuItem,
            ShortcutTitle(SasayakiShortcutActions.PreviousCue),
            ShortcutLabel(SasayakiShortcutActions.PreviousCue));
        ApplyShortcutLabel(
            SasayakiPlayPauseMenuItem,
            ShortcutTitle(SasayakiShortcutActions.PlayPause),
            ShortcutLabel(SasayakiShortcutActions.PlayPause));
        ApplyShortcutLabel(
            SasayakiNextCueMenuItem,
            ShortcutTitle(SasayakiShortcutActions.NextCue),
            ShortcutLabel(SasayakiShortcutActions.NextCue));
        ApplyShortcutLabel(
            SasayakiReplayCueMenuItem,
            ShortcutTitle(SasayakiShortcutActions.ReplayCue),
            ShortcutLabel(SasayakiShortcutActions.ReplayCue));
        ApplyShortcutLabel(
            SasayakiJumpCueMenuItem,
            ShortcutTitle(SasayakiShortcutActions.JumpCue),
            ShortcutLabel(SasayakiShortcutActions.JumpCue));
    }

    private string ShortcutLabel(ReaderShortcutAction action)
    {
        var shortcutAction = _shortcutService.Registry.Action(action.Id);
        return shortcutAction == null
            ? action.DefaultShortcut.Label
            : _shortcutService.GetBinding(shortcutAction).Label;
    }

    private static string ShortcutTitle(ReaderShortcutAction action) =>
        ResourceStringHelper.GetString(
            ShortcutResourceKey.ForActionId(action.Id),
            action.Title);

    private static void ApplyShortcutLabel(
        Control control,
        string actionTitle,
        string shortcutLabel)
    {
        var text = $"{actionTitle} ({shortcutLabel})";
        ToolTipService.SetToolTip(control, text);
        AutomationProperties.SetHelpText(control, text);
    }

    private void OnReaderShortcutsChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            RegisterReaderKeyboardAccelerators();
            ApplyReaderShortcutLabels();
            _ = RefreshReaderWebShortcutBindingsAsync();
        });
    }

    private void RegisterReaderKeyboardAccelerators()
    {
        foreach (var accelerator in _keyboardAcceleratorActionIds.Keys)
            accelerator.Invoked -= ReaderKeyboardAccelerator_Invoked;

        KeyboardAccelerators.Clear();
        _keyboardAcceleratorActionIds.Clear();

        foreach (var action in ReaderShortcutActions.All)
            RegisterKeyboardAccelerator(action);

        foreach (var action in SasayakiShortcutActions.All)
            RegisterKeyboardAccelerator(action);
    }

    private void RegisterKeyboardAccelerator(ReaderShortcutAction readerAction)
    {
        var shortcutAction = _shortcutService.Registry.Action(readerAction.Id);
        if (shortcutAction == null)
            return;

        var binding = _shortcutService.GetBinding(shortcutAction);
        if (!ShouldRegisterKeyboardAccelerator(binding)
            || !ShortcutInputMapper.TryGetVirtualKey(binding, out var key, out var modifiers))
        {
            return;
        }

        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += ReaderKeyboardAccelerator_Invoked;
        _keyboardAcceleratorActionIds[accelerator] = readerAction.Id;
        KeyboardAccelerators.Add(accelerator);
    }

    private static bool ShouldRegisterKeyboardAccelerator(KeyboardShortcutBinding binding) =>
        !IsReaderKeyDownFallbackBinding(binding)
        && ShortcutInputMapper.TryGetVirtualKey(binding, out _, out _);

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _messenger.Unregister<AppBackgroundingMessage>(this);
        _messenger.Register<AppBackgroundingMessage>(
            this,
            static (recipient, message) =>
                message.Reply(((NovelReaderPage)recipient).HandleAppLifecycleCheckpointAsync(message)));
        if (e.Parameter is NovelReaderNavigationArgs args)
        {
            await ViewModel.InitializeAsync(args);
            await InitializeReaderAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopStatisticsProjectionTimer();
        _messenger.Unregister<AppBackgroundingMessage>(this);
        _pendingProgrammaticFragment = null;
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
        _reloadCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        var readerSettings = App.GetService<IReaderSettingsService>();
        readerSettings.SettingChanged -= OnReaderSettingChanged;
        App.GetService<ISettingsService>().SettingChanged -= OnAppSettingChanged;
        _shortcutService.ShortcutsChanged -= OnReaderShortcutsChanged;
        ViewModel.PropertyChanged -= OnReaderViewModelPropertyChanged;

        if (NovelWebView.CoreWebView2 != null)
        {
            NovelWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            NovelWebView.CoreWebView2.DOMContentLoaded -= OnDomContentLoaded;
            NovelWebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
            NovelWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            NovelWebView.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
        }

        NovelWebView.SizeChanged -= OnWebViewSizeChanged;

        if (_popupOverlay != null)
            _popupOverlay.Dismissed -= OnPopupOverlayDismissed;
        _popupOverlay?.Dispose();
        _popupOverlay = null;

        _ = SaveSasayakiPlaybackAsync();

        if (_sasayakiPlayer != null)
        {
            _sasayakiPlayer.PositionChanged -= OnSasayakiPositionChanged;
            _sasayakiPlayer.MediaEnded -= OnSasayakiMediaEnded;
            _sasayakiPlayer.MediaFailed -= OnSasayakiMediaFailed;
        }
        _sasayakiPlayer?.Dispose();
        _sasayakiPlayer = null;

        CloseReaderPanels();
        _ = CompleteReaderLifecycleCloseAfterDetachAsync();
    }

    private void EnsureStatisticsProjectionTimer()
    {
        if (_statisticsProjectionTimer != null)
            return;

        _statisticsProjectionTimer = _dispatcherQueue.CreateTimer();
        _statisticsProjectionTimer.Interval = TimeSpan.FromSeconds(1);
        _statisticsProjectionTimer.IsRepeating = true;
        _statisticsProjectionTimer.Tick += StatisticsProjectionTimer_Tick;
    }

    private void UpdateStatisticsProjectionTimer()
    {
        EnsureStatisticsProjectionTimer();
        if (ViewModel.IsStatisticsTracking && !ViewModel.IsStatisticsPaused)
            _statisticsProjectionTimer!.Start();
        else
            _statisticsProjectionTimer!.Stop();
    }

    private void StopStatisticsProjectionTimer()
    {
        if (_statisticsProjectionTimer == null)
            return;

        _statisticsProjectionTimer.Stop();
        _statisticsProjectionTimer.Tick -= StatisticsProjectionTimer_Tick;
        _statisticsProjectionTimer = null;
    }

    private async void StatisticsProjectionTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (!ViewModel.IsStatisticsTracking || ViewModel.IsStatisticsPaused)
        {
            UpdateStatisticsProjectionTimer();
            return;
        }

        if (!ViewModel.CanAcceptReaderPositionMutation)
            return;

        await ViewModel.TickStatisticsAsync();
    }

    private async Task<bool> HandleAppLifecycleCheckpointAsync(
        AppBackgroundingMessage message)
    {
        try
        {
            var settlement = await ViewModel.SettleNavigationForLifecycleAsync();
            if (settlement != null)
            {
                var ownsPendingSettlement = _renderState.OwnsPendingSettlement(
                    settlement.Generation);
                if (!ownsPendingSettlement
                    && !ApplyNavigationSettlement(settlement))
                {
                    await HandleTerminalRenderFailureAsync(
                        "Reader navigation could not be settled for backgrounding.");
                }
                await WaitForTerminalRenderAsync(settlement.Generation);
            }

            if (message.Reason == AppLifecycleCheckpointReason.Closing)
                await ViewModel.PrepareForReaderLifecycleCloseAsync();
            else
                await ViewModel.CheckpointAppBackgroundingAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Reader] Failed lifecycle checkpoint: {Reason}", message.Reason);
            return false;
        }
    }

    private Task WaitForTerminalRenderAsync(long generation) =>
        _renderState.WaitForTerminalAsync(generation);

    private async Task CompleteReaderLifecycleCloseAfterDetachAsync()
    {
        try
        {
            var settlement = await ViewModel.SettleNavigationForLifecycleAsync();
            if (settlement != null)
            {
                var release = _renderState.TryPrepareFailure();
                if (release != null)
                {
                    if (!ViewModel.AcknowledgeNavigationRendered(release.Value.Generation))
                    {
                        Log.Warning(
                            "[Reader] Detached lifecycle settlement {Generation} was already acknowledged",
                            settlement.Generation);
                    }
                    _renderState.CompleteFailure(release.Value);
                }
                else
                    await WaitForTerminalRenderAsync(settlement.Generation);
            }

            await ViewModel.PrepareForReaderLifecycleCloseAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Reader] Failed detached lifecycle close checkpoint");
        }
    }

    private async System.Threading.Tasks.Task InitializeReaderAsync()
    {
        var book = ViewModel.CurrentBook;
        if (book == null)
        {
            Log.Error("[NovelReader] Book data not available");
            App.GetService<INotificationService>()
                .ShowError("Book data not available.", "Novel reader");
            return;
        }

        if (string.IsNullOrEmpty(book.ExtractedPath))
        {
            var fallbackPath = Helpers.AppDataHelper.GetNovelBookPath(book.Id);
            Log.Information(
                "[NovelReader] ExtractedPath missing for '{Title}', using fallback: {Path}",
                book.Title,
                fallbackPath
            );
            book.ExtractedPath = fallbackPath;
        }

        Log.Information(
            "[NovelReader] Initializing reader for '{Title}' (ExtractedPath: {Path})",
            book.Title,
            book.ExtractedPath
        );

        var parser = App.GetService<IEpubParserService>();
        try
        {
            _epubBook = LoadEpubBookForReading(parser, book);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] Failed to load EPUB for '{Title}'", book.Title);
            App.GetService<INotificationService>()
                .ShowError(ex.Message, "Novel reader");
            return;
        }
        _chapterCharacterCounts = CalculateChapterCharacterCounts(_epubBook);
        _chapterStartCharacterCounts = CalculateChapterStartCharacterCounts(_chapterCharacterCounts);
        _searchDocument = null;
        ViewModel.SetChapterCharacterCounts(_chapterCharacterCounts);
        try
        {
            await ViewModel.SaveBookInfoSidecarAsync(
                _epubBook.Chapters,
                _epubBook.ContainerDirectory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[NovelReader] Failed to save bookinfo sidecar");
        }
        await ViewModel.LoadHighlightsAsync();

        Log.Information(
            "[NovelReader] Parsed EPUB: {ChapterCount} chapters, OPF dir: {OpaqueDir}",
            _epubBook.Chapters.Count,
            _epubBook.ContainerDirectory
        );

        if (_epubBook.Chapters.Count == 0)
        {
            Log.Error("[NovelReader] No readable chapters found");
            App.GetService<INotificationService>()
                .ShowError("No readable chapters found in this book.", "Novel reader");
            return;
        }

        for (var i = 0; i < Math.Min(_epubBook.Chapters.Count, 3); i++)
        {
            var ch = _epubBook.Chapters[i];
            Log.Information("[NovelReader] Chapter {Index}: {Href}", i, ch.Href);
        }

        var savedChapterIndex = ViewModel.CurrentBook?.CurrentChapterIndex ?? 0;
        var initialChapterIndex = savedChapterIndex >= 0
            && savedChapterIndex < _epubBook.Chapters.Count
            ? savedChapterIndex
            : 0;
        ViewModel.SetChapter(initialChapterIndex, _epubBook.Chapters.Count);
        ViewModel.UpdateProgress(ViewModel.CurrentBook?.Progress ?? 0);
        await ViewModel.LoadStatisticsAsync();
        UpdateStatisticsProjectionTimer();
        UpdateStatisticsButtonVisibility();
        RefreshReaderDisplayChrome();
        RefreshReaderStatisticsChrome();
        Log.Information(
            "[NovelReader] Restoring position: chapter {Chapter}, progress {Progress:F3}",
            initialChapterIndex,
            ViewModel.Progress);

        var jsPath = Path.Combine(
            AppContext.BaseDirectory,
            "Web",
            "NovelReader",
            "reader-bridge.js"
        );
        Log.Information("[NovelReader] Bridge JS path: {Path}, exists: {Exists}", jsPath, File.Exists(jsPath));
        if (File.Exists(jsPath))
            _readerJs = await File.ReadAllTextAsync(jsPath);
        else
            Log.Error("[NovelReader] Bridge JS file not found at {Path}", jsPath);

        var selPath = Path.Combine(
            AppContext.BaseDirectory,
            "Web",
            "NovelReader",
            "selection.js"
        );
        if (File.Exists(selPath))
            _selectionJs = await File.ReadAllTextAsync(selPath);
        else
            Log.Error("[NovelReader] Selection JS file not found at {Path}", selPath);

        var highlightsPath = Path.Combine(
            AppContext.BaseDirectory,
            "Web",
            "NovelReader",
            "highlights.js"
        );
        if (File.Exists(highlightsPath))
            _highlightsJs = await File.ReadAllTextAsync(highlightsPath);
        else
            Log.Error("[NovelReader] Highlights JS file not found at {Path}", highlightsPath);

        var environment = await WebView2EnvironmentHelper.GetOrCreateAsync();
        await NovelWebView.EnsureCoreWebView2Async(environment);

        NovelWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        NovelWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // Capture JS console messages and runtime errors via DevTools protocol
        try
        {
            NovelWebView.CoreWebView2.GetDevToolsProtocolEventReceiver(
                "Runtime.consoleAPICalled"
            ).DevToolsProtocolEventReceived += (s, a) =>
                Log.Information("[NovelReader] JS console: {Event}", a.ParameterObjectAsJson);

            NovelWebView.CoreWebView2.GetDevToolsProtocolEventReceiver(
                "Runtime.exceptionThrown"
            ).DevToolsProtocolEventReceived += (s, a) =>
                Log.Error("[NovelReader] JS exception: {Event}", a.ParameterObjectAsJson);

            await NovelWebView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Runtime.enable",
                "{}"
            );
            Log.Information("[NovelReader] DevTools Runtime.enable completed");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[NovelReader] Failed to enable DevTools protocol");
        }

        NovelWebView.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
        NovelWebView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;

        NovelWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            NovelBookHostName,
            _epubBook.ContainerDirectory,
            CoreWebView2HostResourceAccessKind.Allow
        );

        NovelWebView.CoreWebView2.AddWebResourceRequestedFilter(
            $"https://{NovelBookHostName}/*",
            CoreWebView2WebResourceContext.All
        );
        NovelWebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

        // Inject reader CSS and JS when the DOM is ready.
        // This is the equivalent of Android's onPageFinished + evaluateJavascript.
        NovelWebView.CoreWebView2.DOMContentLoaded += OnDomContentLoaded;
        Log.Information("[NovelReader] DOMContentLoaded handler registered");

        // Deferred navigation when viewport becomes available
        NovelWebView.SizeChanged += OnWebViewSizeChanged;

        // Navigation events for diagnostics
        NovelWebView.CoreWebView2.NavigationStarting += (s, a) =>
            Log.Information("[NovelReader] Navigation starting: {Uri}", a.Uri);
        NovelWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        NovelWebView.CoreWebView2.FrameNavigationStarting += (s, a) =>
            Log.Information("[NovelReader] Frame navigation: {Uri}", a.Uri);

        var readerSettings = App.GetService<IReaderSettingsService>();
        readerSettings.SettingChanged += OnReaderSettingChanged;
        App.GetService<ISettingsService>().SettingChanged += OnAppSettingChanged;

        _ = EnsurePopupOverlay().PrewarmAsync(XamlRoot, App.GetService<ISettingsService>().Current.Theme);

        UpdateSasayakiChromeVisibility();
        if (CurrentSasayakiSettings.EnableSasayaki)
            _ = LoadSasayakiSidecarAsync();

        Log.Information("[NovelReader] Loading chapter {Index}", initialChapterIndex);
        LoadChapter(initialChapterIndex);
    }

    private static EpubBook LoadEpubBookForReading(IEpubParserService parser, NovelBook book)
    {
        var extractedPath = string.IsNullOrWhiteSpace(book.ExtractedPath)
            ? AppDataHelper.GetNovelBookPath(book.Id)
            : book.ExtractedPath;

        book.ExtractedPath = extractedPath;

        if (HasExtractedEpub(extractedPath))
            return parser.ParseExtracted(extractedPath, book.Title);

        if (!string.IsNullOrWhiteSpace(book.FilePath) && File.Exists(book.FilePath))
            return parser.Parse(book.FilePath, extractedPath);

        throw new FileNotFoundException(
            $"Could not open '{book.Title}'. The imported book data is incomplete, and the original EPUB file was not found.",
            book.FilePath);
    }

    private static bool HasExtractedEpub(string extractedPath) =>
        File.Exists(Path.Combine(extractedPath, "META-INF", "container.xml"));

    private void OnReaderViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.StatisticsSessionSpeedText)
            or nameof(ViewModel.StatisticsSessionTimeText)
            or nameof(ViewModel.IsStatisticsTracking))
        {
            RefreshReaderStatisticsChrome();
        }

        if (e.PropertyName is nameof(ViewModel.IsStatisticsTracking)
            or nameof(ViewModel.IsStatisticsPaused))
        {
            UpdateStatisticsProjectionTimer();
        }

        if (e.PropertyName is nameof(ViewModel.ReaderTitle)
            or nameof(ViewModel.ReaderProgressText))
        {
            RefreshReaderDisplayChrome();
        }
    }

    private async void OnReaderSettingChanged(object? sender, Models.DTO.SettingsChangedEventArgs e)
    {
        _reloadCts?.Cancel();
        _reloadCts = new CancellationTokenSource();
        var token = _reloadCts.Token;

        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
            {
                UpdateStatisticsButtonVisibility();
                RefreshReaderDisplayChrome();
                RefreshReaderStatisticsChrome();
                LoadChapter(ViewModel.CurrentChapterIndex);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignored — newer change arrived and cancelled this one
        }
    }

    private async void ReaderKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_keyboardAcceleratorActionIds.TryGetValue(sender, out var actionId))
            return;

        args.Handled = await HandleReaderShortcutActionAsync(actionId)
            || await HandleSasayakiShortcutActionAsync(actionId);
    }

    private async void NovelReaderPage_CharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs args)
    {
        if (ShouldIgnoreReaderShortcutSource(args.OriginalSource))
            return;

        var binding = KeyboardShortcutBindingFromCharacter(
            args.Character,
            ShortcutInputMapper.GetCurrentModifiers());
        if (binding.IsEmpty || ShouldRegisterKeyboardAccelerator(binding))
            return;

        if (WasRecentlyHandledByReaderKeyDownFallback(binding))
        {
            args.Handled = true;
            return;
        }

        args.Handled = await TryHandleReaderShortcutBindingAsync(binding);
    }

    private async void NovelReaderPage_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || ShouldIgnoreReaderShortcutSource(args.OriginalSource))
            return;

        var binding = KeyboardShortcutBinding.FromVirtualKey(
            args.Key,
            ShortcutInputMapper.GetCurrentModifiers());
        if (!IsReaderKeyDownFallbackBinding(binding))
            return;

        args.Handled = await TryHandleReaderShortcutBindingAsync(binding);
        if (!args.Handled)
            return;

        _lastKeyDownShortcutBinding = binding;
        _lastKeyDownShortcutHandledAt = DateTimeOffset.UtcNow;
    }

    private static bool IsReaderKeyDownFallbackBinding(KeyboardShortcutBinding binding) =>
        binding.Key is "[" or "]";

    private bool WasRecentlyHandledByReaderKeyDownFallback(KeyboardShortcutBinding binding) =>
        _lastKeyDownShortcutBinding.Matches(binding)
        && DateTimeOffset.UtcNow - _lastKeyDownShortcutHandledAt <= ReaderKeyDownCharacterSuppressWindow;

    private static bool ShouldIgnoreReaderShortcutSource(object? source) =>
        source is TextBox
            or PasswordBox
            or RichEditBox
            or AutoSuggestBox
            or NumberBox;

    private async Task<bool> TryHandleReaderShortcutBindingAsync(KeyboardShortcutBinding binding)
    {
        if (_shortcutService.TryResolve(ShortcutScope.Reader, binding, out var readerAction)
            && readerAction != null
            && await HandleReaderShortcutActionAsync(readerAction.Id))
        {
            return true;
        }

        return _shortcutService.TryResolve(ShortcutScope.Sasayaki, binding, out var sasayakiAction)
            && sasayakiAction != null
            && await HandleSasayakiShortcutActionAsync(sasayakiAction.Id);
    }

    private async Task<bool> HandleReaderShortcutActionAsync(string actionId)
    {
        switch (actionId)
        {
            case string id when id == ReaderShortcutActions.PreviousPage.Id:
                return await NavigateReaderPageAsync("backward");
            case string id when id == ReaderShortcutActions.NextPage.Id:
                return await NavigateReaderPageAsync("forward");
            case string id when id == ReaderShortcutActions.Close.Id:
                await ViewModel.BackToLibraryCommand.ExecuteAsync(null);
                return true;
            case string id when id == ReaderShortcutActions.ToggleFocusMode.Id:
                ToggleReaderFocusMode();
                return true;
            case string id when id == ReaderShortcutActions.ToggleStatistics.Id:
                await ViewModel.ToggleStatisticsTrackingCommand.ExecuteAsync(null);
                return true;
            case string id when id == ReaderShortcutActions.ToggleLyricsMode.Id:
                return await ToggleReaderLyricsModeShortcutAsync();
            default:
                return false;
        }
    }

    private async Task<bool> HandleSasayakiShortcutActionAsync(string actionId)
    {
        if (!CanHandleSasayakiShortcut())
            return false;

        switch (actionId)
        {
            case string id when id == SasayakiShortcutActions.PreviousCue.Id:
                await GoToPreviousSasayakiCueAsync();
                return true;
            case string id when id == SasayakiShortcutActions.PlayPause.Id:
                await ToggleSasayakiPlaybackAsync();
                return true;
            case string id when id == SasayakiShortcutActions.NextCue.Id:
                await GoToNextSasayakiCueAsync();
                return true;
            case string id when id == SasayakiShortcutActions.ReplayCue.Id:
                await ReplayCurrentSasayakiCueAsync();
                return true;
            case string id when id == SasayakiShortcutActions.JumpCue.Id:
                await JumpToCurrentSasayakiCueAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> NavigateReaderPageAsync(string direction)
    {
        if (NovelWebView.CoreWebView2 == null)
            return false;

        var directionJson = JsonSerializer.Serialize(direction);
        await NovelWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.hoshiReaderNavigate?.({directionJson});");
        return true;
    }

    private async Task RefreshReaderWebShortcutBindingsAsync()
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        try
        {
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__hoshiReaderShortcutBindings = {BuildReaderWebShortcutBindingsJson()};");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NovelReader] Failed to refresh shortcut bindings in WebView");
        }
    }

    private string BuildReaderWebShortcutBindingsJson()
    {
        var shortcuts = ReaderShortcutActions.All
            .Concat(SasayakiShortcutActions.All)
            .Select(action =>
            {
                var shortcutAction = _shortcutService.Registry.Action(action.Id);
                var binding = shortcutAction == null
                    ? KeyboardShortcutBinding.FromReaderShortcut(action.DefaultShortcut)
                    : _shortcutService.GetBinding(shortcutAction);

                return new
                {
                    action.Id,
                    Binding = new
                    {
                        key = binding.Key,
                        control = binding.Modifiers.HasFlag(KeyboardShortcutModifiers.Control),
                        shift = binding.Modifiers.HasFlag(KeyboardShortcutModifiers.Shift),
                        alt = binding.Modifiers.HasFlag(KeyboardShortcutModifiers.Alt),
                        windows = binding.Modifiers.HasFlag(KeyboardShortcutModifiers.Windows),
                    },
                };
            })
            .ToDictionary(item => item.Id, item => item.Binding);

        return JsonSerializer.Serialize(shortcuts);
    }

    private async Task<bool> ToggleReaderLyricsModeShortcutAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return false;

        await JumpToCurrentSasayakiCueAsync();
        return true;
    }

    private static KeyboardShortcutBinding KeyboardShortcutBindingFromCharacter(
        uint character,
        KeyboardShortcutModifiers modifiers)
    {
        if (character == 0 || character > char.MaxValue)
            return new KeyboardShortcutBinding("");

        var value = (char)character;
        if (char.IsControl(value))
            return new KeyboardShortcutBinding("");

        return new KeyboardShortcutBinding(value.ToString(), modifiers);
    }

    private bool CanHandleSasayakiShortcut() =>
        CurrentSasayakiSettings.EnableSasayaki
        && _sasayakiPlayer != null
        && _sasayakiMatchData?.IsValid == true;

    private async void OnAppSettingChanged(object? sender, Models.DTO.SettingsChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            OnReaderSettingChanged(sender, e);
            return;
        }

        if (e.PropertyName == nameof(AppSettings.SasayakiSettings))
        {
            UpdateSasayakiChromeVisibility();
            if (CurrentSasayakiSettings.EnableSasayaki && _sasayakiMatchData == null)
                _ = LoadSasayakiSidecarAsync();
            return;
        }

        if (e.PropertyName == nameof(AppSettings.StatisticsSettings))
        {
            UpdateStatisticsButtonVisibility();
            RefreshReaderStatisticsChrome();
            if (!CurrentStatisticsSettings.EnableStatistics && ViewModel.IsStatisticsTracking)
                await ViewModel.StopStatisticsTrackingAsync();
            else
                ViewModel.StartStatisticsForAutostart(StatisticsAutostartMode.On);
        }
    }

    private void UpdateSasayakiChromeVisibility()
    {
        var settings = CurrentSasayakiSettings;
        var shouldShow = settings.EnableSasayaki
            && (settings.ReaderShowSasayakiToggle || _sasayakiVM.IsLoaded || _sasayakiMatchData != null);

        SasayakiButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        UpdateSasayakiChromeState();
    }

    private void UpdateSasayakiChromeState()
    {
        var canControl = CanHandleSasayakiShortcut();
        var canLoad = CurrentSasayakiSettings.EnableSasayaki
            && _epubBook != null
            && ViewModel.CurrentBook != null;

        SasayakiLoadAudioMenuItem.IsEnabled = canLoad;
        SasayakiSkipBackMenuItem.IsEnabled = canControl;
        SasayakiPreviousCueMenuItem.IsEnabled = canControl;
        SasayakiPlayPauseMenuItem.IsEnabled = canControl;
        SasayakiNextCueMenuItem.IsEnabled = canControl;
        SasayakiSkipForwardMenuItem.IsEnabled = canControl;
        SasayakiReplayCueMenuItem.IsEnabled = canControl;
        SasayakiJumpCueMenuItem.IsEnabled = canControl;
        SasayakiPanelLoadAudioButton.IsEnabled = canLoad;
        SasayakiPanelSkipBackButton.IsEnabled = canControl;
        SasayakiPanelPreviousCueButton.IsEnabled = canControl;
        SasayakiPanelPlayPauseButton.IsEnabled = canControl;
        SasayakiPanelNextCueButton.IsEnabled = canControl;
        SasayakiPanelSkipForwardButton.IsEnabled = canControl;

        var isPlaying = _sasayakiPlayer?.IsPlaying == true;
        SasayakiPlayPauseMenuIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        SasayakiPanelPlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        SasayakiButtonIcon.Glyph = isPlaying ? "\uE769" : "\uE767";
        UpdateSasayakiPanelValueText();

        var currentCue = _sasayakiNav.CurrentCue?.Text;
        var cueSuffix = string.IsNullOrWhiteSpace(currentCue) ? "" : $" - {currentCue}";
        var tooltip = $"Sasayaki{cueSuffix}";
        ToolTipService.SetToolTip(SasayakiButton, tooltip);
        AutomationProperties.SetHelpText(SasayakiButton, tooltip);
    }

    private void UpdateStatisticsButtonVisibility()
    {
        var readerSettings = App.GetService<IReaderSettingsService>();
        NovelReaderStatisticsButton.Visibility = CurrentStatisticsSettings.EnableStatistics
            && readerSettings.Current.ShowStatisticsToggle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ToggleReaderFocusMode()
    {
        _readerFocusMode = !_readerFocusMode;
        ReaderTopChrome.Visibility = _readerFocusMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReaderBottomChrome.Visibility = _readerFocusMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_readerFocusMode)
            CloseReaderPanels();
    }

    private void RefreshReaderDisplayChrome()
    {
        var readerSettings = App.GetService<IReaderSettingsService>();
        NovelReaderTitleText.Visibility = readerSettings.Current.ShowTitle
            ? Visibility.Visible
            : Visibility.Collapsed;

        var progressText = BuildReaderProgressText();
        NovelReaderTopProgressText.Text = progressText;
        NovelReaderBottomProgressText.Text = progressText;

        var hasProgressText = !string.IsNullOrWhiteSpace(progressText);
        NovelReaderTopProgressText.Visibility = hasProgressText
            && readerSettings.Current.ShowProgressTop
            ? Visibility.Visible
            : Visibility.Collapsed;
        NovelReaderBottomProgressText.Visibility = hasProgressText
            && !readerSettings.Current.ShowProgressTop
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshReaderNavigationHistoryChrome();
    }

    private void RefreshReaderNavigationHistoryChrome()
    {
        UpdateNavigationHistoryButton(
            NovelReaderHistoryBackButton,
            NovelReaderHistoryBackText,
            _navigationHistory.BackTarget,
            "Back");
        UpdateNavigationHistoryButton(
            NovelReaderHistoryForwardButton,
            NovelReaderHistoryForwardText,
            _navigationHistory.ForwardTarget,
            "Forward");
    }

    private void UpdateNavigationHistoryButton(
        Button button,
        TextBlock label,
        ReaderNavigationPosition? target,
        string direction)
    {
        button.Visibility = target.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!target.HasValue)
            return;

        var character = NavigationTargetCharacter(target.Value);
        label.Text = character.ToString();
        var accessibleText = $"{direction} to character {character}";
        ToolTipService.SetToolTip(button, accessibleText);
        AutomationProperties.SetName(button, accessibleText);
    }

    private int NavigationTargetCharacter(ReaderNavigationPosition target)
    {
        if (target.ChapterIndex < 0
            || target.ChapterIndex >= _chapterCharacterCounts.Count
            || target.ChapterIndex >= _chapterStartCharacterCounts.Count)
        {
            return 0;
        }

        return _chapterStartCharacterCounts[target.ChapterIndex]
            + (int)(_chapterCharacterCounts[target.ChapterIndex]
                * Math.Clamp(target.Progress, 0, 1));
    }

    private string BuildReaderProgressText()
    {
        var readerSettings = App.GetService<IReaderSettingsService>();
        var parts = new List<string>();
        if (readerSettings.Current.ShowCharacters && ViewModel.TotalCharacterCount > 0)
            parts.Add($"{ViewModel.CurrentCharacterCount} / {ViewModel.TotalCharacterCount}");
        if (readerSettings.Current.ShowPercentage)
            parts.Add(ViewModel.OverallProgressText);

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void RefreshReaderStatisticsChrome()
    {
        var readerSettings = App.GetService<IReaderSettingsService>();
        if (!CurrentStatisticsSettings.EnableStatistics)
        {
            NovelReaderStatisticsText.Text = "";
            NovelReaderStatisticsText.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (readerSettings.Current.ShowReadingSpeed)
            parts.Add(ViewModel.StatisticsSessionSpeedText);
        if (readerSettings.Current.ShowReadingTime)
            parts.Add(ViewModel.StatisticsSessionChromeTimeText);

        NovelReaderStatisticsText.Text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        NovelReaderStatisticsText.Visibility = parts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LoadChapter(ReaderNavigationRenderRequest renderRequest)
    {
        BeginHiddenRender(renderRequest);
        NavigateCurrentRenderAttempt();
    }

    private void BeginHiddenRender(ReaderNavigationRenderRequest renderRequest)
    {
        var destinationUri = BuildChapterUri(renderRequest.Destination.ChapterIndex);
        _renderState.BeginNavigation(
            renderRequest,
            destinationUri,
            waitsForFragment: _pendingProgrammaticFragment != null);
        NovelWebView.Opacity = 0;
    }

    private bool LoadChapter(
        int index,
        double? progressOverride = null)
    {
        if (_epubBook == null || NovelWebView.CoreWebView2 == null)
            return false;

        if (index < 0 || index >= _epubBook.Chapters.Count)
            return false;

        double progress;
        if (progressOverride.HasValue)
        {
            progress = Math.Clamp(progressOverride.Value, 0, 1);
        }
        else
        {
            // Restore progress when reloading the same chapter (e.g. settings change).
            // Forward navigation: start at 0. Backward: start at end (progress 1).
            if (_previousChapterIndex < 0)
                progress = ViewModel.Progress;
            else if (index == _previousChapterIndex)
                progress = ViewModel.Progress;
            else if (index > _previousChapterIndex)
                progress = 0;
            else
                progress = 1;
        }

        var uri = BuildChapterUri(index);
        if (!_renderState.TryBeginOrdinary(uri, index, progress))
        {
            Log.Information(
                "[NovelReader] Deferring ordinary chapter reload while navigation render is active");
            return false;
        }

        _currentProgress = progress;
        _previousChapterIndex = index;
        ViewModel.SetChapter(index, _epubBook.Chapters.Count);
        ViewModel.UpdateProgress(_currentProgress);
        RefreshReaderDisplayChrome();
        NavigateCurrentRenderAttempt();
        return true;
    }

    private string BuildChapterUri(int index)
    {
        if (_epubBook == null || index < 0 || index >= _epubBook.Chapters.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var chapter = _epubBook.Chapters[index];
        var relativePath = Path
            .GetRelativePath(_epubBook.ContainerDirectory, chapter.Href)
            .Replace('\\', '/');
        return new Uri($"https://{NovelBookHostName}/{relativePath}").AbsoluteUri;
    }

    private bool NavigateCurrentRenderAttempt()
    {
        if (NovelWebView.CoreWebView2 == null
            || _renderState.CurrentAttempt is not { } attempt)
        {
            if (_renderState.HasActiveNavigation)
            {
                _ = HandleTerminalRenderFailureAsync(
                    "Reader navigation could not start.");
            }
            return false;
        }

        // Defer navigation when the WebView hasn't been laid out yet.
        if (NovelWebView.ActualWidth <= 0 || NovelWebView.ActualHeight <= 0)
        {
            Log.Information(
                "[NovelReader] Viewport not ready, deferring chapter {Index}",
                attempt.ChapterIndex);
            _renderAttemptPendingViewport = true;
            return true;
        }

        _renderAttemptPendingViewport = false;
        Log.Information(
            "[NovelReader] Navigating to chapter {Index}: {Url} (progress={Progress:F3})",
            attempt.ChapterIndex,
            attempt.Uri,
            attempt.Progress);

        var readerSettings = App.GetService<Services.Settings.IReaderSettingsService>();
        var appTheme = App.GetService<ISettingsService>().Current.Theme;
        _currentReaderCss = NovelReaderContentStyles.GenerateCss(readerSettings.Current, appTheme);

        NovelWebView.DefaultBackgroundColor = ARGBToWindowsColor(
            readerSettings.Current.BackgroundColor(appTheme));

        try
        {
            // Hide WebView until chapterReady to prevent FOUC.
            NovelWebView.Opacity = 0;
            NovelWebView.CoreWebView2.Navigate(attempt.Uri);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] Failed to start WebView navigation");
            _ = HandleTerminalRenderFailureAsync(
                "Reader content could not be loaded.",
                ex);
            return false;
        }
    }

    private async void OnDomContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (_epubBook == null)
            return;

        var uri = sender.Source;
        if (string.IsNullOrEmpty(uri))
            return;

        if (!uri.StartsWith($"https://{NovelBookHostName}/", StringComparison.OrdinalIgnoreCase))
            return;

        Log.Information("[NovelReader] DOM ready, injecting chapter info, CSS and JS");

        try
        {
            if (!_renderState.TryGetDomAttempt(uri, out var chapterInstruction))
            {
                Log.Warning(
                    "[NovelReader] Ignoring DOM for stale render URI {Uri}",
                    uri);
                if (_renderState.HasActiveNavigation)
                {
                    await HandleTerminalRenderFailureAsync(
                        "Reader content identity could not be verified.");
                }
                return;
            }

            var destinationChapterIndex = chapterInstruction.ChapterIndex;
            var chapterInfo = JsonSerializer.Serialize(new
            {
                index = destinationChapterIndex,
                totalChapters = _epubBook.Chapters.Count,
                progress = chapterInstruction.Progress,
                restoreTarget = chapterInstruction.RestoreTarget switch
                {
                    ReaderChapterRestoreTarget.Start => "start",
                    ReaderChapterRestoreTarget.End => "end",
                    _ => null,
                },
                navigationGeneration = chapterInstruction.NavigationGeneration,
                renderAttemptId = chapterInstruction.RenderAttemptId,
            });
            await sender.ExecuteScriptAsync(
                $"window.__hoshiChapterInfo = {chapterInfo};");
            var highlightsJson = ViewModel.GetChapterHighlightsJson(destinationChapterIndex) ?? "[]";
            await sender.ExecuteScriptAsync(
                $"window.__hoshiChapterHighlights = {highlightsJson};");

            var readerSettings = App.GetService<IReaderSettingsService>();
            var dictionaryDisplaySettings = App.GetService<Services.Settings.ISettingsService>()
                .Current.DictionaryDisplaySettings;
            var lookupSettings = JsonSerializer.Serialize(new
            {
                scanNonJapaneseText = dictionaryDisplaySettings.ScanNonJapaneseText,
                maxResults = dictionaryDisplaySettings.MaxResults,
                scanLength = dictionaryDisplaySettings.ScanLength
            });
            await sender.ExecuteScriptAsync(
                $"window.__hoshiLookupSettings = {lookupSettings}; window.scanNonJapaneseText = {JsonSerializer.Serialize(dictionaryDisplaySettings.ScanNonJapaneseText)};");
            await sender.ExecuteScriptAsync(
                $"window.__hoshiReaderShortcutBindings = {BuildReaderWebShortcutBindingsJson()};");

            if (!string.IsNullOrEmpty(_currentReaderCss))
            {
                var cssScript = NovelReaderContentStyles.GenerateScriptTag(_currentReaderCss);
                await sender.ExecuteScriptAsync(cssScript);
            }

            if (!string.IsNullOrEmpty(_highlightsJs))
                await sender.ExecuteScriptAsync(_highlightsJs);

            if (!string.IsNullOrEmpty(_readerJs))
                await sender.ExecuteScriptAsync(_readerJs);

            var wheelNavigationEnabled = !readerSettings.Current.ContinuousMode && readerSettings.Current.MouseWheelPageTurn;
            await sender.ExecuteScriptAsync(
                $"window.hoshiReader.registerWheelNavigation?.({JsonSerializer.Serialize(wheelNavigationEnabled)});");

            if (!string.IsNullOrEmpty(_selectionJs))
                await sender.ExecuteScriptAsync(_selectionJs);

            // Re-highlight Sasayaki cue if it matches the newly loaded chapter
            if (_sasayakiNav.CurrentMatch is { } match
                && match.ChapterIndex == destinationChapterIndex)
            {
                await HighlightSasayakiCueAsync(
                    match,
                    allowAutoScroll: !_renderState.HasActiveNavigation);
            }
            else
            {
                await ClearSasayakiHighlightAsync();
            }

            Log.Information("[NovelReader] CSS and JS injected via DOMContentLoaded");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] Failed to inject CSS/JS via DOMContentLoaded");
            await HandleTerminalRenderFailureAsync(
                "Reader content could not be prepared.",
                ex);
        }
    }

    private void OnWebViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderAttemptPendingViewport
            && NovelWebView.ActualWidth > 0
            && NovelWebView.ActualHeight > 0)
        {
            Log.Information("[NovelReader] Viewport ready, loading deferred render attempt");
            NavigateCurrentRenderAttempt();
        }
    }

    private async void OnNavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        Log.Information(
            "[NovelReader] Navigation completed: {Status}, IsSuccess: {Success}",
            args.WebErrorStatus,
            args.IsSuccess);
        if (!args.IsSuccess)
        {
            await HandleTerminalRenderFailureAsync(
                "Reader content could not be loaded.");
        }
    }

    private async void OnWebViewProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs args)
    {
        Log.Error(
            "[NovelReader] WebView2 ProcessFailed: Kind={Kind}, ExitCode={ExitCode}, Reason={Reason}",
            args.ProcessFailedKind,
            args.ExitCode,
            args.Reason);
        await HandleTerminalRenderFailureAsync(
            "Reader content process failed.");
    }

    private async void OnWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args
    )
    {
        try
        {
            Log.Information("[NovelReader] WebMessage received: {Raw}", args.WebMessageAsJson);

            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                throw new InvalidDataException("Invalid reader bridge message envelope.");
            }
            var type = typeElement.GetString();

            Log.Information("[NovelReader] Message: version={Version}, type={Type}", version, type);

            if (version != 1)
                throw new InvalidOperationException(
                    $"Unsupported reader message version: {version}"
                );

            switch (type)
            {
                case "debugLog":
                    var dbgPayload = root.GetProperty("payload");
                    Log.Information(
                        "[NovelReader] JS Debug: {Msg} | Data: {Data}",
                        dbgPayload.GetProperty("message").GetString(),
                        dbgPayload.GetProperty("data").GetRawText()
                    );
                    break;
                case "lookupRequest":
                    await HandleLookupRequestAsync(root.GetProperty("payload"));
                    break;
                case "lookupDismiss":
                    _popupOverlay?.Dismiss();
                    await SetLookupPopupActiveAsync(false);
                    break;
                case "readerReady":
                    Log.Information("[NovelReader] Bridge ready, sending setChapter");
                    await SendSetChapterMessageAsync();
                    break;
                case "chapterReady":
                    if (!NovelReaderTerminalPayloadParser.TryParseChapterReady(
                        root,
                        out var readyPayload,
                        out var readyRawPayload))
                    {
                        await HandleMalformedTerminalPayloadAsync("chapterReady");
                        break;
                    }

                    var readyDisposition = _renderState.AcceptChapterReady(
                        readyPayload.ChapterIndex,
                        readyPayload.NavigationGeneration,
                        readyPayload.RenderAttemptId);
                    if (readyDisposition == NovelReaderChapterReadyDisposition.Ordinary)
                    {
                        NovelWebView.Opacity = 1;
                    }
                    else if (readyDisposition == NovelReaderChapterReadyDisposition.HiddenTerminal
                        && readyPayload.NavigationGeneration is long readyGeneration)
                    {
                        TryRevealSettledNavigation(readyGeneration);
                    }
                    else if (readyDisposition == NovelReaderChapterReadyDisposition.HiddenInitial
                        && _pendingProgrammaticFragment != null)
                    {
                        await SendPendingProgrammaticFragmentAsync();
                    }

                    if (readyDisposition != NovelReaderChapterReadyDisposition.Rejected)
                    {
                        Log.Information("[NovelReader] Chapter ready, capturing artifacts");
                        await CaptureReaderArtifactsAsync(readyRawPayload.GetRawText());
                    }
                    break;
                case "restoreCompleted":
                    Log.Information("[NovelReader] Restore completed");
                    if (!NovelReaderTerminalPayloadParser.TryParseRestoreCompleted(
                        root,
                        out var restorePayload))
                    {
                        await HandleMalformedTerminalPayloadAsync("restoreCompleted");
                        break;
                    }

                    if (restorePayload.NavigationGeneration is long navigationGeneration
                        && _renderState.NavigationRequest is { } restoreRequest
                        && _renderState.PendingSettlement == null
                        && restoreRequest.Generation == navigationGeneration
                        && restoreRequest.Destination.ChapterIndex == restorePayload.ChapterIndex
                        && _renderState.CurrentAttempt?.RenderAttemptId
                            == restorePayload.RenderAttemptId)
                    {
                        var settlement = await ViewModel.ResolveNavigationAsync(
                            navigationGeneration,
                            restorePayload.ChapterIndex,
                            restorePayload.Progress,
                            CancellationToken.None);
                        if (settlement == null)
                        {
                            await HandleTerminalRenderFailureAsync(
                                "Reader navigation returned no terminal settlement.");
                        }
                        else if (!ApplyNavigationSettlement(settlement))
                        {
                            await HandleTerminalRenderFailureAsync(
                                "Reader navigation settlement could not be applied.");
                        }
                    }
                    else if (!_renderState.HasActiveNavigation
                        && restorePayload.NavigationGeneration == null
                        && _renderState.CurrentAttempt is
                        {
                            Kind: NovelReaderRenderAttemptKind.Ordinary
                        } ordinaryAttempt
                        && ordinaryAttempt.ChapterIndex == restorePayload.ChapterIndex
                        && ordinaryAttempt.RenderAttemptId == restorePayload.RenderAttemptId)
                    {
                        ViewModel.StartStatisticsForAutostart(StatisticsAutostartMode.On);
                    }
                    break;
                case "pageChanged":
                    if (!ViewModel.CanAcceptReaderPositionMutation)
                    {
                        Log.Information("[NovelReader] Ignoring pageChanged while destination commit is pending");
                        break;
                    }
                    if (!root.TryGetProperty("payload", out var payload)
                        || payload.ValueKind != JsonValueKind.Object)
                    {
                        Log.Warning("[NovelReader] Ignoring invalid pageChanged payload");
                        break;
                    }

                    var result = payload.TryGetProperty("result", out var resultElement)
                        && resultElement.ValueKind == JsonValueKind.String
                        ? resultElement.GetString()
                        : null;
                    var direction = payload.TryGetProperty("direction", out var directionElement)
                        && directionElement.ValueKind == JsonValueKind.String
                        ? directionElement.GetString()
                        : null;
                    var progress = default(double);
                    var hasProgress = payload.TryGetProperty("progress", out var progressElement)
                        && progressElement.ValueKind == JsonValueKind.Number
                        && progressElement.TryGetDouble(out progress);
                    if (!hasProgress
                        || !ReaderStatisticsEventClassifier.TryCreateEvent(
                            result,
                            direction,
                            progress,
                            out var readerEvent))
                    {
                        Log.Warning(
                            "[NovelReader] Ignoring invalid pageChanged event: direction={Dir}, result={Result}",
                            direction,
                            result);
                        break;
                    }

                    Log.Information(
                        "[NovelReader] Page changed: direction={Dir}, result={Result}, progress={Progress:F3}",
                        direction,
                        result,
                        readerEvent.Progress
                    );

                    var outcome = await ViewModel.HandleManualPageNavigationAsync(readerEvent);
                    if (outcome.DidMove)
                    {
                        _navigationHistory.ClearForward();
                        RefreshReaderNavigationHistoryChrome();
                        _currentProgress = readerEvent.Progress;
                    }

                    if (outcome.AdjacentChapterIndex is int adjacentChapterIndex
                        && outcome.AdjacentChapterRestoreTarget is ReaderChapterRestoreTarget restoreTarget)
                    {
                        var renderRequest = ViewModel.TryBeginNavigation(
                            adjacentChapterIndex,
                            restoreTarget,
                            exactProgress: null);
                        if (renderRequest != null)
                            LoadChapter(renderRequest);
                    }

                    if (readerEvent.Result != ReaderPageNavigationResult.Limit
                        && payload.TryGetProperty("state", out var pageState))
                    {
                        await CaptureReaderArtifactsAsync(pageState.GetRawText());
                    }
                    break;
                case "shortcut":
                    var shortcutPayload = root.GetProperty("payload");
                    var actionId = shortcutPayload.GetProperty("actionId").GetString();
                    if (!string.IsNullOrWhiteSpace(actionId))
                    {
                        _ = await HandleReaderShortcutActionAsync(actionId)
                            || await HandleSasayakiShortcutActionAsync(actionId);
                    }
                    break;
                case "internalLink":
                    var internalLinkPayload = root.GetProperty("payload");
                    var href = internalLinkPayload.GetProperty("href").GetString();
                    if (!string.IsNullOrWhiteSpace(href))
                        await NavigateToInternalLinkAsync(href);
                    break;
                case "error":
                    var message = root.TryGetProperty("payload", out var errorPayload)
                        && errorPayload.ValueKind == JsonValueKind.Object
                        && errorPayload.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString()
                            : null;
                    Log.Error("[NovelReader] Bridge error: {Error}", message);
                    await HandleTerminalRenderFailureAsync(
                        "Reader content reported an error.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] Failed to process WebMessage");
            if (_renderState.HasActiveNavigation)
            {
                await HandleTerminalRenderFailureAsync(
                    "Reader content sent an invalid message.",
                    ex);
            }
            else
            {
                App.GetService<INotificationService>()
                    .ShowError("Reader content sent an invalid message.", "Novel reader");
            }
        }
    }

    private async Task HandleMalformedTerminalPayloadAsync(string messageType)
    {
        Log.Warning(
            "[NovelReader] Ignoring malformed terminal payload: {MessageType}",
            messageType);
        if (_renderState.HasActiveNavigation)
        {
            await HandleTerminalRenderFailureAsync(
                "Reader content sent an invalid terminal message.");
        }
    }

    private async Task HandleTerminalRenderFailureAsync(
        string notificationMessage,
        Exception? exception = null)
    {
        if (exception != null)
            Log.Error(exception, "[NovelReader] Terminal render failure");

        if (_terminalFailureRecoveryInProgress)
            return;

        _terminalFailureRecoveryInProgress = true;
        try
        {
            if (!_renderState.HasActiveNavigation
                || _renderState.CurrentAttempt is
                {
                    Kind: NovelReaderRenderAttemptKind.Recovery
                })
            {
                await ApplyNativeTerminalFallbackAsync(notificationMessage);
                return;
            }

            if (_renderState.PendingSettlement is { } pendingSettlement)
            {
                var recoveryUri = BuildChapterUri(
                    pendingSettlement.Position.ChapterIndex);
                if (_renderState.TryBeginPendingSettlementRecovery(recoveryUri)
                    && NavigateCurrentRenderAttempt())
                {
                    _pendingProgrammaticFragment = null;
                    return;
                }

                await ApplyNativeTerminalFallbackAsync(notificationMessage);
                return;
            }

            ReaderNavigationSettlement? settlement;
            try
            {
                settlement = await ViewModel.HandleNavigationBridgeErrorAsync();
            }
            catch (Exception settlementException)
            {
                Log.Error(
                    settlementException,
                    "[NovelReader] Failed to settle terminal render failure");
                await ApplyNativeTerminalFallbackAsync(notificationMessage);
                return;
            }

            if (settlement != null
                && ApplyNavigationSettlement(
                    settlement,
                    forceTerminalReload: true))
            {
                return;
            }

            Log.Warning("[NovelReader] Navigation failure returned no applicable settlement");
            await ApplyNativeTerminalFallbackAsync(notificationMessage);
        }
        catch (Exception recoveryException)
        {
            Log.Error(
                recoveryException,
                "[NovelReader] Failed to recover terminal render failure");
            await ApplyNativeTerminalFallbackAsync(notificationMessage);
        }
        finally
        {
            _terminalFailureRecoveryInProgress = false;
        }
    }

    private Task ApplyNativeTerminalFallbackAsync(string notificationMessage)
    {
        var hadActiveNavigation = _renderState.HasActiveNavigation;
        var release = _renderState.TryPrepareFailure();
        if (hadActiveNavigation && release == null)
            return Task.CompletedTask;

        if (release != null
            && !ViewModel.AcknowledgeNavigationRendered(release.Value.Generation))
        {
            Log.Warning(
                "[NovelReader] Terminal failure generation {Generation} was already acknowledged",
                release.Value.Generation);
        }

        if (release != null)
            _renderState.CompleteFailure(release.Value);

        _pendingProgrammaticFragment = null;
        _renderAttemptPendingViewport = false;
        _renderState.DiscardDeferredOrdinaryReload();
        NovelWebView.Opacity = 1;
        App.GetService<INotificationService>()
            .ShowError(notificationMessage, "Novel reader");
        return Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task HandleLookupRequestAsync(
        System.Text.Json.JsonElement payload)
    {
        var traceId = payload.TryGetProperty("traceId", out var traceElement)
            ? traceElement.GetString() ?? $"reader-lookup-native-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : $"reader-lookup-native-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var clientNow = payload.TryGetProperty("clientNow", out var clientNowElement)
            && clientNowElement.TryGetDouble(out var parsedClientNow)
                ? parsedClientNow
                : (double?)null;
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();
        var text = payload.GetProperty("text").GetString() ?? "";
        var sentence = payload.GetProperty("sentence").GetString() ?? "";
        var x = payload.GetProperty("x").GetDouble();
        var y = payload.GetProperty("y").GetDouble();
        var width = payload.GetProperty("width").GetDouble();
        var height = payload.GetProperty("height").GetDouble();
        var sentenceOffset = TryGetOptionalInt32(payload, "sentenceOffset");
        var normalizedOffset = TryGetOptionalInt32(payload, "normalizedOffset");

        Log.Information(
            "[LookupTrace] trace={TraceId} received textLen={TextLen} clientNow={ClientNow} point=({X:F0},{Y:F0}) rect=({Width:F0}x{Height:F0})",
            traceId, text.Length, clientNow, x, y, width, height);
        Log.Information(
            "[NovelReader] Lookup request: '{Text}' (sentence: '{Sentence}') at ({X:F0},{Y:F0})",
            text, sentence, x, y);

        if (string.IsNullOrWhiteSpace(text))
            return;

        var requestVersion = Interlocked.Increment(ref _lookupRequestVersion);
        // Don't dismiss — new lookup replaces the existing popup content inline.
        // The popup stays open persistently until the user clicks the close button.

        var lookupService = App.GetService<IDictionaryLookupService>();
        var dictionaryDisplaySettings = App.GetService<Services.Settings.ISettingsService>()
            .Current.DictionaryDisplaySettings;

        phaseSw.Restart();
        await _lookupSemaphore.WaitAsync();
        Log.Information(
            "[LookupTrace] trace={TraceId} lookup semaphore acquired in {Ms}ms total={TotalMs}ms requestVersion={RequestVersion}",
            traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, requestVersion);
        try
        {
            if (requestVersion != Volatile.Read(ref _lookupRequestVersion))
            {
                Log.Information(
                    "[LookupTrace] trace={TraceId} abandoned before native lookup total={TotalMs}ms requestVersion={RequestVersion} currentVersion={CurrentVersion}",
                    traceId, totalSw.ElapsedMilliseconds, requestVersion, Volatile.Read(ref _lookupRequestVersion));
                return;
            }

            phaseSw.Restart();
            var results = await lookupService.LookupAsync(
                text,
                dictionaryDisplaySettings.MaxResults,
                dictionaryDisplaySettings.ScanLength,
                traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} native lookup finished in {Ms}ms total={TotalMs}ms results={Count}",
                traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results.Count);

            Log.Information(
                "[NovelReader] Lookup returned {Count} results for '{Text}'",
                results.Count, text);

            if (requestVersion != Volatile.Read(ref _lookupRequestVersion))
            {
                Log.Information(
                    "[LookupTrace] trace={TraceId} abandoned after native lookup total={TotalMs}ms requestVersion={RequestVersion} currentVersion={CurrentVersion}",
                    traceId, totalSw.ElapsedMilliseconds, requestVersion, Volatile.Read(ref _lookupRequestVersion));
                return;
            }

            if (results.Count == 0)
            {
                Log.Information("[NovelReader] No results, popup will not be shown");
                Log.Information(
                    "[LookupTrace] trace={TraceId} no results total={TotalMs}ms",
                    traceId, totalSw.ElapsedMilliseconds);
                return;
            }

            phaseSw.Restart();
            await HighlightLookupSelectionAsync(results[0].Matched);
            Log.Information(
                "[LookupTrace] trace={TraceId} reader highlight finished in {Ms}ms total={TotalMs}ms matched='{Matched}'",
                traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results[0].Matched);

            phaseSw.Restart();
            var styles = await lookupService.GetStylesAsync();
            Log.Information(
                "[LookupTrace] trace={TraceId} styles loaded in {Ms}ms total={TotalMs}ms styles={Count}",
                traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, styles.Count);
            if (requestVersion != Volatile.Read(ref _lookupRequestVersion))
            {
                Log.Information(
                    "[LookupTrace] trace={TraceId} abandoned after styles total={TotalMs}ms requestVersion={RequestVersion} currentVersion={CurrentVersion}",
                    traceId, totalSw.ElapsedMilliseconds, requestVersion, Volatile.Read(ref _lookupRequestVersion));
                return;
            }
            var styleDict = styles.ToDictionary(s => s.DictName, s => s.Styles);

            // Convert WebView2-relative coordinates to the dictionary overlay canvas.
            var webViewTransform = NovelWebView.TransformToVisual(DictionaryOverlayCanvas);
            var webViewOffset = webViewTransform.TransformPoint(new Windows.Foundation.Point(0, 0));
            var windowX = webViewOffset.X + x;
            var windowY = webViewOffset.Y + y;

            var readerSettings = App.GetService<Services.Settings.IReaderSettingsService>();
            var isVertical = readerSettings.Current.VerticalWriting;
            var appSettings = App.GetService<ISettingsService>().Current;
            var appTheme = appSettings.Theme;
            var audioSettings = appSettings.AudioSettings;
            var ankiSettings = appSettings.AnkiSettings;
            var miningContext = CreateReaderAnkiMiningContext(
                sentence,
                sentenceOffset,
                normalizedOffset);

            var popupOverlay = EnsurePopupOverlay();
            _ = popupOverlay.PrewarmAsync(XamlRoot, appTheme);
            PauseSasayakiForLookup();
            phaseSw.Restart();
            await popupOverlay.ShowLookupAsync(
                results, styleDict, dictionaryDisplaySettings,
                windowX, windowY, width, height,
                XamlRoot, isVertical, appTheme,
                audioSettings,
                ankiSettings,
                miningContext,
                traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} popup show completed in {Ms}ms total={TotalMs}ms window=({X:F0},{Y:F0}) vertical={Vertical}",
                traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, windowX, windowY, isVertical);
            phaseSw.Restart();
            await SetLookupPopupActiveAsync(true);
            Log.Information(
                "[LookupTrace] trace={TraceId} active flag set in {Ms}ms total={TotalMs}ms",
                traceId, phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
        }
        finally
        {
            _lookupSemaphore.Release();
            Log.Information(
                "[LookupTrace] trace={TraceId} completed total={TotalMs}ms",
                traceId, totalSw.ElapsedMilliseconds);
        }
    }

    private static int? TryGetOptionalInt32(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element)
            || element.ValueKind == JsonValueKind.Null
            || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number)
            return null;

        if (element.TryGetInt32(out var value))
            return value >= 0 ? value : null;

        if (!element.TryGetDouble(out var doubleValue) || !double.IsFinite(doubleValue))
            return null;

        var rounded = (long)Math.Round(doubleValue);
        return rounded >= 0 && rounded <= int.MaxValue
            ? (int)rounded
            : null;
    }

    private AnkiMiningContext CreateReaderAnkiMiningContext(
        string sentence,
        int? sentenceOffset,
        int? normalizedOffset)
    {
        var book = ViewModel.CurrentBook;
        var context = new AnkiMiningContext
        {
            Sentence = sentence,
            SentenceOffset = sentenceOffset,
            DocumentTitle = book?.Title,
            CoverPath = book?.CoverPath,
        };

        var match = normalizedOffset.HasValue
            ? TryFindSasayakiMatchAtOffset(ViewModel.CurrentChapterIndex, normalizedOffset.Value)
            : null;
        if (match == null || _sasayakiMatchData == null)
            return context;

        var audiobookPath = _sasayakiMatchData.AudiobookPath;
        if (string.IsNullOrWhiteSpace(audiobookPath) || !File.Exists(audiobookPath))
            return context;

        context.SasayakiAudioProvider = (request, ct) =>
            RequestSasayakiMiningAudioAsync(match, sentence, request, ct);
        context.SasayakiPopupControls = new SasayakiPopupControls(
            TogglePlaybackAsync: ToggleSasayakiPlaybackAsync,
            ReplayCueAsync: () => ReplaySasayakiMatchAsync(match),
            JumpToCueAsync: () => JumpToAndPlaySasayakiMatchAsync(match),
            IsPlaying: () => _sasayakiPlayer?.IsPlaying == true,
            CanControl: CanHandleSasayakiShortcut);
        return context;
    }

    private SasayakiMatch? TryFindSasayakiMatchAtOffset(int chapterIndex, int normalizedOffset)
    {
        if (_sasayakiMatchData?.IsValid != true)
            return null;

        return _sasayakiMatchData.Matches.FirstOrDefault(match =>
            match.ChapterIndex == chapterIndex
            && normalizedOffset >= match.StartCodePoint
            && normalizedOffset < match.StartCodePoint + Math.Max(1, match.Length));
    }

    private async Task HighlightLookupSelectionAsync(string matchedText)
    {
        if (NovelWebView.CoreWebView2 == null || string.IsNullOrEmpty(matchedText))
            return;

        var highlightCount = matchedText.EnumerateRunes().Count();
        if (highlightCount <= 0)
            return;

        try
        {
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.hoshiSelection.highlightSelection({highlightCount});");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NovelReader] Failed to highlight lookup selection");
        }
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay != null)
            return _popupOverlay;

        _popupOverlay = new DictionaryPopupOverlay();
        _popupOverlay.Dismissed += OnPopupOverlayDismissed;
        _popupOverlay.UseCanvas(DictionaryOverlayCanvas);
        return _popupOverlay;
    }

    private void OnPopupOverlayDismissed(object? sender, EventArgs e)
    {
        _ = SetLookupPopupActiveAsync(false);
    }

    private void PauseSasayakiForLookup()
    {
        if (!CurrentSasayakiSettings.AutoPauseOnLookup)
            return;
        if (_sasayakiPlayer?.IsPlaying != true)
            return;

        _sasayakiPlayer.Pause();
        _sasayakiVM.UpdatePlaybackState(
            false,
            true,
            _sasayakiPlayer.PositionSeconds,
            _sasayakiPlayer.DurationSeconds);
        UpdateSasayakiChromeState();
        _ = SaveSasayakiPlaybackAsync();
    }

    private async Task SetLookupPopupActiveAsync(bool active)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        try
        {
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__hoshiLookupPopupActive = {JsonSerializer.Serialize(active)};");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NovelReader] Failed to update lookup popup active flag");
        }
    }

    private async System.Threading.Tasks.Task SendSetChapterMessageAsync()
    {
        if (NovelWebView.CoreWebView2 == null || _epubBook == null)
            return;

        if (_renderState.CurrentAttempt is not { } chapterInstruction)
            return;

        var message = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(
            chapterInstruction.ChapterIndex,
            _epubBook.Chapters.Count,
            chapterInstruction.RenderAttemptId,
            chapterInstruction.Progress,
            chapterInstruction.NavigationGeneration,
            chapterInstruction.RestoreTarget);
        Log.Information("[NovelReader] Sending setChapter: {Msg}", message);
        NovelWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private async System.Threading.Tasks.Task SendRestoreProgressMessageAsync(
        double progress,
        long renderAttemptId,
        long? navigationGeneration = null)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        var message = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(
            progress,
            renderAttemptId,
            navigationGeneration);
        Log.Information("[NovelReader] Sending restoreProgress: {Msg}", message);
        NovelWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private async Task SendJumpToFragmentMessageAsync(
        string fragment,
        long renderAttemptId,
        long navigationGeneration)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        var message = NovelReaderBridgeMessageFactory.CreateJumpToFragmentMessage(
            fragment,
            renderAttemptId,
            navigationGeneration);
        Log.Information("[NovelReader] Sending jumpToFragment: {Msg}", message);
        NovelWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private async Task SendPendingProgrammaticFragmentAsync()
    {
        if (_pendingProgrammaticFragment is not { } fragment
            || _renderState.NavigationRequest is not { } renderRequest
            || _renderState.CurrentAttempt is not { } renderAttempt)
        {
            return;
        }

        _pendingProgrammaticFragment = null;
        await SendJumpToFragmentMessageAsync(
            fragment,
            renderAttempt.RenderAttemptId,
            renderRequest.Generation);
    }

    private bool ApplyNavigationSettlement(
        ReaderNavigationSettlement settlement,
        bool forceTerminalReload = false)
    {
        var recoveryUri = settlement.ShouldRevealDestination && !forceTerminalReload
            ? string.Empty
            : BuildChapterUri(settlement.Position.ChapterIndex);
        if (!_renderState.TryApplySettlement(
            settlement,
            recoveryUri,
            forceTerminalReload))
            return false;

        _currentProgress = settlement.Position.Progress;
        _previousChapterIndex = settlement.Position.ChapterIndex;
        RefreshReaderDisplayChrome();
        RefreshReaderNavigationHistoryChrome();

        if (settlement.ShouldRevealDestination && !forceTerminalReload)
        {
            TryRevealSettledNavigation(settlement.Generation);
            return true;
        }

        _pendingProgrammaticFragment = null;
        return NavigateCurrentRenderAttempt();
    }

    private void TryRevealSettledNavigation(long generation)
    {
        if (!_renderState.TryPrepareCompletion(out var release)
            || release.Generation != generation)
            return;

        NovelWebView.Opacity = 1;
        if (!ViewModel.AcknowledgeNavigationRendered(release.Generation))
        {
            Log.Warning(
                "[NovelReader] Terminal navigation render {Generation} was already acknowledged",
                release.Generation);
        }
        _renderState.CompleteSuccess(release);

        _pendingProgrammaticFragment = null;
        if (_renderState.TryTakeDeferredOrdinaryReload())
            LoadChapter(ViewModel.CurrentChapterIndex);
    }

    private async Task<bool> NavigateProgrammaticallyAsync(
        int chapterIndex,
        double progress,
        string? fragment = null,
        bool recordHistory = true)
    {
        if (_epubBook == null
            || chapterIndex < 0
            || chapterIndex >= _epubBook.Chapters.Count)
        {
            return false;
        }

        progress = Math.Clamp(progress, 0, 1);
        fragment = string.IsNullOrWhiteSpace(fragment) ? null : fragment;
        if (chapterIndex == ViewModel.CurrentChapterIndex
            && fragment == null
            && !ReaderStatisticsEventClassifier.HasProgressMovement(
                ViewModel.Progress,
                progress))
        {
            return false;
        }

        await ViewModel.CheckpointProgrammaticDepartureAsync();
        var renderRequest = ViewModel.TryBeginNavigation(
            chapterIndex,
            restoreTarget: null,
            exactProgress: fragment == null ? progress : null);
        if (renderRequest == null)
            return false;

        if (recordHistory)
            _navigationHistory.Record(CurrentReaderNavigationPosition());
        RefreshReaderNavigationHistoryChrome();

        _pendingProgrammaticFragment = fragment;
        if (chapterIndex == ViewModel.CurrentChapterIndex)
        {
            BeginHiddenRender(renderRequest);
            var renderAttemptId = _renderState.CurrentAttempt!.RenderAttemptId;
            if (fragment != null)
            {
                _pendingProgrammaticFragment = null;
                await SendJumpToFragmentMessageAsync(
                    fragment,
                    renderAttemptId,
                    renderRequest.Generation);
            }
            else
            {
                await SendRestoreProgressMessageAsync(
                    progress,
                    renderAttemptId,
                    renderRequest.Generation);
            }
        }
        else
        {
            LoadChapter(renderRequest);
        }

        return true;
    }

    private async Task NavigateToInternalLinkAsync(string href)
    {
        if (_epubBook == null)
            return;

        var target = ReaderInternalLinkResolver.Resolve(
            _epubBook.ContainerDirectory,
            _epubBook.Chapters,
            href,
            NovelBookHostName);
        if (target == null)
            return;

        _popupOverlay?.Dismiss();
        await SetLookupPopupActiveAsync(false);
        await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            0,
            target.Fragment);
    }

    private ReaderNavigationPosition CurrentReaderNavigationPosition() =>
        new(ViewModel.CurrentChapterIndex, ViewModel.Progress);

    private async void HistoryBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_navigationHistory.TryGoBack(
                CurrentReaderNavigationPosition(),
                out var target))
        {
            return;
        }

        RefreshReaderNavigationHistoryChrome();
        await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            target.Progress,
            recordHistory: false);
    }

    private async void HistoryForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_navigationHistory.TryGoForward(
                CurrentReaderNavigationPosition(),
                out var target))
        {
            return;
        }

        RefreshReaderNavigationHistoryChrome();
        await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            target.Progress,
            recordHistory: false);
    }

    private async void ChapterListButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null) return;

        ReaderChapterListContent.ChapterSelected -= OnChapterSelected;
        ReaderChapterListContent.CharacterJumpRequested -= OnCharacterJumpRequested;
        ReaderChapterListContent.Load(
            _epubBook.Chapters,
            _epubBook.Toc,
            ViewModel.CurrentChapterIndex,
            _chapterStartCharacterCounts,
            ViewModel.CurrentCharacterCount,
            ViewModel.TotalCharacterCount);
        ReaderChapterListContent.ChapterSelected += OnChapterSelected;
        ReaderChapterListContent.CharacterJumpRequested += OnCharacterJumpRequested;
        await ShowReaderPanelDialogAsync(ReaderChapterPanelDialog);
        ReaderChapterListContent.SelectCurrentChapter();
    }

    private async void AppearanceButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowReaderPanelDialogAsync(ReaderAppearancePanelDialog);
    }

    private async void HighlightsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null)
            return;

        RefreshHighlightList();
        await ShowReaderPanelDialogAsync(ReaderHighlightsPanelDialog);
    }

    private async void StatisticsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowReaderPanelDialogAsync(ReaderStatisticsPanelDialog);
    }

    private async void SearchButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null) return;

        await ShowReaderPanelDialogAsync(ReaderSearchPanelDialog);
        ReaderSearchQueryBox.Focus(FocusState.Programmatic);
        if (!ReaderSearchTextFilter.HasMatchableText(ReaderSearchQueryBox.Text))
            SetReaderSearchStatus(ReaderSearchPanelStatus.Prompt);

        try
        {
            await EnsureSearchDocumentAsync();
        }
        catch (Exception ex)
        {
            SetReaderSearchStatus(ReaderSearchPanelStatus.Failed);
            Log.Error(ex, "[NovelReader] Failed to prepare book search");
            App.GetService<INotificationService>()
                .ShowError("Could not prepare search for this book.", "Novel reader");
        }
    }

    private async void ReaderSearchQueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ReaderSearchQueryBox.Text;
        var requestVersion = Interlocked.Increment(ref _searchRequestVersion);
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (!ReaderSearchTextFilter.HasMatchableText(query))
        {
            ReaderSearchResultsList.ItemsSource = Array.Empty<ReaderSearchResult>();
            SetReaderSearchStatus(ReaderSearchPanelStatus.Prompt);
            return;
        }

        SetReaderSearchStatus(ReaderSearchPanelStatus.Loading);

        try
        {
            var document = await EnsureSearchDocumentAsync(token);
            var results = await Task.Run(
                () => new ReaderSearchEngine(document).Search(query, 200),
                token);

            if (requestVersion == Volatile.Read(ref _searchRequestVersion)
                && !token.IsCancellationRequested)
            {
                ReaderSearchResultsList.ItemsSource = results;
                SetReaderSearchStatus(results.Count == 0
                    ? ReaderSearchPanelStatus.NoMatches
                    : ReaderSearchPanelStatus.Hidden);
            }
        }
        catch (OperationCanceledException)
        {
            // Newer query superseded this one.
        }
        catch (Exception ex)
        {
            ReaderSearchResultsList.ItemsSource = Array.Empty<ReaderSearchResult>();
            SetReaderSearchStatus(ReaderSearchPanelStatus.Failed);
            Log.Error(ex, "[NovelReader] Search failed for query '{Query}'", query);
            App.GetService<INotificationService>()
                .ShowError("Could not search this book.", "Novel reader");
        }
    }

    private void SetReaderSearchStatus(ReaderSearchPanelStatus status)
    {
        ReaderSearchStatusPanel.Visibility = status == ReaderSearchPanelStatus.Hidden
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReaderSearchLoadingRing.IsActive = status == ReaderSearchPanelStatus.Loading;
        ReaderSearchLoadingRing.Visibility = status == ReaderSearchPanelStatus.Loading
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderSearchPromptText.Visibility = status == ReaderSearchPanelStatus.Prompt
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderSearchLoadingText.Visibility = status == ReaderSearchPanelStatus.Loading
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderSearchNoMatchesText.Visibility = status == ReaderSearchPanelStatus.NoMatches
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReaderSearchFailedText.Visibility = status == ReaderSearchPanelStatus.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task<ReaderSearchDocument> EnsureSearchDocumentAsync(
        CancellationToken ct = default)
    {
        if (_searchDocument != null)
            return _searchDocument;
        if (_epubBook == null)
            throw new InvalidOperationException("EPUB is not loaded.");

        _searchDocument = await ReaderSearchDocumentFactory.CreateAsync(
            _epubBook,
            _chapterCharacterCounts,
            ct);
        return _searchDocument;
    }

    private async void SearchResult_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReaderSearchResult result)
            return;

        CloseReaderPanels();
        await NavigateProgrammaticallyAsync(
            result.ChapterIndex,
            result.ChapterProgress);
    }

    private async void Highlight_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReaderHighlightListItem item)
            return;

        CloseReaderPanels();
        await JumpToHighlightAsync(item);
    }

    private async void ReaderHighlightDeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ReaderHighlightListItem item)
            return;

        await DeleteHighlightItemAsync(item);
    }

    private void RefreshHighlightList()
    {
        var items = ViewModel.GetHighlightListItems(BuildChapterLabels());
        ReaderHighlightsList.ItemsSource = items;
        ReaderHighlightsEmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private IReadOnlyList<string> BuildChapterLabels()
    {
        if (_epubBook == null)
            return [];

        var rows = ReaderChapterListDialog.BuildChapterRows(
            _epubBook.Chapters,
            _epubBook.Toc,
            ViewModel.CurrentChapterIndex,
            _chapterStartCharacterCounts,
            ViewModel.CurrentCharacterCount);
        var labelsBySpine = rows
            .GroupBy(row => row.SpineIndex)
            .ToDictionary(group => group.Key, group => group.First().DisplayTitle);

        return _epubBook.Chapters
            .Select((chapter, index) => labelsBySpine.TryGetValue(index, out var label)
                ? label
                : $"Chapter {index + 1}")
            .ToList();
    }

    private async Task JumpToHighlightAsync(ReaderHighlightListItem item)
    {
        var target = item.JumpTarget;
        await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            target.ChapterProgress);
    }

    private async Task DeleteHighlightItemAsync(ReaderHighlightListItem item)
    {
        var deleted = await ViewModel.DeleteHighlightAsync(item.Highlight.Id);
        if (!deleted)
            return;

        await RemoveHighlightFromWebViewAsync(item.Highlight.Id);
        RefreshHighlightList();
    }

    private async Task RemoveHighlightFromWebViewAsync(Guid id)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        try
        {
            var idJson = JsonSerializer.Serialize(id.ToString("D"));
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.hoshiHighlights?.removeHighlight({idJson});");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[NovelReader] Failed to remove highlight");
        }
    }

    private async void OnChapterSelected(object? sender, int chapterIndex)
    {
        CloseReaderPanels();

        if (chapterIndex >= 0 && chapterIndex != ViewModel.CurrentChapterIndex)
        {
            await NavigateProgrammaticallyAsync(chapterIndex, 0);
        }
    }

    private async void OnCharacterJumpRequested(object? sender, int characterCount)
    {
        CloseReaderPanels();
        await JumpToCharacterAsync(characterCount);
    }

    private async Task JumpToCharacterAsync(int characterCount)
    {
        var target = ResolveCharacterJumpTarget(characterCount);
        if (target == null)
            return;

        await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            target.ChapterProgress);
    }

    private ReaderHighlightJumpTarget? ResolveCharacterJumpTarget(int characterCount)
    {
        if (_chapterCharacterCounts.Count == 0)
            return null;

        var total = Math.Max(0, ViewModel.TotalCharacterCount);
        var target = total > 0
            ? Math.Clamp(characterCount, 0, total - 1)
            : Math.Max(0, characterCount);

        var chapterIndex = 0;
        for (var i = 0; i < _chapterStartCharacterCounts.Count; i++)
        {
            if (_chapterStartCharacterCounts[i] <= target)
                chapterIndex = i;
            else
                break;
        }

        chapterIndex = Math.Clamp(chapterIndex, 0, _chapterCharacterCounts.Count - 1);
        var chapterStart = chapterIndex < _chapterStartCharacterCounts.Count
            ? _chapterStartCharacterCounts[chapterIndex]
            : 0;
        var chapterCount = Math.Max(1, _chapterCharacterCounts[chapterIndex]);
        var progress = Math.Clamp((target - chapterStart) / (double)chapterCount, 0, 1);
        return new ReaderHighlightJumpTarget(chapterIndex, progress);
    }

    private Task ShowReaderPanelDialogAsync(ContentDialog dialog)
    {
        if (_activeReaderPanelDialog == dialog)
            return Task.CompletedTask;

        CloseReaderPanels();
        dialog.XamlRoot = XamlRoot;
        _activeReaderPanelDialog = dialog;
        _ = TrackReaderPanelDialogAsync(dialog);
        return Task.CompletedTask;
    }

    private async Task TrackReaderPanelDialogAsync(ContentDialog dialog)
    {
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[NovelReader] Failed to show reader panel");
        }
        finally
        {
            if (_activeReaderPanelDialog == dialog)
                _activeReaderPanelDialog = null;
        }
    }

    private void CloseReaderPanels()
    {
        _activeReaderPanelDialog?.Hide();
        _activeReaderPanelDialog = null;
    }

    private static IReadOnlyList<int> CalculateChapterCharacterCounts(EpubBook book)
    {
        return book.Chapters
            .Select(chapter => CountReadableCharacters(chapter.Href))
            .ToArray();
    }

    private static IReadOnlyList<int> CalculateChapterStartCharacterCounts(IReadOnlyList<int> chapterCharacterCounts)
    {
        var starts = new int[chapterCharacterCounts.Count];
        var total = 0;
        for (var i = 0; i < chapterCharacterCounts.Count; i++)
        {
            starts[i] = total;
            total += Math.Max(0, chapterCharacterCounts[i]);
        }

        return starts;
    }

    private static int CountReadableCharacters(string chapterPath)
    {
        if (!File.Exists(chapterPath))
            return 0;

        var html = File.ReadAllText(chapterPath);
        return ReaderTextFilter.CountReadableCharacters(html);
    }


    private async System.Threading.Tasks.Task CaptureReaderArtifactsAsync(
        string readerStateJson
    )
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        Log.Information("[NovelReader] Reader state: {State}", readerStateJson);

        var artifactDirectory = Environment.GetEnvironmentVariable(
            ArtifactDirectoryEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Log.Information("[NovelReader] Artifact directory not set, skipping capture");
            return;
        }

        Directory.CreateDirectory(artifactDirectory);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var statePath = Path.Combine(
            artifactDirectory,
            $"{timestamp}-reader-state.json"
        );
        var capturePath = Path.Combine(
            artifactDirectory,
            $"{timestamp}-webview-capture.png"
        );

        await File.WriteAllTextAsync(statePath, readerStateJson);
        await using var fileStream = File.Create(capturePath);
        await NovelWebView.CoreWebView2.CapturePreviewAsync(
            CoreWebView2CapturePreviewImageFormat.Png,
            fileStream.AsRandomAccessStream()
        );
        Log.Information(
            "[NovelReader] Artifacts saved: {StatePath}, {CapturePath}",
            statePath,
            capturePath
        );
    }

    private async void OnWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        var uriStr = args.Request.Uri;
        if (string.IsNullOrEmpty(uriStr))
            return;
        if (!uriStr.StartsWith($"https://{NovelBookHostName}/", StringComparison.OrdinalIgnoreCase))
            return;

        var requestUri = new Uri(uriStr);
        var path = requestUri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path))
            return;

        var isHtml = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);
        var isCss = path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        if (!isHtml && !isCss)
            return;

        var decoded = Uri.UnescapeDataString(path);
        var filePath = Path.GetFullPath(Path.Combine(_epubBook!.ContainerDirectory, decoded));
        if (!filePath.StartsWith(_epubBook.ContainerDirectory, StringComparison.OrdinalIgnoreCase))
            return;
        if (!File.Exists(filePath))
            return;

        var deferral = args.GetDeferral();
        try
        {
            var fileBytes = File.ReadAllBytes(filePath);
            var mediaType = ResolveMediaType(filePath, isHtml, isCss);

            if (isCss)
                fileBytes = SanitizeReaderCss(fileBytes);

            var responseHeaders = isHtml || isCss
                ? $"Content-Type: {mediaType}; charset=utf-8\r\n"
                : $"Content-Type: {mediaType}\r\n";

            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(fileBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            args.Response = sender.Environment.CreateWebResourceResponse(
                stream, 200, "OK", responseHeaders);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] WebResourceRequested failed for {Path}", filePath);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private string ResolveMediaType(string filePath, bool isHtml, bool isCss)
    {
        foreach (var item in _epubBook!.Manifest.Values)
        {
            if (string.Equals(item.Href, filePath, StringComparison.OrdinalIgnoreCase))
                return item.MediaType;
        }

        if (isHtml)
            return "text/html";
        if (isCss)
            return "text/css";
        return "application/octet-stream";
    }

    private static byte[] SanitizeReaderCss(byte[] bytes)
    {
        var css = System.Text.Encoding.UTF8.GetString(bytes);
        var sanitized = EpubCssSanitizerRegex().Replace(css, match =>
        {
            var indent = match.Groups["indent"].Value;
            var property = match.Groups["prop"].Value.ToLowerInvariant();
            var value = match.Groups["value"].Value.Trim();
            return property switch
            {
                "writing-mode" => "",
                "line-break" => $"{indent}-webkit-line-break: {value};\n{indent}line-break: {value};\n",
                "word-break" => $"{indent}word-break: {value};\n",
                "hyphens" => $"{indent}-webkit-hyphens: {value};\n{indent}hyphens: {value};\n",
                "text-underline-position" => $"{indent}text-underline-position: {value};\n",
                "text-combine" => $"{indent}-webkit-text-combine: {value};\n{indent}text-combine-upright: {(value.Equals("horizontal", StringComparison.OrdinalIgnoreCase) ? "all" : value)};\n",
                "text-orientation" => $"{indent}-webkit-text-orientation: {value};\n{indent}text-orientation: {value};\n",
                "text-emphasis-style" => $"{indent}-webkit-text-emphasis-style: {value};\n{indent}text-emphasis-style: {value};\n",
                "text-emphasis-color" => $"{indent}-webkit-text-emphasis-color: {value};\n{indent}text-emphasis-color: {value};\n",
                _ => "",
            };
        });
        return System.Text.Encoding.UTF8.GetBytes(sanitized);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<indent>[ \t]*)-epub-(?<prop>[^:;{}\r\n]+)[ \t]*:[ \t]*(?<value>[^;{}\r\n]*)[ \t]*;[ \t]*(?:\r?\n)?",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EpubCssSanitizerRegex();

    private static Color ARGBToWindowsColor(uint argb)
    {
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF)
        );
    }

    // ── Sasayaki ────────────────────────────────────────────────────

    private async void SasayakiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSasayakiPanelOpen)
            return;

        RefreshSasayakiPanelControls();
        SasayakiPanelDialog.XamlRoot = XamlRoot;
        _isSasayakiPanelOpen = true;

        try
        {
            await SasayakiPanelDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to show reader panel");
        }
        finally
        {
            _isSasayakiPanelOpen = false;
        }
    }

    private void RefreshSasayakiPanelControls()
    {
        _isRefreshingSasayakiPanel = true;

        try
        {
            var settings = CurrentSasayakiSettings;
            var playbackRate = _sasayakiPlayer?.PlaybackRate ?? _sasayakiVM.PlaybackRate;
            if (playbackRate <= 0)
                playbackRate = settings.PlaybackRate;

            SasayakiDelaySlider.Value = Math.Clamp(_sasayakiDelay, -2, 2);
            SasayakiSpeedSlider.Value = Math.Clamp(playbackRate, 0.5, 1.5);
            SasayakiPanelShowToggleSwitch.IsOn = settings.ReaderShowSasayakiToggle;
            SasayakiPanelAutoScrollToggleSwitch.IsOn = settings.AutoScroll;
            SasayakiPanelAutoPauseToggleSwitch.IsOn = settings.AutoPauseOnLookup;
            SasayakiPanelLightTextColorPicker.Color = ParseSasayakiColor(
                settings.LightTextColor,
                "#FF000000");
            SasayakiPanelLightBackgroundColorPicker.Color = ParseSasayakiColor(
                settings.LightBackgroundColor,
                "#6652C7FA");
            SasayakiPanelDarkTextColorPicker.Color = ParseSasayakiColor(
                settings.DarkTextColor,
                "#FFFFFFFF");
            SasayakiPanelDarkBackgroundColorPicker.Color = ParseSasayakiColor(
                settings.DarkBackgroundColor,
                "#6652C7FA");

            UpdateSasayakiPanelValueText();
            UpdateSasayakiChromeState();
        }
        finally
        {
            _isRefreshingSasayakiPanel = false;
        }
    }

    private void UpdateSasayakiPanelValueText()
    {
        if (SasayakiDelayValueText == null || SasayakiSpeedValueText == null)
            return;

        SasayakiDelayValueText.Text = $"{SasayakiDelaySlider.Value:+0.00;-0.00;0.00}s";
        SasayakiSpeedValueText.Text = $"{SasayakiSpeedSlider.Value:0.00}x";
    }

    private async void SasayakiDelaySlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        _sasayakiDelay = Math.Clamp(e.NewValue, -2, 2);
        UpdateSasayakiPanelValueText();
        await SaveSasayakiPlaybackAsync();
    }

    private async void SasayakiSpeedSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        var playbackRate = Math.Clamp(e.NewValue, 0.5, 1.5);
        if (_sasayakiPlayer != null)
            _sasayakiPlayer.PlaybackRate = playbackRate;

        _sasayakiVM.SetPlaybackRate(playbackRate);
        UpdateSasayakiPanelValueText();
        await SaveSasayakiPlaybackAsync();
    }

    private async void SasayakiPanelShowToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.ReaderShowSasayakiToggle = SasayakiPanelShowToggleSwitch.IsOn);
    }

    private async void SasayakiPanelAutoScrollToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.AutoScroll = SasayakiPanelAutoScrollToggleSwitch.IsOn);
    }

    private async void SasayakiPanelAutoPauseToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.AutoPauseOnLookup = SasayakiPanelAutoPauseToggleSwitch.IsOn);
    }

    private async void SasayakiPanelLightTextColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.LightTextColor = FormatSasayakiColor(args.NewColor),
            refreshCurrentHighlight: true);
    }

    private async void SasayakiPanelLightBackgroundColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.LightBackgroundColor = FormatSasayakiColor(args.NewColor),
            refreshCurrentHighlight: true);
    }

    private async void SasayakiPanelDarkTextColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.DarkTextColor = FormatSasayakiColor(args.NewColor),
            refreshCurrentHighlight: true);
    }

    private async void SasayakiPanelDarkBackgroundColorPicker_ColorChanged(
        ColorPicker sender,
        ColorChangedEventArgs args)
    {
        if (_isRefreshingSasayakiPanel)
            return;

        await SaveSasayakiSettingsAsync(
            settings => settings.DarkBackgroundColor = FormatSasayakiColor(args.NewColor),
            refreshCurrentHighlight: true);
    }

    private async Task SaveSasayakiSettingsAsync(
        Action<SasayakiSettings> update,
        bool refreshCurrentHighlight = false)
    {
        var settingsService = App.GetService<ISettingsService>();
        var settings = CloneSasayakiSettings(settingsService.Current.SasayakiSettings);
        update(settings);

        settingsService.Set(s => s.SasayakiSettings, settings);
        await settingsService.SaveAsync();
        RefreshSasayakiPanelControls();

        if (refreshCurrentHighlight)
            await RefreshCurrentSasayakiHighlightAsync();
    }

    private static SasayakiSettings CloneSasayakiSettings(SasayakiSettings settings) =>
        new()
        {
            EnableSasayaki = settings.EnableSasayaki,
            ReaderShowSasayakiToggle = settings.ReaderShowSasayakiToggle,
            SearchWindowSize = settings.SearchWindowSize,
            PlaybackRate = settings.PlaybackRate,
            AutoScroll = settings.AutoScroll,
            AutoPauseOnLookup = settings.AutoPauseOnLookup,
            ShowSkipControls = settings.ShowSkipControls,
            EnableSync = settings.EnableSync,
            LightTextColor = settings.LightTextColor,
            LightBackgroundColor = settings.LightBackgroundColor,
            DarkTextColor = settings.DarkTextColor,
            DarkBackgroundColor = settings.DarkBackgroundColor,
        };

    private async Task RefreshCurrentSasayakiHighlightAsync()
    {
        var match = _sasayakiNav.CurrentMatch;
        if (match != null && match.ChapterIndex == ViewModel.CurrentChapterIndex)
            await HighlightSasayakiCueAsync(match);
    }

    private static Color ParseSasayakiColor(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length == 6)
            text = "FF" + text;

        if (text.Length == 8
            && byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(text.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(a, r, g, b);
        }

        return ParseSasayakiColor(fallback, "#FF000000");
    }

    private static string FormatSasayakiColor(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private async void SasayakiLoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSasayakiPanelOpen)
            SasayakiPanelDialog.Hide();

        if (_epubBook == null || ViewModel.CurrentBook == null || App.MainWindow == null)
            return;
        if (!CurrentSasayakiSettings.EnableSasayaki)
            return;

        // Pick audiobook file
        var audioPicker = new Windows.Storage.Pickers.FileOpenPicker();
        audioPicker.FileTypeFilter.Add(".mp3");
        audioPicker.FileTypeFilter.Add(".m4b");
        audioPicker.FileTypeFilter.Add(".m4a");
        audioPicker.FileTypeFilter.Add(".wav");
        audioPicker.FileTypeFilter.Add(".flac");
        audioPicker.FileTypeFilter.Add(".ogg");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(audioPicker, hwnd);
        var audioFile = await audioPicker.PickSingleFileAsync();
        if (audioFile == null) return;

        // Pick SRT file
        var srtPicker = new Windows.Storage.Pickers.FileOpenPicker();
        srtPicker.FileTypeFilter.Add(".srt");
        srtPicker.FileTypeFilter.Add(".vtt");
        WinRT.Interop.InitializeWithWindow.Initialize(srtPicker, hwnd);
        var srtFile = await srtPicker.PickSingleFileAsync();
        if (srtFile == null) return;

        await LoadSasayakiAsync(audioFile.Path, srtFile.Path);
    }

    private async Task LoadSasayakiAsync(string audiobookPath, string srtPath)
    {
        try
        {
            UpdateSasayakiChromeVisibility();
            _sasayakiVM.SasayakiStatusText = "Loading...";

            // Parse SRT
            var cues = await _sasayakiParser.ParseAsync(srtPath);
            if (cues.Count == 0)
            {
                _sasayakiVM.SasayakiStatusText = "No cues found in SRT file";
                return;
            }

            // Match against book text
            var bookId = ViewModel.CurrentBook!.Id;
            var matchData = await _sasayakiMatcher.MatchAsync(
                _epubBook!, cues, bookId, audiobookPath, srtPath,
                App.GetService<ISettingsService>().Current.SasayakiSettings.SearchWindowSize);

            _sasayakiMatchData = matchData;
            _sasayakiNav.Load(matchData);
            _sasayakiVM.UpdateMatchStats(matchData);
            _sasayakiVM.IsLoaded = true;
            _sasayakiDelay = 0;

            // Save sidecar
            await SaveSasayakiSidecarAsync(matchData);

            // Load audio
            _sasayakiPlayer?.Dispose();
            _sasayakiPlayer = new SasayakiPlayer();
            _sasayakiPlayer.PositionChanged += OnSasayakiPositionChanged;
            _sasayakiPlayer.MediaEnded += OnSasayakiMediaEnded;
            _sasayakiPlayer.MediaFailed += OnSasayakiMediaFailed;
            await _sasayakiPlayer.LoadAsync(audiobookPath);
            _sasayakiPlayer.PlaybackRate = CurrentSasayakiSettings.PlaybackRate;
            _sasayakiVM.SetPlaybackRate(_sasayakiPlayer.PlaybackRate);

            _sasayakiVM.UpdatePlaybackState(false, false, 0, _sasayakiPlayer.DurationSeconds);
            UpdateSasayakiChromeVisibility();
            await SaveSasayakiPlaybackAsync(0);

            Log.Information(
                "[Sasayaki] Loaded: {CueCount} cues, {MatchCount} matched, {Unmatched} unmatched",
                cues.Count, matchData.Matches.Count, matchData.UnmatchedCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Sasayaki] Failed to load");
            _sasayakiVM.SasayakiStatusText = "Failed to load audiobook";
        }
    }

    private async Task LoadSasayakiSidecarAsync()
    {
        if (_epubBook == null || ViewModel.CurrentBook == null)
            return;
        if (!CurrentSasayakiSettings.EnableSasayaki)
            return;

        var bookRootPath = GetSasayakiBookRootPath();
        if (string.IsNullOrWhiteSpace(bookRootPath))
            return;

        try
        {
            var matchData = await SasayakiSidecarService.LoadMatchAsync(bookRootPath);
            if (matchData == null || !matchData.IsValid)
                return;

            _sasayakiMatchData = matchData;
            _sasayakiNav.Load(matchData);
            _sasayakiVM.UpdateMatchStats(matchData);
            _sasayakiVM.IsLoaded = true;

            // Load audio if file still exists
            if (File.Exists(matchData.AudiobookPath))
            {
                _sasayakiPlayer?.Dispose();
                _sasayakiPlayer = new SasayakiPlayer();
                _sasayakiPlayer.PositionChanged += OnSasayakiPositionChanged;
                _sasayakiPlayer.MediaEnded += OnSasayakiMediaEnded;
                _sasayakiPlayer.MediaFailed += OnSasayakiMediaFailed;
                await _sasayakiPlayer.LoadAsync(matchData.AudiobookPath);

                var playback = await SasayakiSidecarService.LoadPlaybackAsync(bookRootPath);
                ApplySasayakiPlayback(playback);
                UpdateSasayakiChromeVisibility();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to load sidecar");
        }
    }

    private async Task SaveSasayakiSidecarAsync(SasayakiMatchData data)
    {
        try
        {
            var bookRootPath = GetSasayakiBookRootPath(data.BookId);
            if (string.IsNullOrWhiteSpace(bookRootPath))
                return;

            await SasayakiSidecarService.SaveMatchAsync(bookRootPath, data);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to save sidecar");
        }
    }

    private async Task SaveSasayakiPlaybackAsync(double? positionOverride = null)
    {
        if (_sasayakiPlayer == null)
            return;

        var bookRootPath = GetSasayakiBookRootPath();
        if (string.IsNullOrWhiteSpace(bookRootPath))
            return;

        var position = Math.Max(0, positionOverride ?? _sasayakiPlayer.PositionSeconds);
        _sasayakiNav.UpdatePosition(position);
        _lastSasayakiPlaybackSavePosition = position;

        try
        {
            await SasayakiSidecarService.SavePlaybackAsync(
                bookRootPath,
                new SasayakiPlaybackData
                {
                    LastPosition = position,
                    Delay = _sasayakiDelay,
                    Rate = _sasayakiPlayer.PlaybackRate,
                    AudioBookmark = _sasayakiNav.CurrentCueIndex,
                });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to save playback sidecar");
        }
    }

    private string? GetSasayakiBookRootPath(string? bookId = null)
    {
        var currentBook = ViewModel.CurrentBook;
        if (currentBook != null
            && (string.IsNullOrEmpty(bookId) || string.Equals(currentBook.Id, bookId, StringComparison.Ordinal)))
        {
            return string.IsNullOrWhiteSpace(currentBook.ExtractedPath)
                ? AppDataHelper.GetNovelBookPath(currentBook.Id)
                : currentBook.ExtractedPath;
        }

        return string.IsNullOrWhiteSpace(bookId)
            ? null
            : AppDataHelper.GetNovelBookPath(bookId);
    }

    private async Task<SasayakiMiningAudioResult> RequestSasayakiMiningAudioAsync(
        SasayakiMatch match,
        string sentence,
        SasayakiMiningAudioRequest request,
        CancellationToken ct)
    {
        if (!request.CaptureAudioClip || _sasayakiMatchData == null)
            return new SasayakiMiningAudioResult();

        var audiobookPath = _sasayakiMatchData.AudiobookPath;
        if (string.IsNullOrWhiteSpace(audiobookPath) || !File.Exists(audiobookPath))
            return new SasayakiMiningAudioResult(ErrorMessage: "Audiobook file is missing.");

        var range = ResolveSasayakiAudioClipRange(match, sentence);
        if (range == null)
            return new SasayakiMiningAudioResult(ErrorMessage: "Unable to capture the Sasayaki audio clip.");

        var filename = SasayakiMiningMediaNaming.CreateAudioClipFilename(
            audiobookPath,
            range.Value.Start,
            range.Value.End);

        if (!string.IsNullOrWhiteSpace(request.DirectMediaDirectory))
        {
            _ = GenerateDirectSasayakiMiningAudioAsync(
                request.DirectMediaDirectory,
                filename,
                audiobookPath,
                range.Value);
            return new SasayakiMiningAudioResult(
                AudioClipTag: AnkiMediaMarkup.ForFieldPlaceholder(filename));
        }

        var mediaDir = Path.Combine(AppDataHelper.GetDataPath(), "SasayakiMining");
        Directory.CreateDirectory(mediaDir);
        var target = Path.Combine(mediaDir, filename);
        var exported = await App.GetService<IVideoMiningMediaExtractor>().ExportAudioClipAsync(
            audiobookPath,
            target,
            range.Value.Start,
            range.Value.End,
            ct);

        return string.IsNullOrWhiteSpace(exported)
            ? new SasayakiMiningAudioResult(ErrorMessage: "Unable to capture the Sasayaki audio clip.")
            : new SasayakiMiningAudioResult(AudioClipPath: exported);
    }

    private (TimeSpan Start, TimeSpan End)? ResolveSasayakiAudioClipRange(
        SasayakiMatch match,
        string sentence)
    {
        if (_sasayakiMatchData == null
            || match.CueIndex < 0
            || match.CueIndex >= _sasayakiMatchData.Cues.Count)
        {
            return null;
        }

        var startCueIndex = match.CueIndex;
        var endCueIndex = match.CueIndex;
        var normalizedSentence = NormalizeSasayakiMiningText(sentence);
        if (!string.IsNullOrWhiteSpace(normalizedSentence))
        {
            while (startCueIndex > 0
                   && IsAdjacentSasayakiCueInSentence(
                       startCueIndex - 1,
                       match.ChapterIndex,
                       normalizedSentence))
            {
                startCueIndex--;
            }

            while (endCueIndex + 1 < _sasayakiMatchData.Cues.Count
                   && IsAdjacentSasayakiCueInSentence(
                       endCueIndex + 1,
                       match.ChapterIndex,
                       normalizedSentence))
            {
                endCueIndex++;
            }
        }

        var startCue = _sasayakiMatchData.Cues[startCueIndex];
        var endCue = _sasayakiMatchData.Cues[endCueIndex];
        var delay = TimeSpan.FromSeconds(_sasayakiDelay);
        var start = TimeSpan.FromSeconds(startCue.StartTime) + delay;
        var end = TimeSpan.FromSeconds(endCue.EndTime) + delay;

        if (_sasayakiPlayer?.DurationSeconds > 0)
        {
            var duration = TimeSpan.FromSeconds(_sasayakiPlayer.DurationSeconds);
            if (end > duration)
                end = duration;
        }

        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (end <= start)
            return null;

        return (start, end);
    }

    private bool IsAdjacentSasayakiCueInSentence(
        int cueIndex,
        int chapterIndex,
        string normalizedSentence)
    {
        if (_sasayakiMatchData == null
            || cueIndex < 0
            || cueIndex >= _sasayakiMatchData.Cues.Count)
        {
            return false;
        }

        var match = _sasayakiMatchData.Matches.FirstOrDefault(item => item.CueIndex == cueIndex);
        if (match == null || match.ChapterIndex != chapterIndex)
            return false;

        var cueText = NormalizeSasayakiMiningText(_sasayakiMatchData.Cues[cueIndex].Text);
        return cueText.Length > 0
            && normalizedSentence.Contains(cueText, StringComparison.Ordinal);
    }

    private async Task GenerateDirectSasayakiMiningAudioAsync(
        string mediaDirectory,
        string filename,
        string audiobookPath,
        (TimeSpan Start, TimeSpan End) range)
    {
        try
        {
            Directory.CreateDirectory(mediaDirectory);
            var tempDir = Path.Combine(AppDataHelper.GetDataPath(), "SasayakiMining", "Temp");
            Directory.CreateDirectory(tempDir);
            var temp = Path.Combine(tempDir, $".{Guid.NewGuid():N}-{filename}");
            var exported = await App.GetService<IVideoMiningMediaExtractor>().ExportAudioClipAsync(
                audiobookPath,
                temp,
                range.Start,
                range.End);
            if (!string.IsNullOrWhiteSpace(exported) && File.Exists(exported))
                ReplaceFile(exported, Path.Combine(mediaDirectory, filename));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to generate direct mining audio");
        }
    }

    private static string NormalizeSasayakiMiningText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch)
                && !char.IsPunctuation(ch)
                && !char.IsSymbol(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        File.Delete(sourcePath);
    }

    private void ApplySasayakiPlayback(SasayakiPlaybackData playback)
    {
        if (_sasayakiPlayer == null)
            return;

        _sasayakiDelay = playback.Delay;
        _sasayakiPlayer.PlaybackRate = playback.Rate;
        _sasayakiVM.SetPlaybackRate(playback.Rate);

        var position = Math.Max(0, playback.LastPosition);
        if (position > 0)
            _sasayakiPlayer.Seek(position);

        _sasayakiNav.UpdatePosition(position);
        _lastSasayakiPlaybackSavePosition = position;

        var currentCue = _sasayakiNav.CurrentCue;
        _sasayakiVM.UpdateCurrentCue(currentCue);
        _sasayakiVM.UpdatePlaybackState(false, false, position, _sasayakiPlayer.DurationSeconds);
        UpdateSasayakiChromeState();
    }

    private async void SasayakiPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await ToggleSasayakiPlaybackAsync();
    }

    private async Task ToggleSasayakiPlaybackAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        _sasayakiStopPlaybackAtSeconds = null;

        if (_sasayakiPlayer!.IsPlaying)
        {
            _sasayakiPlayer.Pause();
            _sasayakiVM.UpdatePlaybackState(false, true,
                _sasayakiPlayer.PositionSeconds, _sasayakiPlayer.DurationSeconds);
            UpdateSasayakiChromeState();
            await SaveSasayakiPlaybackAsync();
            return;
        }

        if (_sasayakiPlayer.IsPaused)
        {
            _sasayakiPlayer.Resume();
        }
        else
        {
            _sasayakiPlayer.Play();
        }

        _sasayakiVM.UpdatePlaybackState(true, false,
            _sasayakiPlayer.PositionSeconds,
            _sasayakiPlayer.DurationSeconds);
        UpdateSasayakiChromeState();
    }

    private async void SasayakiPrevCue_Click(object sender, RoutedEventArgs e)
    {
        await GoToPreviousSasayakiCueAsync();
    }

    private async Task GoToPreviousSasayakiCueAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var prevIndex = _sasayakiNav.GetMatchedCueIndexBefore(_sasayakiPlayer!.PositionSeconds);
        if (prevIndex.HasValue)
            await SeekToSasayakiCueAsync(prevIndex.Value);
    }

    private async void SasayakiNextCue_Click(object sender, RoutedEventArgs e)
    {
        await GoToNextSasayakiCueAsync();
    }

    private async void SasayakiReplayCue_Click(object sender, RoutedEventArgs e)
    {
        await ReplayCurrentSasayakiCueAsync();
    }

    private async void SasayakiJumpCue_Click(object sender, RoutedEventArgs e)
    {
        await JumpToCurrentSasayakiCueAsync();
    }

    private async Task GoToNextSasayakiCueAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var nextIndex = _sasayakiNav.GetMatchedCueIndexAfter(_sasayakiPlayer!.PositionSeconds);
        if (nextIndex.HasValue)
            await SeekToSasayakiCueAsync(nextIndex.Value);
    }

    private async Task ReplayCurrentSasayakiCueAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var match = ResolveCurrentSasayakiMatch();
        if (match == null)
            return;

        await ReplaySasayakiMatchAsync(match);
    }

    private async Task ReplaySasayakiMatchAsync(SasayakiMatch match)
    {
        if (!CanHandleSasayakiShortcut()
            || _sasayakiMatchData == null
            || match.CueIndex < 0
            || match.CueIndex >= _sasayakiMatchData.Cues.Count)
        {
            return;
        }

        var cue = _sasayakiMatchData.Cues[match.CueIndex];
        await SeekToSasayakiCueAsync(
            match.CueIndex,
            startPlayback: true,
            stopPlaybackAtSeconds: cue.EndTime);
    }

    private async Task JumpToCurrentSasayakiCueAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var match = ResolveCurrentSasayakiMatch();
        if (match == null)
            return;

        await JumpToSasayakiMatchAsync(match);
    }

    private async Task JumpToAndPlaySasayakiMatchAsync(SasayakiMatch match)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        await JumpToSasayakiMatchAsync(match);
        await PlaySasayakiMatchFromCueAsync(match);
    }

    private async Task PlaySasayakiMatchFromCueAsync(SasayakiMatch match)
    {
        if (!CanHandleSasayakiShortcut()
            || _sasayakiMatchData == null
            || match.CueIndex < 0
            || match.CueIndex >= _sasayakiMatchData.Cues.Count)
        {
            return;
        }

        await SeekToSasayakiCueAsync(
            match.CueIndex,
            startPlayback: true);
    }

    private async Task JumpToSasayakiMatchAsync(SasayakiMatch match)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var target = ResolveSasayakiJumpTarget(match);
        if (target == null)
            return;

        var sameChapter = target.ChapterIndex == ViewModel.CurrentChapterIndex;
        if (await NavigateProgrammaticallyAsync(
            target.ChapterIndex,
            target.ChapterProgress,
            recordHistory: false)
            && sameChapter)
        {
            await HighlightSasayakiCueAsync(match);
        }
    }

    private async Task SeekToSasayakiCueAsync(
        int cueIndex,
        bool startPlayback = false,
        double? stopPlaybackAtSeconds = null)
    {
        if (_sasayakiMatchData == null
            || _sasayakiPlayer == null
            || cueIndex < 0
            || cueIndex >= _sasayakiMatchData.Cues.Count)
        {
            return;
        }

        var cue = _sasayakiMatchData.Cues[cueIndex];
        _sasayakiStopPlaybackAtSeconds = stopPlaybackAtSeconds;
        _sasayakiPlayer.Seek(cue.StartTime);
        _sasayakiNav.SeekToCue(cueIndex);
        _sasayakiVM.UpdateCurrentCue(cue);

        var duration = _sasayakiPlayer.DurationSeconds;

        if (startPlayback)
            _sasayakiPlayer.Play();

        _sasayakiVM.UpdatePlaybackState(
            _sasayakiPlayer.IsPlaying,
            _sasayakiPlayer.IsPaused,
            cue.StartTime,
            duration);
        UpdateSasayakiChromeState();

        await SaveSasayakiPlaybackAsync(cue.StartTime);

        var match = _sasayakiNav.GetMatchForCue(cueIndex);
        if (match != null && match.ChapterIndex == ViewModel.CurrentChapterIndex)
            _ = HighlightSasayakiCueAsync(match);
    }

    private int? ResolveCurrentSasayakiCueIndex()
    {
        if (_sasayakiMatchData == null)
            return null;

        if (_sasayakiNav.CurrentCueIndex >= 0
            && _sasayakiNav.CurrentCueIndex < _sasayakiMatchData.Cues.Count)
        {
            return _sasayakiNav.CurrentCueIndex;
        }

        if (_sasayakiPlayer == null)
            return null;

        _sasayakiNav.UpdatePosition(_sasayakiPlayer.PositionSeconds);
        return _sasayakiNav.CurrentCueIndex >= 0
            && _sasayakiNav.CurrentCueIndex < _sasayakiMatchData.Cues.Count
            ? _sasayakiNav.CurrentCueIndex
            : null;
    }

    private SasayakiMatch? ResolveCurrentSasayakiMatch()
    {
        var cueIndex = ResolveCurrentSasayakiCueIndex();
        return cueIndex.HasValue
            ? _sasayakiNav.GetMatchForCue(cueIndex.Value)
            : null;
    }

    private ReaderHighlightJumpTarget? ResolveSasayakiJumpTarget(SasayakiMatch match)
    {
        if (_chapterCharacterCounts.Count == 0
            || match.ChapterIndex < 0
            || match.ChapterIndex >= _chapterCharacterCounts.Count)
        {
            return null;
        }

        var chapterCount = Math.Max(1, _chapterCharacterCounts[match.ChapterIndex]);
        var progress = Math.Clamp(match.StartCodePoint / (double)chapterCount, 0, 1);
        return new ReaderHighlightJumpTarget(match.ChapterIndex, progress);
    }

    private async void SasayakiSkipBack_Click(object sender, RoutedEventArgs e)
    {
        await SkipSasayakiAsync(-15);
    }

    private async void SasayakiSkipForward_Click(object sender, RoutedEventArgs e)
    {
        await SkipSasayakiAsync(15);
    }

    private async Task SkipSasayakiAsync(double deltaSeconds)
    {
        if (_sasayakiPlayer == null)
            return;

        _sasayakiStopPlaybackAtSeconds = null;
        var duration = Math.Max(0, _sasayakiPlayer.DurationSeconds);
        var position = Math.Clamp(_sasayakiPlayer.PositionSeconds + deltaSeconds, 0, duration);
        _sasayakiPlayer.Seek(position);
        _sasayakiNav.UpdatePosition(position);

        var currentCue = _sasayakiNav.CurrentCue;
        _sasayakiVM.UpdateCurrentCue(currentCue);
        _sasayakiVM.UpdatePlaybackState(
            _sasayakiPlayer.IsPlaying,
            _sasayakiPlayer.IsPaused,
            position,
            duration);
        UpdateSasayakiChromeState();

        await SaveSasayakiPlaybackAsync(position);
    }

    private void OnSasayakiPositionChanged(object? sender, double seconds)
    {
        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (_sasayakiPlayer == null)
                return;

            var duration = _sasayakiPlayer.DurationSeconds;
            _sasayakiVM.UpdatePlaybackState(
                _sasayakiPlayer.IsPlaying, _sasayakiPlayer.IsPaused,
                seconds, duration);

            if (_sasayakiStopPlaybackAtSeconds.HasValue
                && seconds >= _sasayakiStopPlaybackAtSeconds.Value)
            {
                _sasayakiStopPlaybackAtSeconds = null;
                _sasayakiPlayer.Pause();
                _sasayakiVM.UpdatePlaybackState(false, true, seconds, duration);
                UpdateSasayakiChromeState();
                _ = SaveSasayakiPlaybackAsync(seconds);
                return;
            }

            // Update cue navigation
            _sasayakiNav.UpdatePosition(seconds);
            var currentCue = _sasayakiNav.CurrentCue;
            _sasayakiVM.UpdateCurrentCue(currentCue);
            UpdateSasayakiChromeState();

            if (Math.Abs(seconds - _lastSasayakiPlaybackSavePosition) >= 1)
                _ = SaveSasayakiPlaybackAsync(seconds);

            // Highlight current cue if in same chapter
            if (currentCue != null)
            {
                var match = _sasayakiNav.CurrentMatch;
                if (match != null && match.CueIndex != _lastHighlightedCue)
                {
                    _lastHighlightedCue = match.CueIndex;
                    if (match.ChapterIndex == ViewModel.CurrentChapterIndex)
                    {
                        _ = HighlightSasayakiCueAsync(match);
                    }
                    else if (CurrentSasayakiSettings.AutoScroll)
                    {
                        LoadChapterForSasayakiAutoScroll(match);
                    }
                    else
                    {
                        _ = ClearSasayakiHighlightAsync();
                    }
                }
            }
        });
    }

    private void OnSasayakiMediaEnded(object? sender, EventArgs e)
    {
        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            _sasayakiVM.UpdatePlaybackState(false, false,
                _sasayakiPlayer?.PositionSeconds ?? 0,
                _sasayakiPlayer?.DurationSeconds ?? 0);
            UpdateSasayakiChromeState();
            _ = SaveSasayakiPlaybackAsync(_sasayakiPlayer?.PositionSeconds ?? 0);
        });
    }

    private void OnSasayakiMediaFailed(object? sender, string error)
    {
        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            _sasayakiVM.SasayakiStatusText = $"Playback error: {error}";
            _sasayakiVM.UpdatePlaybackState(false, false, 0, 0);
            UpdateSasayakiChromeState();
        });
    }

    private async Task HighlightSasayakiCueAsync(
        SasayakiMatch match,
        bool allowAutoScroll = true)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        var generation = Interlocked.Increment(ref _sasayakiHighlightGeneration);

        try
        {
            var settings = CurrentSasayakiSettings;
            var useDarkColors = ActualTheme == ElementTheme.Dark;
            var textColor = useDarkColors ? settings.DarkTextColor : settings.LightTextColor;
            var backgroundColor = useDarkColors
                ? settings.DarkBackgroundColor
                : settings.LightBackgroundColor;
            var textColorJson = JsonSerializer.Serialize(textColor);
            var backgroundColorJson = JsonSerializer.Serialize(backgroundColor);
            var autoScrollJson = JsonSerializer.Serialize(
                allowAutoScroll && settings.AutoScroll);
            var progressJson = await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $$"""
                (() => {
                  const generation = {{generation}};
                  const currentGeneration = Number(window.__hoshiSasayakiHighlightGeneration || 0);
                  if (currentGeneration > generation) return null;
                  window.__hoshiSasayakiHighlightGeneration = generation;
                  if (!window.hoshiSasayaki?.setColors || !window.hoshiSasayaki?.highlightCue) return null;
                  window.hoshiSasayaki.setColors({{textColorJson}}, {{backgroundColorJson}});
                  return window.hoshiSasayaki.highlightCue({{match.StartCodePoint}}, {{match.Length}}, {{autoScrollJson}});
                })();
                """);
            if (allowAutoScroll
                && generation == Volatile.Read(ref _sasayakiHighlightGeneration))
                TryApplySasayakiAutoScrollProgress(progressJson);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Sasayaki] Failed to highlight cue");
        }
    }

    private bool TryApplySasayakiAutoScrollProgress(string? progressJson)
    {
        if (string.IsNullOrWhiteSpace(progressJson)
            || progressJson == "null"
            || progressJson == "undefined")
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(progressJson);
            if (document.RootElement.ValueKind != JsonValueKind.Number
                || !document.RootElement.TryGetDouble(out var progress)
                || !double.IsFinite(progress))
            {
                return false;
            }

            progress = Math.Clamp(progress, 0, 1);
            if (!ReaderStatisticsEventClassifier.HasProgressMovement(
                ViewModel.Progress,
                progress))
            {
                return false;
            }

            ViewModel.StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);
            _currentProgress = progress;
            ViewModel.UpdateProgress(progress);
            RefreshReaderDisplayChrome();
            ViewModel.SaveProgressDebounced();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool LoadChapterForSasayakiAutoScroll(SasayakiMatch match)
    {
        var target = ResolveSasayakiJumpTarget(match);
        if (target == null)
            return false;

        if (target.ChapterIndex == ViewModel.CurrentChapterIndex
            && !ReaderStatisticsEventClassifier.HasProgressMovement(
                ViewModel.Progress,
                target.ChapterProgress))
        {
            return false;
        }

        ViewModel.StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);
        LoadChapter(target.ChapterIndex, target.ChapterProgress);
        ViewModel.SaveProgressDebounced();
        return true;
    }

    private async Task ClearSasayakiHighlightAsync()
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        try
        {
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                "window.hoshiSasayaki.clearHighlight();");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Sasayaki] Failed to clear highlight");
        }
    }
}
