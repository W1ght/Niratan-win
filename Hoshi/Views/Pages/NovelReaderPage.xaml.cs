using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using Hoshi.Services.Settings;
using Hoshi.Views.Dialogs;
using Serilog;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Shortcuts;
using Hoshi.Models.Settings;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.UI;
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
    private const double ReaderPanelLeft = 70;
    private const double ReaderPanelTop = 148;
    private const double ReaderPanelWidth = 420;
    private const double ReaderPanelBottomMargin = 96;

    public NovelReaderPageViewModel ViewModel { get; set; }
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
    private int? _pendingChapterIndex;
    private readonly List<ReaderNavigationHistoryEntry> _readerBackHistory = [];
    private readonly List<ReaderNavigationHistoryEntry> _readerForwardHistory = [];
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _searchCts;
    private long _searchRequestVersion;
    private DictionaryPopupOverlay? _popupOverlay;
    private readonly SemaphoreSlim _lookupSemaphore = new(1, 1);
    private long _lookupRequestVersion;
    private bool _readerFocusMode;

    private sealed record ReaderNavigationHistoryEntry(int ChapterIndex, double Progress);

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
    private bool _sasayakiSeeking;
    private int _lastHighlightedCue = -1;
    private double _sasayakiDelay;
    private double _lastSasayakiPlaybackSavePosition = -1;
    private double? _sasayakiStopPlaybackAtSeconds;

    private static ISasayakiSidecarService SasayakiSidecarService =>
        App.GetService<ISasayakiSidecarService>();

    private static SasayakiSettings CurrentSasayakiSettings =>
        App.GetService<ISettingsService>().Current.SasayakiSettings;

    private static NovelStatisticsSettings CurrentStatisticsSettings =>
        App.GetService<ISettingsService>().Current.StatisticsSettings;

    public NovelReaderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelReaderPageViewModel>();
        ViewModel.PropertyChanged += OnReaderViewModelPropertyChanged;
        DataContext = ViewModel;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ApplyReaderShortcutLabels();
    }

    private void ApplyReaderShortcutLabels()
    {
        ApplyShortcutLabel(
            NovelReaderBackButton,
            ReaderShortcutActions.Close.Title,
            ReaderShortcutActions.Close.DefaultShortcut.Label);
        ApplyShortcutLabel(
            NovelReaderSearchButton,
            "Search",
            "/");
        ApplyShortcutLabel(
            NovelReaderStatisticsButton,
            ReaderShortcutActions.ToggleStatistics.Title,
            ReaderShortcutActions.ToggleStatistics.DefaultShortcut.Label);
        ApplyShortcutLabel(
            SasayakiPreviousCueButton,
            SasayakiShortcutActions.PreviousCue.Title,
            SasayakiShortcutActions.PreviousCue.DefaultShortcut.Label);
        ApplyShortcutLabel(
            SasayakiPlayPauseButton,
            SasayakiShortcutActions.PlayPause.Title,
            SasayakiShortcutActions.PlayPause.DefaultShortcut.Label);
        ApplyShortcutLabel(
            SasayakiNextCueButton,
            SasayakiShortcutActions.NextCue.Title,
            SasayakiShortcutActions.NextCue.DefaultShortcut.Label);
    }

    private static void ApplyShortcutLabel(
        Control control,
        string actionTitle,
        string shortcutLabel)
    {
        var text = $"{actionTitle} ({shortcutLabel})";
        ToolTipService.SetToolTip(control, text);
        AutomationProperties.SetHelpText(control, text);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NovelReaderNavigationArgs args)
        {
            await ViewModel.InitializeAsync(args);
            await InitializeReaderAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
        _reloadCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        var readerSettings = App.GetService<IReaderSettingsService>();
        readerSettings.SettingChanged -= OnReaderSettingChanged;
        App.GetService<ISettingsService>().SettingChanged -= OnAppSettingChanged;
        ViewModel.PropertyChanged -= OnReaderViewModelPropertyChanged;

        if (NovelWebView.CoreWebView2 != null)
        {
            NovelWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            NovelWebView.CoreWebView2.DOMContentLoaded -= OnDomContentLoaded;
            NovelWebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
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
        _epubBook = parser.Parse(book.FilePath, book.ExtractedPath);
        _chapterCharacterCounts = CalculateChapterCharacterCounts(_epubBook);
        _chapterStartCharacterCounts = CalculateChapterStartCharacterCounts(_chapterCharacterCounts);
        _searchDocument = null;
        _readerBackHistory.Clear();
        _readerForwardHistory.Clear();
        UpdateReaderHistoryButtons();
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
        UpdateStatisticsButtonVisibility();
        RefreshReaderDisplayChrome();
        RefreshReaderStatisticsChrome();
        StartStatisticsForAutostart(StatisticsAutostartMode.On);
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

        await NovelWebView.EnsureCoreWebView2Async();

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

        NovelWebView.CoreWebView2.ProcessFailed += (_, args) =>
            Log.Error("[NovelReader] WebView2 ProcessFailed: Kind={Kind}, ExitCode={ExitCode}, Reason={Reason}",
                args.ProcessFailedKind, args.ExitCode, args.Reason);

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
        NovelWebView.CoreWebView2.NavigationCompleted += (s, a) =>
            Log.Information(
                "[NovelReader] Navigation completed: {Status}, IsSuccess: {Success}",
                a.WebErrorStatus,
                a.IsSuccess
            );
        NovelWebView.CoreWebView2.FrameNavigationStarting += (s, a) =>
            Log.Information("[NovelReader] Frame navigation: {Uri}", a.Uri);

        var readerSettings = App.GetService<IReaderSettingsService>();
        readerSettings.SettingChanged += OnReaderSettingChanged;
        App.GetService<ISettingsService>().SettingChanged += OnAppSettingChanged;

        _ = EnsurePopupOverlay().PrewarmAsync(XamlRoot);

        UpdateSasayakiBarVisibility();
        if (CurrentSasayakiSettings.EnableSasayaki)
            _ = LoadSasayakiSidecarAsync();

        Log.Information("[NovelReader] Loading chapter {Index}", initialChapterIndex);
        LoadChapter(initialChapterIndex);
    }

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

    private void ReaderCloseKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.BackToLibraryCommand.Execute(null);
    }

    private void ReaderFocusKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleReaderFocusMode();
    }

    private async void ReaderStatisticsKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ToggleStatisticsTrackingAsync();
    }

    private async void NovelReaderPage_CharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs args)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        switch ((char)args.Character)
        {
            case '[':
                args.Handled = true;
                await GoToPreviousSasayakiCueAsync();
                break;
            case ']':
                args.Handled = true;
                await GoToNextSasayakiCueAsync();
                break;
        }
    }

    private async void SasayakiPlayPauseKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        args.Handled = true;
        await ToggleSasayakiPlaybackAsync();
    }

    private async void SasayakiReplayCueKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        args.Handled = true;
        await ReplayCurrentSasayakiCueAsync();
    }

    private async void SasayakiJumpCueKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!CanHandleSasayakiShortcut())
            return;

        args.Handled = true;
        await JumpToCurrentSasayakiCueAsync();
    }

    private bool CanHandleSasayakiShortcut() =>
        CurrentSasayakiSettings.EnableSasayaki
        && _sasayakiPlayer != null
        && _sasayakiMatchData?.IsValid == true;

    private void OnAppSettingChanged(object? sender, Models.DTO.SettingsChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            OnReaderSettingChanged(sender, e);
            return;
        }

        if (e.PropertyName == nameof(AppSettings.SasayakiSettings))
        {
            UpdateSasayakiBarVisibility();
            if (CurrentSasayakiSettings.EnableSasayaki && _sasayakiMatchData == null)
                _ = LoadSasayakiSidecarAsync();
            return;
        }

        if (e.PropertyName == nameof(AppSettings.StatisticsSettings))
        {
            UpdateStatisticsButtonVisibility();
            RefreshReaderStatisticsChrome();
            if (!CurrentStatisticsSettings.EnableStatistics && ViewModel.IsStatisticsTracking)
                _ = ViewModel.StopStatisticsTrackingAsync();
            else
                StartStatisticsForAutostart(StatisticsAutostartMode.On);
        }
    }

    private void UpdateSasayakiBarVisibility()
    {
        var settings = CurrentSasayakiSettings;
        var shouldShow = settings.EnableSasayaki
            && (settings.ReaderShowSasayakiToggle || _sasayakiVM.IsLoaded || _sasayakiMatchData != null);

        SasayakiBar.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        SasayakiSkipBackButton.Visibility = settings.ShowSkipControls
            ? Visibility.Visible
            : Visibility.Collapsed;
        SasayakiSkipForwardButton.Visibility = settings.ShowSkipControls
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            parts.Add(ViewModel.StatisticsSessionTimeText);

        NovelReaderStatisticsText.Text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        NovelReaderStatisticsText.Visibility = parts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StartStatisticsForAutostart(StatisticsAutostartMode trigger)
    {
        var settings = CurrentStatisticsSettings;
        if (!settings.EnableStatistics || ViewModel.IsStatisticsTracking)
            return;
        if (settings.AutostartMode != trigger)
            return;

        ViewModel.StartStatisticsTracking();
        RefreshStatisticsPanel();
    }

    private async Task ToggleStatisticsTrackingAsync()
    {
        if (!CurrentStatisticsSettings.EnableStatistics)
            return;

        if (ViewModel.IsStatisticsTracking)
            await ViewModel.StopStatisticsTrackingAsync();
        else
            ViewModel.StartStatisticsTracking();

        RefreshStatisticsPanel();
    }

    private void LoadChapter(int index, double? progressOverride = null)
    {
        if (_epubBook == null || NovelWebView.CoreWebView2 == null)
            return;

        if (index < 0 || index >= _epubBook.Chapters.Count)
            return;

        if (progressOverride.HasValue)
        {
            _currentProgress = Math.Clamp(progressOverride.Value, 0, 1);
        }
        else
        {
            // Restore progress when reloading the same chapter (e.g. settings change).
            // Forward navigation: start at 0. Backward: start at end (progress 1).
            if (_previousChapterIndex < 0)
                _currentProgress = ViewModel.Progress;
            else if (index == _previousChapterIndex)
                _currentProgress = ViewModel.Progress;
            else if (index > _previousChapterIndex)
                _currentProgress = 0;
            else
                _currentProgress = 1;
        }
        _previousChapterIndex = index;

        ViewModel.SetChapter(index, _epubBook.Chapters.Count);
        ViewModel.UpdateProgress(_currentProgress);
        RefreshReaderDisplayChrome();

        var chapter = _epubBook.Chapters[index];
        var relativePath = Path
            .GetRelativePath(_epubBook.ContainerDirectory, chapter.Href)
            .Replace('\\', '/');
        var url = $"https://{NovelBookHostName}/{relativePath}";

        // Defer navigation when the WebView hasn't been laid out yet.
        if (NovelWebView.ActualWidth <= 0 || NovelWebView.ActualHeight <= 0)
        {
            Log.Information(
                "[NovelReader] Viewport not ready, deferring chapter {Index}",
                index);
            _pendingChapterIndex = index;
            return;
        }

        Log.Information(
            "[NovelReader] Navigating to chapter {Index}: {Url} (progress={Progress:F3})",
            index,
            url,
            _currentProgress);

        var readerSettings = App.GetService<Services.Settings.IReaderSettingsService>();
        var appTheme = App.GetService<ISettingsService>().Current.Theme;
        _currentReaderCss = NovelReaderContentStyles.GenerateCss(readerSettings.Current, appTheme);

        NovelWebView.DefaultBackgroundColor = ARGBToWindowsColor(
            readerSettings.Current.BackgroundColor(appTheme));

        // Hide WebView until chapterReady to prevent FOUC.
        NovelWebView.Opacity = 0;
        NovelWebView.CoreWebView2.Navigate(url);
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
            var chapterInfo = JsonSerializer.Serialize(new
            {
                index = ViewModel.CurrentChapterIndex,
                totalChapters = _epubBook.Chapters.Count,
                progress = _currentProgress
            });
            await sender.ExecuteScriptAsync(
                $"window.__hoshiChapterInfo = {chapterInfo};");
            var highlightsJson = ViewModel.GetCurrentChapterHighlightsJson() ?? "[]";
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
                && match.ChapterIndex == ViewModel.CurrentChapterIndex)
            {
                await HighlightSasayakiCueAsync(match);
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
        }
    }

    private void OnWebViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pendingChapterIndex.HasValue
            && NovelWebView.ActualWidth > 0
            && NovelWebView.ActualHeight > 0)
        {
            var index = _pendingChapterIndex.Value;
            _pendingChapterIndex = null;
            Log.Information("[NovelReader] Viewport ready, loading deferred chapter {Index}", index);
            LoadChapter(index);
        }
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
            var version = root.GetProperty("version").GetInt32();
            var type = root.GetProperty("type").GetString();

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
                    NovelWebView.Opacity = 1;
                    Log.Information("[NovelReader] Chapter ready, capturing artifacts");
                    await CaptureReaderArtifactsAsync(
                        root.GetProperty("payload").GetRawText()
                    );
                    break;
                case "restoreCompleted":
                    Log.Information("[NovelReader] Restore completed");
                    break;
                case "pageChanged":
                    var payload = root.GetProperty("payload");
                    var progress = payload.GetProperty("progress").GetDouble();
                    var direction = payload.GetProperty("direction").GetString();
                    var result = payload.GetProperty("result").GetString();
                    ViewModel.UpdateProgress(progress);
                    _currentProgress = progress;
                    StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);
                    ViewModel.SaveProgressDebounced();

                    Log.Information(
                        "[NovelReader] Page changed: direction={Dir}, result={Result}, progress={Progress:F3}",
                        direction,
                        result,
                        progress
                    );

                    if (result == "limit")
                    {
                        if (direction == "forward")
                            LoadChapter(ViewModel.CurrentChapterIndex + 1);
                        else if (direction == "backward")
                            LoadChapter(ViewModel.CurrentChapterIndex - 1);
                    }
                    else if (payload.TryGetProperty("state", out var pageState))
                    {
                        await CaptureReaderArtifactsAsync(pageState.GetRawText());
                    }
                    break;
                case "error":
                    var message = root
                        .GetProperty("payload")
                        .GetProperty("message")
                        .GetString();
                    Log.Error("[NovelReader] Bridge error: {Error}", message);
                    App.GetService<INotificationService>()
                        .ShowError(message ?? "Reader host error.", "Novel reader");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelReader] Failed to process WebMessage");
            App.GetService<INotificationService>()
                .ShowError(ex.Message, "Novel reader");
        }
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
            var appTheme = App.GetService<ISettingsService>().Current.Theme;

            var popupOverlay = EnsurePopupOverlay();
            _ = popupOverlay.PrewarmAsync(XamlRoot);
            PauseSasayakiForLookup();
            phaseSw.Restart();
            await popupOverlay.ShowLookupAsync(
                results, styleDict, dictionaryDisplaySettings,
                windowX, windowY, width, height,
                XamlRoot, isVertical, appTheme,
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
        SasayakiPlayIcon.Glyph = "\uE768";
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

        var message = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(
            ViewModel.CurrentChapterIndex,
            _epubBook.Chapters.Count
        );
        Log.Information("[NovelReader] Sending setChapter: {Msg}", message);
        NovelWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private async System.Threading.Tasks.Task SendRestoreProgressMessageAsync(double progress)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        var message = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(progress);
        Log.Information("[NovelReader] Sending restoreProgress: {Msg}", message);
        NovelWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void ChapterListButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null) return;

        ReaderAppearancePanelPopup.IsOpen = false;
        ReaderSearchPanelPopup.IsOpen = false;
        ReaderHighlightsPanelPopup.IsOpen = false;
        ReaderStatisticsPanelPopup.IsOpen = false;
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
        OpenReaderPanel(ReaderChapterPanelPopup, ReaderChapterPanelRoot);
        ReaderChapterListContent.SelectCurrentChapter();
    }

    private void AppearanceButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ReaderChapterPanelPopup.IsOpen = false;
        ReaderSearchPanelPopup.IsOpen = false;
        ReaderHighlightsPanelPopup.IsOpen = false;
        ReaderStatisticsPanelPopup.IsOpen = false;
        OpenReaderPanel(ReaderAppearancePanelPopup, ReaderAppearancePanelRoot);
    }

    private void HighlightsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null)
            return;

        ReaderChapterPanelPopup.IsOpen = false;
        ReaderSearchPanelPopup.IsOpen = false;
        ReaderAppearancePanelPopup.IsOpen = false;
        ReaderStatisticsPanelPopup.IsOpen = false;
        RefreshHighlightList();
        OpenReaderPanel(ReaderHighlightsPanelPopup, ReaderHighlightsPanelRoot);
    }

    private async void StatisticsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ReaderChapterPanelPopup.IsOpen = false;
        ReaderSearchPanelPopup.IsOpen = false;
        ReaderHighlightsPanelPopup.IsOpen = false;
        ReaderAppearancePanelPopup.IsOpen = false;

        await ViewModel.FlushStatisticsAsync();
        RefreshStatisticsPanel();
        OpenReaderPanel(ReaderStatisticsPanelPopup, ReaderStatisticsPanelRoot);
    }

    private async void StatisticsStartStopButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ToggleStatisticsTrackingAsync();
    }

    private void RefreshStatisticsPanel()
    {
        ReaderStatisticsStartStopIcon.Glyph = ViewModel.IsStatisticsTracking ? "\uE769" : "\uE768";
        RefreshReaderStatisticsChrome();
        Bindings.Update();
    }

    private async void ReaderHistoryBackButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RestoreReaderHistoryEntryAsync(_readerBackHistory, _readerForwardHistory);
    }

    private async void ReaderHistoryForwardButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RestoreReaderHistoryEntryAsync(_readerForwardHistory, _readerBackHistory);
    }

    private void RecordReaderHistoryEntry()
    {
        if (_epubBook == null)
            return;

        _readerBackHistory.Add(new ReaderNavigationHistoryEntry(
            ViewModel.CurrentChapterIndex,
            Math.Clamp(_currentProgress, 0, 1)));
        _readerForwardHistory.Clear();
        UpdateReaderHistoryButtons();
    }

    private async Task RestoreReaderHistoryEntryAsync(
        List<ReaderNavigationHistoryEntry> source,
        List<ReaderNavigationHistoryEntry> destination)
    {
        if (source.Count == 0)
            return;

        await ViewModel.SaveProgressNowAsync();

        var target = source[^1];
        source.RemoveAt(source.Count - 1);
        destination.Add(new ReaderNavigationHistoryEntry(
            ViewModel.CurrentChapterIndex,
            Math.Clamp(_currentProgress, 0, 1)));

        await RestoreReaderHistoryTargetAsync(target);
        UpdateReaderHistoryButtons();
    }

    private async Task RestoreReaderHistoryTargetAsync(ReaderNavigationHistoryEntry target)
    {
        var progress = Math.Clamp(target.Progress, 0, 1);
        if (target.ChapterIndex == ViewModel.CurrentChapterIndex)
        {
            _currentProgress = progress;
            ViewModel.UpdateProgress(progress);
            await SendRestoreProgressMessageAsync(progress);
            ViewModel.SaveProgressDebounced();
            return;
        }

        LoadChapter(target.ChapterIndex, progress);
        ViewModel.SaveProgressDebounced();
    }

    private void UpdateReaderHistoryButtons()
    {
        NovelReaderHistoryBackButton.IsEnabled = _readerBackHistory.Count > 0;
        NovelReaderHistoryForwardButton.IsEnabled = _readerForwardHistory.Count > 0;
    }

    private async void SearchButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null) return;

        ReaderChapterPanelPopup.IsOpen = false;
        ReaderAppearancePanelPopup.IsOpen = false;
        ReaderHighlightsPanelPopup.IsOpen = false;
        ReaderStatisticsPanelPopup.IsOpen = false;
        OpenReaderPanel(ReaderSearchPanelPopup, ReaderSearchPanelRoot);
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

        ReaderSearchPanelPopup.IsOpen = false;
        await ViewModel.SaveProgressNowAsync();

        if (result.ChapterIndex == ViewModel.CurrentChapterIndex)
        {
            RecordReaderHistoryEntry();
            _currentProgress = result.ChapterProgress;
            ViewModel.UpdateProgress(result.ChapterProgress);
            await SendRestoreProgressMessageAsync(result.ChapterProgress);
            ViewModel.SaveProgressDebounced();
            return;
        }

        RecordReaderHistoryEntry();
        LoadChapter(result.ChapterIndex, result.ChapterProgress);
        ViewModel.SaveProgressDebounced();
    }

    private async void Highlight_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ReaderHighlightListItem item)
            return;

        ReaderHighlightsPanelPopup.IsOpen = false;
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
        await ViewModel.SaveProgressNowAsync();

        var target = item.JumpTarget;
        if (target.ChapterIndex == ViewModel.CurrentChapterIndex)
        {
            RecordReaderHistoryEntry();
            _currentProgress = target.ChapterProgress;
            ViewModel.UpdateProgress(target.ChapterProgress);
            await SendRestoreProgressMessageAsync(target.ChapterProgress);
            ViewModel.SaveProgressDebounced();
            return;
        }

        RecordReaderHistoryEntry();
        LoadChapter(target.ChapterIndex, target.ChapterProgress);
        ViewModel.SaveProgressDebounced();
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
        ReaderChapterPanelPopup.IsOpen = false;

        if (chapterIndex >= 0 && chapterIndex != ViewModel.CurrentChapterIndex)
        {
            await ViewModel.SaveProgressNowAsync();
            RecordReaderHistoryEntry();
            LoadChapter(chapterIndex);
        }
    }

    private async void OnCharacterJumpRequested(object? sender, int characterCount)
    {
        ReaderChapterPanelPopup.IsOpen = false;
        await JumpToCharacterAsync(characterCount);
    }

    private async Task JumpToCharacterAsync(int characterCount)
    {
        var target = ResolveCharacterJumpTarget(characterCount);
        if (target == null)
            return;

        await ViewModel.SaveProgressNowAsync();
        if (target.ChapterIndex == ViewModel.CurrentChapterIndex)
        {
            RecordReaderHistoryEntry();
            _currentProgress = target.ChapterProgress;
            ViewModel.UpdateProgress(target.ChapterProgress);
            await SendRestoreProgressMessageAsync(target.ChapterProgress);
            ViewModel.SaveProgressDebounced();
            return;
        }

        RecordReaderHistoryEntry();
        LoadChapter(target.ChapterIndex, target.ChapterProgress);
        ViewModel.SaveProgressDebounced();
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

    private void OpenReaderPanel(Popup popup, FrameworkElement root)
    {
        root.Width = ReaderPanelWidth;
        root.MaxHeight = Math.Max(320, XamlRoot.Size.Height - ReaderPanelTop - ReaderPanelBottomMargin);
        popup.HorizontalOffset = ReaderPanelLeft;
        popup.VerticalOffset = ReaderPanelTop;
        popup.IsOpen = true;
    }

    private void CloseReaderPanels()
    {
        ReaderChapterPanelPopup.IsOpen = false;
        ReaderAppearancePanelPopup.IsOpen = false;
        ReaderSearchPanelPopup.IsOpen = false;
        ReaderHighlightsPanelPopup.IsOpen = false;
        ReaderStatisticsPanelPopup.IsOpen = false;
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
        html = ScriptOrStyleRegex().Replace(html, "");
        html = RubyAnnotationRegex().Replace(html, "");
        html = TagRegex().Replace(html, "");
        html = WebUtility.HtmlDecode(html);
        return html.Count(c => !char.IsWhiteSpace(c));
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

    [System.Text.RegularExpressions.GeneratedRegex(
        @"<(script|style)\b[^>]*>.*?</\1>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex ScriptOrStyleRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"<(rt|rp)\b[^>]*>.*?</\1>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex RubyAnnotationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"<[^>]+>",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex TagRegex();

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

    private async void SasayakiLoadButton_Click(object sender, RoutedEventArgs e)
    {
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
            UpdateSasayakiBarVisibility();
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
            SelectSasayakiPlaybackRate(_sasayakiPlayer.PlaybackRate);

            _sasayakiVM.UpdatePlaybackState(false, false, 0, _sasayakiPlayer.DurationSeconds);
            UpdateSasayakiBarVisibility();
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
                UpdateSasayakiBarVisibility();
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

    private void ApplySasayakiPlayback(SasayakiPlaybackData playback)
    {
        if (_sasayakiPlayer == null)
            return;

        _sasayakiDelay = playback.Delay;
        _sasayakiPlayer.PlaybackRate = playback.Rate;
        _sasayakiVM.SetPlaybackRate(playback.Rate);
        SelectSasayakiPlaybackRate(playback.Rate);

        var position = Math.Max(0, playback.LastPosition);
        if (position > 0)
            _sasayakiPlayer.Seek(position);

        _sasayakiNav.UpdatePosition(position);
        _lastSasayakiPlaybackSavePosition = position;

        var currentCue = _sasayakiNav.CurrentCue;
        _sasayakiVM.UpdateCurrentCue(currentCue);
        SasayakiCueTextBlock.Text = currentCue?.Text ?? "";
        _sasayakiVM.UpdatePlaybackState(false, false, position, _sasayakiPlayer.DurationSeconds);

        if (_sasayakiPlayer.DurationSeconds > 0)
        {
            _sasayakiSeeking = false;
            SasayakiPositionSlider.Value = (position / _sasayakiPlayer.DurationSeconds) * 100;
            _sasayakiSeeking = true;
        }
    }

    private void SelectSasayakiPlaybackRate(double rate)
    {
        var index = _sasayakiVM.AvailablePlaybackRates
            .Select((value, i) => new { value, i })
            .FirstOrDefault(item => Math.Abs(item.value - rate) < 0.001)?.i;
        if (index.HasValue && SasayakiRateComboBox.SelectedIndex != index.Value)
            SasayakiRateComboBox.SelectedIndex = index.Value;
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
            SasayakiPlayIcon.Glyph = ""; // Play
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
        SasayakiPlayIcon.Glyph = ""; // Pause
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

        var cueIndex = ResolveCurrentSasayakiCueIndex();
        if (!cueIndex.HasValue || _sasayakiMatchData == null)
            return;

        var cue = _sasayakiMatchData.Cues[cueIndex.Value];
        await SeekToSasayakiCueAsync(
            cueIndex.Value,
            startPlayback: true,
            stopPlaybackAtSeconds: cue.EndTime);
    }

    private async Task JumpToCurrentSasayakiCueAsync()
    {
        if (!CanHandleSasayakiShortcut())
            return;

        var match = ResolveCurrentSasayakiMatch();
        var target = match == null ? null : ResolveSasayakiJumpTarget(match);
        if (match == null || target == null)
            return;

        await ViewModel.SaveProgressNowAsync();
        if (target.ChapterIndex == ViewModel.CurrentChapterIndex)
        {
            RecordReaderHistoryEntry();
            _currentProgress = target.ChapterProgress;
            ViewModel.UpdateProgress(target.ChapterProgress);
            await SendRestoreProgressMessageAsync(target.ChapterProgress);
            ViewModel.SaveProgressDebounced();
            await HighlightSasayakiCueAsync(match);
            return;
        }

        RecordReaderHistoryEntry();
        LoadChapter(target.ChapterIndex, target.ChapterProgress);
        ViewModel.SaveProgressDebounced();
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
        SasayakiCueTextBlock.Text = cue.Text;

        var duration = _sasayakiPlayer.DurationSeconds;
        if (duration > 0)
        {
            _sasayakiSeeking = false;
            SasayakiPositionSlider.Value = (cue.StartTime / duration) * 100;
            _sasayakiSeeking = true;
        }

        if (startPlayback)
            _sasayakiPlayer.Play();

        _sasayakiVM.UpdatePlaybackState(
            _sasayakiPlayer.IsPlaying,
            _sasayakiPlayer.IsPaused,
            cue.StartTime,
            duration);
        SasayakiPlayIcon.Glyph = _sasayakiPlayer.IsPlaying ? "" : "";

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
        SasayakiCueTextBlock.Text = currentCue?.Text ?? "";
        _sasayakiVM.UpdatePlaybackState(
            _sasayakiPlayer.IsPlaying,
            _sasayakiPlayer.IsPaused,
            position,
            duration);

        if (duration > 0)
        {
            _sasayakiSeeking = false;
            SasayakiPositionSlider.Value = (position / duration) * 100;
            _sasayakiSeeking = true;
        }

        await SaveSasayakiPlaybackAsync(position);
    }

    private void SasayakiPositionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_sasayakiSeeking || _sasayakiPlayer == null)
            return;

        var totalSeconds = _sasayakiPlayer.DurationSeconds;
        if (totalSeconds <= 0)
            return;

        var seekSeconds = (e.NewValue / 100.0) * totalSeconds;
        _sasayakiStopPlaybackAtSeconds = null;
        _sasayakiPlayer.Seek(seekSeconds);
        _ = SaveSasayakiPlaybackAsync(seekSeconds);
    }

    private void SasayakiRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sasayakiPlayer == null || SasayakiRateComboBox.SelectedIndex < 0)
            return;

        var rates = _sasayakiVM.AvailablePlaybackRates;
        var index = SasayakiRateComboBox.SelectedIndex;
        if (index < rates.Count)
        {
            var rate = rates[index];
            _sasayakiPlayer.PlaybackRate = rate;
            _sasayakiVM.SetPlaybackRate(rate);
            _ = SaveSasayakiPlaybackAsync();
        }
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
                SasayakiPlayIcon.Glyph = ""; // Play
                _ = SaveSasayakiPlaybackAsync(seconds);
                return;
            }

            // Update slider without triggering ValueChanged
            if (duration > 0)
            {
                _sasayakiSeeking = false;
                SasayakiPositionSlider.Value = (seconds / duration) * 100;
                _sasayakiSeeking = true;
            }

            // Update cue navigation
            _sasayakiNav.UpdatePosition(seconds);
            var currentCue = _sasayakiNav.CurrentCue;
            _sasayakiVM.UpdateCurrentCue(currentCue);
            SasayakiCueTextBlock.Text = currentCue?.Text ?? "";

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
            SasayakiPlayIcon.Glyph = ""; // Play
            _ = SaveSasayakiPlaybackAsync(_sasayakiPlayer?.PositionSeconds ?? 0);
        });
    }

    private void OnSasayakiMediaFailed(object? sender, string error)
    {
        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            _sasayakiVM.SasayakiStatusText = $"Playback error: {error}";
            _sasayakiVM.UpdatePlaybackState(false, false, 0, 0);
            SasayakiPlayIcon.Glyph = ""; // Play
        });
    }

    private async Task HighlightSasayakiCueAsync(SasayakiMatch match)
    {
        if (NovelWebView.CoreWebView2 == null)
            return;

        try
        {
            var settings = CurrentSasayakiSettings;
            var useDarkColors = ActualTheme == ElementTheme.Dark;
            var textColor = useDarkColors ? settings.DarkTextColor : settings.LightTextColor;
            var backgroundColor = useDarkColors
                ? settings.DarkBackgroundColor
                : settings.LightBackgroundColor;
            await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                "window.hoshiSasayaki.setColors("
                + JsonSerializer.Serialize(textColor)
                + ", "
                + JsonSerializer.Serialize(backgroundColor)
                + ");");
            var progressJson = await NovelWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.hoshiSasayaki.highlightCue({match.StartCodePoint}, {match.Length}, {JsonSerializer.Serialize(settings.AutoScroll)});");
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
            StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);
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

        StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);
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
