using System;
using System.IO;
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
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class NovelReaderPage : Page
{
    private const string NovelBookHostName = "hoshi-novel-book.local";
    private const string ArtifactDirectoryEnvironmentVariable =
        "HOSHI_NOVEL_READER_ARTIFACT_DIR";

    public NovelReaderPageViewModel ViewModel { get; set; }
    private EpubBook? _epubBook;
    private string _readerJs = "";
    private bool _isChapterLoading;
    private CancellationTokenSource? _reloadCts;

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

        ViewModel.SetChapter(0, _epubBook.Chapters.Count);

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

        Log.Information("[NovelReader] Loading chapter 0");
        LoadChapter(0);
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

        _isChapterLoading = true;
        ViewModel.SetChapter(index, _epubBook.Chapters.Count);

        var chapter = _epubBook.Chapters[index];
        var chapterDir = Path.GetDirectoryName(chapter.Href) ?? _epubBook.ContainerDirectory;
        var relativeDir = Path
            .GetRelativePath(_epubBook.ContainerDirectory, chapterDir)
            .Replace('\\', '/');
        var baseUrl = $"https://{NovelBookHostName}/{relativeDir}/";
        Log.Information(
            "[NovelReader] Base URL for chapter {Index}: {BaseUrl}",
            index,
            baseUrl
        );

        var html = File.ReadAllText(chapter.Href);
        var readerSettings = App.GetService<Services.Settings.IReaderSettingsService>();
        var systemDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var css = NovelReaderContentStyles.GenerateCss(readerSettings.Current, systemDark);
        NovelWebView.DefaultBackgroundColor = ARGBToWindowsColor(
            readerSettings.Current.BackgroundColor(systemDark));
        var injected = InjectReaderAssets(html, css, _readerJs, baseUrl);
        Log.Information(
            "[NovelReader] NavigateToString: CSS {CssLen}B + JS {JsLen}B into {OrigLen}B -> {NewLen}B",
            css.Length,
            _readerJs.Length,
            html.Length,
            injected.Length
        );
        NovelWebView.CoreWebView2.NavigateToString(injected);
    }

    private static string InjectReaderAssets(string html, string css, string js, string baseUrl)
    {
        var baseTag = $"<base href=\"{baseUrl}\">";
        var styleTag = $"<style>{css}</style>";
        var scriptTag = $"<script>{js}</script>";

        if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
            html = html.Replace(
                "</head>",
                $"{baseTag}{styleTag}</head>",
                StringComparison.OrdinalIgnoreCase
            );
        else if (html.Contains("<body", StringComparison.OrdinalIgnoreCase))
        {
            var bodyIndex = html.IndexOf(
                "<body",
                StringComparison.OrdinalIgnoreCase
            );
            html = html.Insert(bodyIndex, baseTag + styleTag);
        }
        else
            html = baseTag + styleTag + html;

        if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
            html = html.Replace(
                "</body>",
                $"{scriptTag}</body>",
                StringComparison.OrdinalIgnoreCase
            );
        else
            html += scriptTag;

        return html;
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
                case "readerReady":
                    Log.Information("[NovelReader] Bridge ready, sending setChapter");
                    await SendSetChapterMessageAsync();
                    break;
                case "chapterReady":
                    _isChapterLoading = false;
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

    private async void PreviousPageButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await NavigateReaderAsync("backward");
    }

    private async void NextPageButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await NavigateReaderAsync("forward");
    }

    private async void AppearanceButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ReaderAppearanceDialog.ShowAsync(XamlRoot);
    }

    private async System.Threading.Tasks.Task NavigateReaderAsync(string direction)
    {
        if (NovelWebView.CoreWebView2 == null || _isChapterLoading)
            return;

        await NovelWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.hoshiReaderNavigate?.('{direction}')"
        );
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
