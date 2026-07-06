using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Hoshi.Enums;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Models.Anki;
using Hoshi.Services.Anki;
using Hoshi.Services.Audio;
using Hoshi.Services.Dictionary;
using Serilog;

namespace Hoshi.Views.Dictionary;

public sealed record DictionaryPopupRedirectRequest(string Query, double? X = null, double? Y = null, double? Width = null, double? Height = null);

public sealed class DictionaryLookupPopup : IDisposable
{
    public event EventHandler<DictionaryPopupRedirectRequest>? RedirectRequested;
    public event EventHandler? TapOutsideRequested;
    public event EventHandler? Scrolled;
    public event EventHandler? ContentReady;

    private readonly WebView2 _contentWebView;
    private readonly AcrylicBrush _surfaceBrush;
    private readonly SolidColorBrush _strokeBrush;
    private readonly PopupHtmlGenerator _htmlGenerator;
    private readonly IDictionaryLookupService _lookupService;
    private readonly IAudioService _audioService;
    private readonly IAnkiService _ankiService;
    private AudioSettings _audioSettings = new();
    private AnkiSettings _ankiSettings = new();
    private bool _webViewReady;
    private bool _isWarmed;
    private long _displayGeneration;
    private long? _pendingContentGeneration;
    private TaskCompletionSource<bool>? _shellReadyCompletion;
    private string? _currentTraceId;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_resolvedAudioUrls = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> s_audioResolutionTasks = new();
    private static readonly HttpClient s_audioResolveHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _prefetchCts;

    public Border VisualRoot { get; }
    public bool IsWarmed => _isWarmed;

