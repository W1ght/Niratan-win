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
using Microsoft.Web.WebView2.Core;
using Hoshi.Enums;
using Hoshi.Helpers;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Anki;
using Hoshi.Services.Audio;
using Hoshi.Services.Dictionary;
using Serilog;

namespace Hoshi.Views.Dictionary;

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

public sealed class DictionaryLookupPopup : IDisposable
{
    public event EventHandler<DictionaryPopupRedirectRequest>? RedirectRequested;
    public event EventHandler? TapOutsideRequested;
    public event EventHandler? DismissRequested;
    public event EventHandler? Scrolled;
    public event EventHandler? ContentReady;

    private readonly Grid _surfaceRoot;
    private readonly SolidColorBrush _surfaceBrush;
    private readonly SolidColorBrush _outlineBrush;
    private readonly CommandBar _sasayakiControlsBar;
    private readonly AppBarButton _sasayakiPopupPlayPauseButton;
    private readonly AppBarButton _sasayakiPopupReplayCueButton;
    private readonly AppBarButton _sasayakiPopupJumpCueButton;
    private readonly FontIcon _sasayakiPopupPlayPauseIcon;
    private readonly WebView2 _contentWebView;
    private readonly PopupHtmlGenerator _htmlGenerator;
    private readonly IDictionaryLookupService _lookupService;
    private readonly IAudioService _audioService;
    private readonly IAnkiService _ankiService;
    private AnkiMiningContext _miningContext = new();
    private AudioSettings _audioSettings = new();
    private AnkiSettings _ankiSettings = new();
    private bool _webViewReady;
    private bool _isWarmed;
    private long _displayGeneration;
    private long? _pendingContentGeneration;
    private CancellationToken _pendingContentCancellationToken;
    private Stopwatch? _pendingContentStopwatch;
    private TaskCompletionSource<bool>? _shellReadyCompletion;
    private string? _currentTraceId;
    private double _readyOpacity = 1;
    private double _popupCornerRadius = 8;

    private const int MaxResolvedAudioUrlCacheEntries = 512;
    private const string AudioSourcePlaceholderPattern = "[^/?#&]+";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_resolvedAudioUrls = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string?>>> s_audioResolutionTasks = new(StringComparer.Ordinal);
    private static readonly HttpClient s_audioResolveHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly LocalAudioSourceListResolver s_localAudioSourceListResolver = new();
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _deferredResultsCts;

    public Border VisualRoot { get; }
    public bool IsWarmed => _isWarmed;

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

        _sasayakiPopupPlayPauseIcon = CreateSasayakiCommandIcon("\uE768");
        _sasayakiPopupPlayPauseButton = CreateSasayakiCommandButton(
            "NovelReaderPopupSasayakiPlayPauseButton",
            "NovelReaderPopupSasayakiPlayPauseButton.AutomationProperties.Name",
            "Play/Pause",
            _sasayakiPopupPlayPauseIcon,
            SasayakiPopupPlayPauseButton_Click);
        _sasayakiPopupReplayCueButton = CreateSasayakiCommandButton(
            "NovelReaderPopupSasayakiReplayCueButton",
            "NovelReaderPopupSasayakiReplayCueButton.AutomationProperties.Name",
            "Replay Cue",
            CreateSasayakiCommandIcon("\uE72C"),
            SasayakiPopupReplayCueButton_Click);
        _sasayakiPopupJumpCueButton = CreateSasayakiCommandButton(
            "NovelReaderPopupSasayakiJumpCueButton",
            "NovelReaderPopupSasayakiJumpCueButton.AutomationProperties.Name",
            "Jump Cue",
            CreateSasayakiCommandIcon("\uE8AD"),
            SasayakiPopupJumpCueButton_Click);

        _sasayakiControlsBar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(8, 0, 8, 0),
            MinHeight = 40,
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

