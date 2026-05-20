using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Serilog;

namespace Hoshi.Views.Dictionary;

public sealed class DictionaryLookupPopup : IDisposable
{
    public event EventHandler<string>? RedirectRequested;
    public event EventHandler? DismissRequested;
    public event EventHandler? Scrolled;
    public event EventHandler? ContentReady;

    private readonly Grid _layout;
    private readonly WebView2 _contentWebView;
    private readonly StackPanel _controlBar;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _closeButton;
    private readonly PopupHtmlGenerator _htmlGenerator;
    private readonly IDictionaryLookupService _lookupService;
    private bool _webViewReady;
    private bool _isWarmed;

    public Border VisualRoot { get; }
    public bool IsWarmed => _isWarmed;

    public DictionaryLookupPopup()
    {
        _htmlGenerator = new PopupHtmlGenerator();
        _lookupService = App.GetService<IDictionaryLookupService>();

        _backButton = CreateControlButton(""); // Back arrow
        _backButton.Click += BackButton_Click;

        _forwardButton = CreateControlButton(""); // Forward arrow
        _forwardButton.Click += ForwardButton_Click;

        _closeButton = CreateControlButton(""); // Close X
        _closeButton.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);

        _controlBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Padding = new Thickness(8, 6, 8, 6),
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
        };
        _controlBar.Children.Add(_backButton);
        _controlBar.Children.Add(_forwardButton);

        // Spacer
        var spacer = new Border { Width = 0, Height = 0 };
        Grid.SetColumn(spacer, 2);
        _controlBar.Children.Add(spacer);

        _controlBar.Children.Add(_closeButton);

        _contentWebView = new WebView2
        {
            DefaultBackgroundColor = Colors.Transparent,
        };

        _layout = new Grid();
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_controlBar, 0);
        Grid.SetRow(_contentWebView, 1);
        _layout.Children.Add(_controlBar);
        _layout.Children.Add(_contentWebView);

        VisualRoot = new Border
        {
            Width = 460,
            MaxHeight = 580,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = _layout,
            Visibility = Visibility.Collapsed,
        };
    }

    public async Task WarmAsync()
    {
        if (_isWarmed) return;

        await EnsureWebViewAsync();
        _contentWebView.CoreWebView2.NavigateToString(_htmlGenerator.GenerateShellHtml());
        // shellReady will be reported by JS; we don't block on it
        _isWarmed = true;
        Log.Information("[DictPopup] Warm root WebView2 initialized");
    }

    public async Task ShowResultsWarmAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings)
    {
        if (!_isWarmed)
            await WarmAsync();

        var injectionScript = _htmlGenerator.GenerateInjectionScript(results, styles, displaySettings);
        _ = _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
        VisualRoot.Visibility = Visibility.Visible;
    }

    public void ShowResultsNavigated(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings)
    {
        _contentWebView.CoreWebView2.NavigateToString(
            _htmlGenerator.GenerateHtml(results, styles, displaySettings));
        VisualRoot.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        VisualRoot.Visibility = Visibility.Collapsed;
        _controlBar.Visibility = Visibility.Collapsed;
    }

    public void SetSize(double width, double height)
    {
        if (width > 0) VisualRoot.Width = width;
        if (height > 0) VisualRoot.Height = height;
    }

    private static Button CreateControlButton(string glyph)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(6),
        };
        return button;
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

        try
        {
            coreWebView.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled")
                .DevToolsProtocolEventReceived += (s, a) =>
                    Log.Information("[DictPopup] JS console: {Event}", a.ParameterObjectAsJson);

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
                    break;

                case "contentReady":
                    Log.Information("[DictPopup] Content ready: {Payload}", payload.GetRawText());
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateControlBar();
                        ContentReady?.Invoke(this, EventArgs.Empty);
                    });
                    break;

                case "popupDiagnostic":
                    Log.Information("[DictPopup] Diagnostic: {Payload}", payload.GetRawText());
                    break;

                case "tapOutside":
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        DismissRequested?.Invoke(this, EventArgs.Empty));
                    break;

                case "lookupRedirect":
                    var query = payload.GetProperty("body").GetString() ?? "";
                    _contentWebView.DispatcherQueue.TryEnqueue(() =>
                        RedirectRequested?.Invoke(this, query));
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
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictPopup] Failed to process WebMessage");
        }
    }

    private async void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateBack?.()");
        UpdateControlBar();
    }

    private async void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateForward?.()");
        UpdateControlBar();
    }

    private async void UpdateControlBar()
    {
        if (_contentWebView.CoreWebView2 == null) return;

        try
        {
            var backCountRaw = await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.backStack?.length || 0");
            var forwardCountRaw = await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.forwardStack?.length || 0");

            var backCount = int.TryParse(backCountRaw?.Trim('"'), out var bc) ? bc : 0;
            var forwardCount = int.TryParse(forwardCountRaw?.Trim('"'), out var fc) ? fc : 0;

            _contentWebView.DispatcherQueue.TryEnqueue(() =>
            {
                _controlBar.Visibility = backCount > 0 || forwardCount > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _backButton.IsEnabled = backCount > 0;
                _forwardButton.IsEnabled = forwardCount > 0;
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictPopup] Failed to update popup control bar");
        }
    }

    public void Dispose()
    {
        if (_contentWebView.CoreWebView2 != null)
            _contentWebView.CoreWebView2.WebMessageReceived -= OnPopupWebMessageReceived;
        _contentWebView.Close();
    }
}