    public DictionaryLookupPopup()
    {
        _htmlGenerator = new PopupHtmlGenerator();
        _lookupService = App.GetService<IDictionaryLookupService>();
        _audioService = App.GetService<IAudioService>();
        _ankiService = App.GetService<IAnkiService>();

        _contentWebView = new WebView2
        {
            DefaultBackgroundColor = Colors.Transparent,
        };

        _surfaceBrush = new AcrylicBrush
        {
            AlwaysUseFallback = false,
            TintColor = Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF8),
            TintOpacity = 0.04,
            TintLuminosityOpacity = 0.06,
            FallbackColor = Windows.UI.Color.FromArgb(0x90, 0xF8, 0xF8, 0xF8),
        };
        _strokeBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        VisualRoot = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = _strokeBrush,
            Background = _surfaceBrush,
            Child = _contentWebView,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 32),
            Visibility = Visibility.Visible,
            Opacity = 0,
            IsHitTestVisible = false,
        };
    }

    public async Task WarmAsync(ThemeMode themeMode = ThemeMode.System, AudioSettings? audioSettings = null, AnkiSettings? ankiSettings = null)
    {
        if (_isWarmed) return;
        _audioSettings = audioSettings ?? new AudioSettings();
        _ankiSettings = ankiSettings ?? new AnkiSettings();

        await EnsureWebViewAsync();
        _shellReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentWebView.CoreWebView2.NavigateToString(_htmlGenerator.GenerateShellHtml(themeMode, audioSettings: _audioSettings, ankiSettings: _ankiSettings, hidden: true));
        await WaitForShellReadyAsync();
        _isWarmed = true;
        Log.Information("[DictPopup] Warm root WebView2 initialized");
    }

    public void SetTheme(ThemeMode themeMode)
    {
        var isDark = IsThemeDark(themeMode);
        _surfaceBrush.TintColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x24, 0x24, 0x24)
            : Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF8);
        _surfaceBrush.AlwaysUseFallback = false;
        _surfaceBrush.TintOpacity = 0.04;
        _surfaceBrush.TintLuminosityOpacity = 0.06;
        _surfaceBrush.FallbackColor = isDark
            ? Windows.UI.Color.FromArgb(0x70, 0x24, 0x24, 0x24)
            : Windows.UI.Color.FromArgb(0x90, 0xF8, 0xF8, 0xF8);

        _strokeBrush.Color = isDark
            ? Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00);
    }

    private static bool IsThemeDark(ThemeMode themeMode) => themeMode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    public async Task ShowResultsWarmAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        ThemeMode themeMode,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        string? traceId = null)
    {
        var sw = Stopwatch.StartNew();
        _currentTraceId = traceId;
        _audioSettings = audioSettings ?? new AudioSettings();
        _ankiSettings = ankiSettings ?? new AnkiSettings();
        CancelPrefetch();
        if (!_isWarmed)
            await WarmAsync(themeMode, _audioSettings, _ankiSettings);

        SetTheme(themeMode);
        var generation = PrepareForPendingContent();
        var injectionScript = _htmlGenerator.GenerateInjectionScript(results, styles, displaySettings, themeMode, generation, _audioSettings, _ankiSettings, traceId: traceId);
        await _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
        Log.Information(
            "[LookupTrace] trace={TraceId} popup ExecuteScriptAsync finished in {Ms}ms gen={Gen} entries={EntryCount}",
            traceId ?? "-", sw.ElapsedMilliseconds, generation, results.Count);
        Log.Information("[Lifecycle] Popup content injected: entries={EntryCount} gen={Gen}", results.Count, generation);
        PrefetchAudioUrls(results);
    }

    public void Hide()
    {
        Log.Information("[Lifecycle] Popup hidden: wasGen={Gen}", _displayGeneration);
        CancelPrefetch();
        _displayGeneration++;
        _pendingContentGeneration = null;
        VisualRoot.Opacity = 0;
        VisualRoot.IsHitTestVisible = false;
    }

    public void SetSize(double width, double height)
    {
        if (width > 0) VisualRoot.Width = width;
        if (height > 0) VisualRoot.Height = height;
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

        await _contentWebView.EnsureCoreWebView2Async();
        var coreWebView = _contentWebView.CoreWebView2;
        if (coreWebView == null)
            throw new InvalidOperationException("Dictionary popup WebView2 initialization was cancelled.");

        coreWebView.Settings.IsScriptEnabled = true;
        coreWebView.Settings.IsWebMessageEnabled = true;
        coreWebView.WebMessageReceived += OnPopupWebMessageReceived;
        coreWebView.AddWebResourceRequestedFilter(
            "https://hoshi-dictionary-media.local/*",
            CoreWebView2WebResourceContext.Image);
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
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Host, "hoshi-dictionary-media.local", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, "/image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var deferral = args.GetDeferral();
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
                        "[LookupTrace] trace={TraceId} popup contentReady message received gen={Gen}",
                        _currentTraceId ?? "-", _pendingContentGeneration);
                    if (!IsCurrentContentReady(payload))
                    {
                        Log.Debug("[DictPopup] Ignored stale contentReady: {Payload}", payload.GetRawText());
                        break;
                    }

                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                    {
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

    private long PrepareForPendingContent()
    {
        var generation = ++_displayGeneration;
        _pendingContentGeneration = generation;
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
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.Opacity = 0.88;
        VisualRoot.IsHitTestVisible = true;
    }

    private bool IsCurrentContentReady(JsonElement payload)
    {
        if (_pendingContentGeneration is not long expected)
            return false;

        if (!payload.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            return false;

        return body.TryGetProperty("generation", out var generationElement)
            && generationElement.TryGetInt64(out var generation)
            && generation == expected;
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
            return new DictionaryPopupRedirectRequest(query);

        return new DictionaryPopupRedirectRequest(
            query,
            TryGetDouble(rect, "x"),
            TryGetDouble(rect, "y"),
            TryGetDouble(rect, "width"),
            TryGetDouble(rect, "height"));
    }

    private static double? TryGetDouble(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.TryGetDouble(out var result)
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
            var resolvedUrl = await ResolveAudioUrlAsync(url);
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
                await ResolveAudioUrlAsync(resolverUrl);
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
    private static Task<string> ResolveAudioUrlAsync(string url)
    {
        if (s_resolvedAudioUrls.TryGetValue(url, out var cached))
            return Task.FromResult(cached);

        return s_audioResolutionTasks.GetOrAdd(url, _ => ResolveAndCacheUrlAsync(url));
    }

    private static async Task<string> ResolveAndCacheUrlAsync(string url)
    {
        try
        {
            var result = await ResolveAudioUrlCoreAsync(url);
            s_resolvedAudioUrls[url] = result;
            return result;
        }
        finally
        {
            s_audioResolutionTasks.TryRemove(url, out _);
        }
    }

    private static async Task<string> ResolveAudioUrlCoreAsync(string url)
    {
        try
        {
            var response = await s_audioResolveHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return url;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                      || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase);
            if (!isJson)
                return url;

            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body) || body[0] != '{')
                return url;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() != "audioSourceList"
                || !root.TryGetProperty("audioSources", out var sources)
                || sources.GetArrayLength() == 0)
                return url;

            var first = sources[0];
            if (first.TryGetProperty("url", out var urlEl))
            {
                var resolved = urlEl.GetString()?.Replace("\\", "/", StringComparison.Ordinal) ?? url;
                Log.Information("[DictPopup] Resolved audioSourceList: '{Old}' -> '{New}'", url, resolved);
                return resolved;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictPopup] ResolveAudioUrlCore: resolution failed, using original URL");
        }

        return url;
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
                var context = new AnkiMiningContext();
                var success = await _ankiService.MineEntryAsync(rawPayload, context);

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