        _surfaceRoot = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
            Children =
            {
                _sasayakiControlsBar,
                _contentWebView,
            },
        };
        Grid.SetRow(_sasayakiControlsBar, 0);
        Grid.SetRow(_contentWebView, 1);

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

    private static FontIcon CreateSasayakiCommandIcon(string glyph) => new()
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 16,
    };

    private static AppBarButton CreateSasayakiCommandButton(
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
        };
        button.Click += clickHandler;
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private void UpdateSasayakiPopupControls()
    {
        var controls = _miningContext.SasayakiPopupControls;
        if (controls == null)
        {
            _sasayakiControlsBar.Visibility = Visibility.Collapsed;
            return;
        }

        _sasayakiControlsBar.Visibility = Visibility.Visible;
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
        var controls = _miningContext.SasayakiPopupControls;
        if (controls == null || controls.CanControl?.Invoke() == false)
            return;

        await controls.TogglePlaybackAsync();
        UpdateSasayakiPopupControls();
    }

    private async Task HandleSasayakiPopupReplayCueAsync()
    {
        var controls = _miningContext.SasayakiPopupControls;
        if (controls == null || controls.CanControl?.Invoke() == false)
            return;

        await controls.ReplayCueAsync();
        UpdateSasayakiPopupControls();
    }

    private async Task HandleSasayakiPopupJumpCueAsync()
    {
        var controls = _miningContext.SasayakiPopupControls;
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

    public async Task WarmAsync(ThemeMode themeMode = ThemeMode.System, AudioSettings? audioSettings = null, AnkiSettings? ankiSettings = null)
    {
        ApplySurfaceTheme(themeMode);
        if (_isWarmed) return;
        var sw = Stopwatch.StartNew();
        _audioSettings = audioSettings ?? new AudioSettings();
        _ankiSettings = ankiSettings ?? new AnkiSettings();

        await EnsureWebViewAsync();
        Log.Information(
            "[LookupTrace] trace={TraceId} popup warm EnsureWebView2 completed in {Ms}ms",
            _currentTraceId ?? "-", sw.ElapsedMilliseconds);
        _shellReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentWebView.CoreWebView2.NavigateToString(_htmlGenerator.GenerateShellHtml(themeMode, audioSettings: _audioSettings, ankiSettings: _ankiSettings, hidden: true));
        Log.Information(
            "[LookupTrace] trace={TraceId} popup warm NavigateToString returned in {Ms}ms",
            _currentTraceId ?? "-", sw.ElapsedMilliseconds);
        await WaitForShellReadyAsync();
        await ApplyPopupCornerRadiusToWebViewAsync();
        _isWarmed = true;
        Log.Information("[DictPopup] Warm WebView2 initialized in {Ms}ms", sw.ElapsedMilliseconds);
    }

    public void SetMiningContext(AnkiMiningContext? context)
    {
        _miningContext = context ?? new AnkiMiningContext();
        UpdateSasayakiPopupControls();
    }

    public void SetReadyOpacity(double opacity)
    {
        _readyOpacity = Math.Clamp(opacity, 0, 1);
        if (VisualRoot.Opacity > 0)
            VisualRoot.Opacity = _readyOpacity;
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplySurfaceTheme(themeMode);
        var sw = Stopwatch.StartNew();
        _currentTraceId = traceId;
        _audioSettings = audioSettings ?? new AudioSettings();
        _ankiSettings = ankiSettings ?? new AnkiSettings();
        CancelPrefetch();
        CancelDeferredResults();
        if (!_isWarmed)
            await WarmAsync(themeMode, _audioSettings, _ankiSettings);
        cancellationToken.ThrowIfCancellationRequested();

        UpdateSasayakiPopupControls();
        var ranges = DictionaryPopupBatchPlanner.Create(results.Count);
        var initialRange = ranges[0];
        var initialResults = results.GetRange(initialRange.Offset, initialRange.Count);
        var generation = PrepareForPendingContent(cancellationToken);
        _pendingContentStopwatch = Stopwatch.StartNew();
        var serializeSw = Stopwatch.StartNew();
        var injectionScript = _htmlGenerator.GenerateInjectionScript(initialResults,
            styles,
            displaySettings,
            themeMode,
            generation,
            _audioSettings,
            _ankiSettings,
            traceId: traceId,
            totalResultCount: results.Count);
        var payloadBytes = Encoding.UTF8.GetByteCount(injectionScript);
        Log.Information(
            "[LookupTrace] trace={TraceId} popup initial serialized in {Ms}ms bytes={Bytes} entries={EntryCount} total={TotalCount}",
            traceId ?? "-",
            serializeSw.ElapsedMilliseconds,
            payloadBytes,
            initialResults.Count,
            results.Count);
        var executeSw = Stopwatch.StartNew();
        await _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
        if (cancellationToken.IsCancellationRequested)
        {
            if (generation == _displayGeneration)
                Hide();
            cancellationToken.ThrowIfCancellationRequested();
        }
        Log.Information(
            "[LookupTrace] trace={TraceId} popup initial ExecuteScriptAsync finished in {Ms}ms total={TotalMs}ms gen={Gen} entries={EntryCount}",
            traceId ?? "-", executeSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, generation, initialResults.Count);
        Log.Information("[Lifecycle] Popup initial content injected: entries={EntryCount} total={TotalCount} gen={Gen}",
            initialResults.Count, results.Count, generation);
        PrefetchAudioUrls(results);

        if (ranges.Count > 1)
        {
            var deferredCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            _deferredResultsCts = deferredCts;
            _ = AppendDeferredResultsAsync(
                results,
                ranges.Skip(1).ToArray(),
                results.Count,
                generation,
                traceId,
                deferredCts);
        }
    }

    private async Task AppendDeferredResultsAsync(
        List<DictionaryLookupResult> results,
        IReadOnlyList<DictionaryPopupBatchRange> ranges,
        int totalResultCount,
        long generation,
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
                    generation);
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
        Log.Information("[Lifecycle] Popup hidden: wasGen={Gen}", _displayGeneration);
        CancelPrefetch();
        CancelDeferredResults();
        _displayGeneration++;
        _pendingContentGeneration = null;
        _pendingContentCancellationToken = default;
        VisualRoot.Opacity = 0;
        VisualRoot.IsHitTestVisible = false;
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

    public async Task HighlightSelectionAsync(string matchedText)
    {
        if (!_webViewReady
            || _contentWebView.CoreWebView2 == null
            || string.IsNullOrEmpty(matchedText))
        {
            return;
        }

        var highlightCount = matchedText.EnumerateRunes().Count();
        if (highlightCount <= 0)
            return;

        await _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.hoshiSelection.highlightSelection({highlightCount});");
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady) return;

        var environment = await WebView2EnvironmentHelper.GetOrCreateAsync();
        await _contentWebView.EnsureCoreWebView2Async(environment);
        _contentWebView.DefaultBackgroundColor = _surfaceBrush.Color;
        var coreWebView = _contentWebView.CoreWebView2;
        if (coreWebView == null)
            throw new InvalidOperationException("Dictionary popup WebView2 initialization was cancelled.");

        coreWebView.Settings.IsScriptEnabled = true;
        coreWebView.Settings.IsWebMessageEnabled = true;
        coreWebView.WebMessageReceived += OnPopupWebMessageReceived;
        coreWebView.AddWebResourceRequestedFilter(
            "https://hoshi-dictionary-media.local/*",
            CoreWebView2WebResourceContext.Image);
        coreWebView.AddWebResourceRequestedFilter(
            "https://hoshi-audio-resolver.local/*",
            CoreWebView2WebResourceContext.All);
        coreWebView.WebResourceRequested += OnPopupWebResourceRequested;

        coreWebView.ProcessFailed += (_, args) =>
            Log.Error("[DictPopup] WebView2 ProcessFailed: Kind={Kind}, ExitCode={ExitCode}, Reason={Reason}",
                args.ProcessFailedKind, args.ExitCode, args.Reason);

        try
        {
            coreWebView.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown")
                .DevToolsProtocolEventReceived += (s, a) =>
                    Log.Error("[DictPopup] JS exception: {Event}", a.ParameterObjectAsJson);

            await coreWebView.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DictPopup] Failed to enable DevTools protocol");
        }

        _webViewReady = true;
    }

    private async void OnPopupWebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri))
            return;

        if (string.Equals(uri.Host, "hoshi-audio-resolver.local", StringComparison.OrdinalIgnoreCase)
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

        if (!string.Equals(uri.Host, "hoshi-dictionary-media.local", StringComparison.OrdinalIgnoreCase)
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

    private bool IsAllowedAudioResolverUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
            return false;

        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            return false;

        if (_audioSettings.EnableLocalAudio && LocalAudioSourceListResolver.IsLocalAudioSourceListUrl(url))
            return true;

        foreach (var template in _audioSettings.EnabledAudioSourceUrls)
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
                    _shellReadyCompletion?.TrySetResult(true);
                    break;

                case "contentReady":
                    Log.Information("[DictPopup] Content ready: {Payload}", payload.GetRawText());
                    Log.Information(
                        "[LookupTrace] trace={TraceId} popup contentReady message received gen={Gen} elapsedSinceInject={Ms}ms",
                        _currentTraceId ?? "-", _pendingContentGeneration, _pendingContentStopwatch?.ElapsedMilliseconds ?? -1);
                    if (!IsCurrentContentReady(payload))
                    {
                        Log.Debug("[DictPopup] Ignored stale contentReady: {Payload}", payload.GetRawText());
                        break;
                    }

                    var readyGeneration = _pendingContentGeneration!.Value;
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!CanShowReadyContent(readyGeneration))
                            return;

                        ShowReadyContent();
                        ContentReady?.Invoke(this, EventArgs.Empty);
                    });
                    break;

                case "popupDiagnostic":
                    LogPopupDiagnostic(payload);
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

    private long PrepareForPendingContent(CancellationToken cancellationToken)
    {
        var generation = ++_displayGeneration;
        _pendingContentGeneration = generation;
        _pendingContentCancellationToken = cancellationToken;
        _pendingContentStopwatch = null;
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.Opacity = 0;
        VisualRoot.IsHitTestVisible = false;
        return generation;
    }

    private async Task WaitForShellReadyAsync()
    {
        var readyTask = _shellReadyCompletion?.Task;
        if (readyTask == null)
            return;

        var completed = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(3)));
        if (!ReferenceEquals(completed, readyTask))
            Log.Warning("[DictPopup] Shell ready timed out; continuing with existing WebView2 document");

        _shellReadyCompletion = null;
    }

    private void ShowReadyContent()
    {
        _pendingContentGeneration = null;
        _pendingContentCancellationToken = default;
        _pendingContentStopwatch = null;
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.Opacity = _readyOpacity;
        VisualRoot.IsHitTestVisible = true;
    }

    private bool IsCurrentContentReady(JsonElement payload)
    {
        if (_pendingContentGeneration is not long expected)
            return false;
        if (_pendingContentCancellationToken.IsCancellationRequested)
            return false;

        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return false;

        return body.TryGetProperty("generation", out var generationElement)
            && generationElement.TryGetInt64(out var generation)
            && generation == expected;
    }

    private bool CanShowReadyContent(long generation) =>
        _pendingContentGeneration == generation
        && !_pendingContentCancellationToken.IsCancellationRequested;

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
    private void PrefetchAudioUrls(List<DictionaryLookupResult> results)
    {
        var sources = _audioSettings.EnabledAudioSourceUrls;
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
                if (!IsAllowedAudioResolverUrl(resolverUrl))
                    return;
                await TryResolveAudioUrlAsync(resolverUrl, _currentTraceId ?? "prefetch", "prefetch");
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
        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                Log.Information("[Lifecycle] Anki mine started");
                var preflight = await _ankiService.PreflightMiningAsync(rawPayload, _miningContext);
                if (!preflight.CanMine)
                {
                    await _contentWebView.CoreWebView2.ExecuteScriptAsync(
                        "if (typeof window.onMineComplete === 'function') window.onMineComplete(false);");
                    return;
                }

                await RequestVideoMiningMediaAsync(preflight);
                await RequestSasayakiMiningMediaAsync(preflight);
                var success = await _ankiService.MineEntryAsync(rawPayload, _miningContext);

                var script = success
                    ? "if (typeof window.onMineComplete === 'function') window.onMineComplete(true);"
                    : "if (typeof window.onMineComplete === 'function') window.onMineComplete(false);";

                await _contentWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DictPopup] MineEntry failed");
                await _contentWebView.CoreWebView2.ExecuteScriptAsync(
                    "if (typeof window.onMineComplete === 'function') window.onMineComplete(false);");
            }
        });
    }

    private async Task RequestVideoMiningMediaAsync(AnkiMiningPreflightResult preflight)
    {
        if (!preflight.MediaNeeds.NeedsVideoMedia || _miningContext.VideoMediaProvider == null)
            return;

        var result = await _miningContext.VideoMediaProvider(
            new VideoMiningMediaRequest(
                preflight.MediaNeeds.NeedsVideoScreenshot,
                preflight.MediaNeeds.NeedsVideoAudioClip,
                preflight.DirectMediaDirectory),
            CancellationToken.None);

        _miningContext.VideoScreenshotPath = result.ScreenshotPath;
        _miningContext.VideoAudioClipPath = result.AudioClipPath;
        _miningContext.VideoScreenshotTag = result.ScreenshotTag;
        _miningContext.VideoAudioClipTag = result.AudioClipTag;
    }

    private async Task RequestSasayakiMiningMediaAsync(AnkiMiningPreflightResult preflight)
    {
        if (!preflight.MediaNeeds.NeedsSasayakiAudio || _miningContext.SasayakiAudioProvider == null)
            return;

        var result = await _miningContext.SasayakiAudioProvider(
            new SasayakiMiningAudioRequest(
                preflight.MediaNeeds.NeedsSasayakiAudio,
                preflight.DirectMediaDirectory),
            CancellationToken.None);

        _miningContext.SasayakiAudioPath = result.AudioClipPath;
        _miningContext.SasayakiAudioTag = result.AudioClipTag;
    }

    private void HandleDuplicateCheck(JsonElement payload)
    {
        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return;

        var rawPayload = body.GetRawText();
        _ = _contentWebView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var isDuplicate = await _ankiService.DuplicateCheckAsync(rawPayload);
                var script = isDuplicate
                    ? "if (typeof window.onDuplicateCheck === 'function') window.onDuplicateCheck(true);"
                    : "if (typeof window.onDuplicateCheck === 'function') window.onDuplicateCheck(false);";

                await _contentWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DictPopup] DuplicateCheck failed");
            }
        });
    }

    public void Dispose()
    {
        CancelPrefetch();
        CancelDeferredResults();
        if (_contentWebView.CoreWebView2 != null)
        {
            _contentWebView.CoreWebView2.WebMessageReceived -= OnPopupWebMessageReceived;
            _contentWebView.CoreWebView2.WebResourceRequested -= OnPopupWebResourceRequested;
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
