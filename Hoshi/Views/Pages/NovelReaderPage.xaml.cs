using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using Hoshi.Services.Settings;
using Hoshi.Views.Dialogs;
using Serilog;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;
using Hoshi.Views.Dictionary;

namespace Hoshi.Views.Pages;

public sealed partial class NovelReaderPage : Page
{
    private const string NovelBookHostName = "hoshi-novel-book.local";
    private const string ArtifactDirectoryEnvironmentVariable =
        "HOSHI_NOVEL_READER_ARTIFACT_DIR";

    public NovelReaderPageViewModel ViewModel { get; set; }
    private EpubBook? _epubBook;
    private string _readerJs = "";
    private string _selectionJs = "";
    private string _currentReaderCss = "";
    private double _currentProgress;
    private int _previousChapterIndex = -1;
    private int? _pendingChapterIndex;
    private CancellationTokenSource? _reloadCts;
    private DictionaryPopupOverlay? _popupOverlay;
    private readonly SemaphoreSlim _lookupSemaphore = new(1, 1);
    private long _lookupRequestVersion;

    public NovelReaderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelReaderPageViewModel>();
        DataContext = ViewModel;
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

        var readerSettings = App.GetService<IReaderSettingsService>();
        readerSettings.SettingChanged -= OnReaderSettingChanged;

        if (NovelWebView.CoreWebView2 != null)
        {
            NovelWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            NovelWebView.CoreWebView2.DOMContentLoaded -= OnDomContentLoaded;
            NovelWebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
        }

        NovelWebView.SizeChanged -= OnWebViewSizeChanged;

        _popupOverlay?.Dispose();
        _popupOverlay = null;
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
        ViewModel.Progress = ViewModel.CurrentBook?.Progress ?? 0;
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

        _popupOverlay ??= new DictionaryPopupOverlay();
        _ = _popupOverlay.PrewarmAsync(XamlRoot);

        Log.Information("[NovelReader] Loading chapter {Index}", initialChapterIndex);
        LoadChapter(initialChapterIndex);
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
                LoadChapter(ViewModel.CurrentChapterIndex);
        }
        catch (OperationCanceledException)
        {
            // Ignored — newer change arrived and cancelled this one
        }
    }

    private void LoadChapter(int index)
    {
        if (_epubBook == null || NovelWebView.CoreWebView2 == null)
            return;

        if (index < 0 || index >= _epubBook.Chapters.Count)
            return;

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
        _previousChapterIndex = index;

        ViewModel.SetChapter(index, _epubBook.Chapters.Count);

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
        var systemDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        _currentReaderCss = NovelReaderContentStyles.GenerateCss(readerSettings.Current, systemDark);

        NovelWebView.DefaultBackgroundColor = ARGBToWindowsColor(
            readerSettings.Current.BackgroundColor(systemDark));

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

            var readerSettings = App.GetService<IReaderSettingsService>();
            var lookupSettings = JsonSerializer.Serialize(new
            {
                shiftHoverDelayMs = readerSettings.Current.ShiftHoverLookupDelayMs
            });
            await sender.ExecuteScriptAsync(
                $"window.__hoshiLookupSettings = {lookupSettings};");

            if (!string.IsNullOrEmpty(_currentReaderCss))
            {
                var cssScript = NovelReaderContentStyles.GenerateScriptTag(_currentReaderCss);
                await sender.ExecuteScriptAsync(cssScript);
            }

            if (!string.IsNullOrEmpty(_readerJs))
                await sender.ExecuteScriptAsync(_readerJs);

            if (!string.IsNullOrEmpty(_selectionJs))
                await sender.ExecuteScriptAsync(_selectionJs);

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
        var text = payload.GetProperty("text").GetString() ?? "";
        var sentence = payload.GetProperty("sentence").GetString() ?? "";
        var x = payload.GetProperty("x").GetDouble();
        var y = payload.GetProperty("y").GetDouble();
        var width = payload.GetProperty("width").GetDouble();
        var height = payload.GetProperty("height").GetDouble();

        Log.Information(
            "[NovelReader] Lookup request: '{Text}' (sentence: '{Sentence}') at ({X:F0},{Y:F0})",
            text, sentence, x, y);

        if (string.IsNullOrWhiteSpace(text))
            return;

        var requestVersion = Interlocked.Increment(ref _lookupRequestVersion);

        var lookupService = App.GetService<IDictionaryLookupService>();
        var results = await lookupService.LookupAsync(text);

        Log.Information(
            "[NovelReader] Lookup returned {Count} results for '{Text}'",
            results.Count, text);

        if (results.Count == 0)
            return;

        var styles = await lookupService.GetStylesAsync();
        var styleDict = styles.ToDictionary(s => s.DictName, s => s.Styles);

        // Convert WebView2-relative coordinates to window-relative
        var webViewTransform = NovelWebView.TransformToVisual(null);
        var webViewOffset = webViewTransform.TransformPoint(new Windows.Foundation.Point(0, 0));
        var windowX = webViewOffset.X + x;
        var windowY = webViewOffset.Y + y;

        var readerSettings = App.GetService<Services.Settings.IReaderSettingsService>();
        var isVertical = readerSettings.Current.VerticalWriting;

        await _lookupSemaphore.WaitAsync();
        try
        {
            if (requestVersion != Volatile.Read(ref _lookupRequestVersion))
                return;

            _popupOverlay ??= new DictionaryPopupOverlay();
            _ = _popupOverlay.PrewarmAsync(XamlRoot);
            await _popupOverlay.ShowLookupAsync(
                results, styleDict, new Models.Settings.DictionaryDisplaySettings(),
                windowX, windowY, width, height,
                XamlRoot, isVertical);
        }
        finally
        {
            _lookupSemaphore.Release();
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

    private async void ChapterListButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_epubBook == null) return;

        var dialog = await ReaderChapterListDialog.ShowAsync(
            XamlRoot,
            _epubBook.Chapters,
            _epubBook.Toc,
            ViewModel.CurrentChapterIndex);

        if (dialog.SelectedChapterIndex >= 0
            && dialog.SelectedChapterIndex != ViewModel.CurrentChapterIndex)
        {
            await ViewModel.SaveProgressNowAsync();
            LoadChapter(dialog.SelectedChapterIndex);
        }
    }

    private async void AppearanceButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ReaderAppearanceDialog.ShowAsync(XamlRoot);
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
}
