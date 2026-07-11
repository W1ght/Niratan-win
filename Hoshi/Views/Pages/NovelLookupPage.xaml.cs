using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Hoshi.Views.Dictionary;
using Hoshi.ViewModels.Pages;
using Serilog;

namespace Hoshi.Views.Pages;

public sealed partial class NovelLookupPage : Page, IDisposable
{
    public NovelLookupPageViewModel ViewModel { get; set; }
    private DictionaryPopupOverlay? _popupOverlay;

    public NovelLookupPage()
    {
        ViewModel = App.GetService<NovelLookupPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += NovelLookupPage_Loaded;
        Unloaded += NovelLookupPage_Unloaded;
    }

    private async void LookupQueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        await LookupAsync();
    }

    private async void LookupButton_Click(object sender, RoutedEventArgs e)
    {
        await LookupAsync();
    }

    private void NovelLookupPage_Loaded(object sender, RoutedEventArgs e)
    {
        var popupOverlay = EnsurePopupOverlay();
        DictionaryPanelRoot.SizeChanged += OnDictionaryPanelSizeChanged;
        _ = popupOverlay.PrewarmAsync(XamlRoot);
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay is null)
        {
            _popupOverlay = new DictionaryPopupOverlay();
            _popupOverlay.Dismissed += OnPopupOverlayDismissed;
        }

        _popupOverlay.UseCanvas(DictionaryOverlayCanvas);
        _popupOverlay.EmbedRoot(DictionaryOverlayCanvas);
        return _popupOverlay;
    }

    private void OnPopupOverlayDismissed(object? sender, EventArgs e)
    {
        DictionaryPanelRoot.Visibility = Visibility.Collapsed;
    }

    private void OnDictionaryPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _popupOverlay?.UpdateRootSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void NovelLookupPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private async Task LookupAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Query))
        {
            ViewModel.StatusText = "Enter text to look up.";
            return;
        }

        try
        {
            var totalSw = Stopwatch.StartNew();
            var traceId = $"lookup-page-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}";
            ViewModel.IsLookupInProgress = true;
            ViewModel.StatusText = "Looking up...";

            var displaySettings = App.GetService<ISettingsService>().Current.DictionaryDisplaySettings;
            var lookupService = App.GetService<IDictionaryLookupService>();
            var query = ViewModel.Query.Trim();
            Log.Information(
                "[LookupTrace] trace={TraceId} lookup page start query='{Query}' max={Max} scan={Scan}",
                traceId, query, displaySettings.MaxResults, displaySettings.ScanLength);
            var lookupSw = Stopwatch.StartNew();
            var results = await lookupService.LookupAsync(
                query,
                displaySettings.MaxResults,
                displaySettings.ScanLength,
                traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} lookup page native lookup returned in {Ms}ms total={TotalMs}ms results={Count}",
                traceId, lookupSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results.Count);

            if (results.Count == 0)
            {
                _popupOverlay?.Dismiss();
                DictionaryPanelRoot.Visibility = Visibility.Collapsed;
                ViewModel.StatusText = "No results.";
                return;
            }

            var stylesSw = Stopwatch.StartNew();
            var styles = await lookupService.GetStylesAsync();
            var styleDict = styles.ToDictionary(s => s.DictName, s => s.Styles);
            Log.Information(
                "[LookupTrace] trace={TraceId} lookup page styles loaded in {Ms}ms total={TotalMs}ms styles={StyleCount}",
                traceId, stylesSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, styleDict.Count);

            var appTheme = App.GetService<ISettingsService>().Current.Theme;

            var popupOverlay = EnsurePopupOverlay();
            var showSw = Stopwatch.StartNew();
            _ = popupOverlay.PrewarmAsync(XamlRoot);
            DictionaryPanelRoot.Visibility = Visibility.Visible;
            await popupOverlay.ShowLookupAsync(
                results,
                styleDict,
                displaySettings,
                0, 0, 1, 1,
                XamlRoot,
                isVertical: false,
                themeMode: appTheme,
                traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} lookup page overlay shown in {Ms}ms total={TotalMs}ms",
                traceId, showSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);

            ViewModel.StatusText = $"{results.Count} results.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NovelLookup] Lookup failed for '{Query}'", ViewModel.Query);
            ViewModel.StatusText = ex.Message;
        }
        finally
        {
            ViewModel.IsLookupInProgress = false;
        }
    }

    public void Dispose()
    {
        Loaded -= NovelLookupPage_Loaded;
        Unloaded -= NovelLookupPage_Unloaded;
        DictionaryPanelRoot.SizeChanged -= OnDictionaryPanelSizeChanged;
        if (_popupOverlay != null)
        {
            _popupOverlay.Dismissed -= OnPopupOverlayDismissed;
            _popupOverlay.Dispose();
            _popupOverlay = null;
        }
    }
}
