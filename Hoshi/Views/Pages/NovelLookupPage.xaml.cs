using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        _popupOverlay ??= new DictionaryPopupOverlay();
        _popupOverlay.EmbedRoot(DictionaryPanelRoot);
        DictionaryPanelRoot.SizeChanged += OnDictionaryPanelSizeChanged;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), true);
        _ = _popupOverlay.PrewarmAsync(XamlRoot);
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_popupOverlay is null) return;
        if (DictionaryPanelRoot.Visibility != Visibility.Visible) return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (ReferenceEquals(source, DictionaryPanelRoot))
                return; // Tap is inside the popup panel
            source = VisualTreeHelper.GetParent(source);
        }

        _popupOverlay.Dismiss();
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
            ViewModel.IsLookupInProgress = true;
            ViewModel.StatusText = "Looking up...";

            var displaySettings = App.GetService<ISettingsService>().Current.DictionaryDisplaySettings;
            var lookupService = App.GetService<IDictionaryLookupService>();
            var query = ViewModel.Query.Trim();
            var results = await lookupService.LookupAsync(
                query,
                displaySettings.MaxResults,
                displaySettings.ScanLength);

            if (results.Count == 0)
            {
                ViewModel.StatusText = "No results.";
                return;
            }

            var styles = await lookupService.GetStylesAsync();
            var styleDict = styles.ToDictionary(s => s.DictName, s => s.Styles);

            var appTheme = App.GetService<ISettingsService>().Current.Theme;

            _popupOverlay ??= new DictionaryPopupOverlay();
            _popupOverlay.EmbedRoot(DictionaryPanelRoot);
            _ = _popupOverlay.PrewarmAsync(XamlRoot);
            await _popupOverlay.ShowLookupAsync(
                results,
                styleDict,
                displaySettings,
                0, 0, 1, 1,
                XamlRoot,
                isVertical: false,
                themeMode: appTheme);

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
        RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed));
        DictionaryPanelRoot.SizeChanged -= OnDictionaryPanelSizeChanged;
        _popupOverlay?.Dispose();
        _popupOverlay = null;
    }
}
