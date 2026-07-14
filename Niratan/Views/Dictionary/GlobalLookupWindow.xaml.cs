using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Dictionary;
using Niratan.ViewModels.Windowing;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace Niratan.Views.Dictionary;

public sealed partial class GlobalLookupWindow : Window, IDisposable
{
    private DictionaryPopupOverlay? _popupOverlay;
    private DictionaryDesktopAcrylicThinBackdrop? _desktopAcrylicThinBackdrop;
    private string? _pendingInitialQuery;
    private bool _isLoaded;

    public GlobalLookupWindowViewModel ViewModel { get; }

    public GlobalLookupWindow()
    {
        InitializeComponent();
        Title = "Global Lookup";
        _desktopAcrylicThinBackdrop = DictionaryPopupMaterial.TryApplyDesktopAcrylicThin(this, RootGrid);
        RootGrid.Background = DictionaryPopupMaterial.CreateWindowFallbackBrush();
        ViewModel = App.GetService<GlobalLookupWindowViewModel>();
        ViewModel.LookupReady += OnLookupReady;
        ViewModel.LookupCleared += OnLookupCleared;
        RootGrid.Loaded += OnLoaded;
        DictionaryPanelRoot.SizeChanged += OnDictionaryPanelSizeChanged;
        Closed += OnClosed;

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(720, 560));
    }

    public async Task OpenAsync(string? initialQuery = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            LookupQueryBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!_isLoaded)
        {
            _pendingInitialQuery = initialQuery;
            return;
        }

        await ViewModel.InitializeAsync(initialQuery, ct);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        var overlay = EnsurePopupOverlay();
        _ = overlay.PrewarmAsync(RootGrid.XamlRoot);
        LookupQueryBox.Focus(FocusState.Programmatic);

        if (!string.IsNullOrWhiteSpace(_pendingInitialQuery))
        {
            var query = _pendingInitialQuery;
            _pendingInitialQuery = null;
            await ViewModel.InitializeAsync(query);
        }
    }

    private async void LookupQueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        await ViewModel.LookupCommand.ExecuteAsync(null);
    }

    private async void LookupButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LookupCommand.ExecuteAsync(null);
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Text))
                return;

            ViewModel.Query = (await data.GetTextAsync()).Trim();
            LookupQueryBox.Select(ViewModel.Query.Length, 0);
            LookupQueryBox.Focus(FocusState.Programmatic);
            ViewModel.StatusText = string.IsNullOrWhiteSpace(ViewModel.Query)
                ? "Clipboard text is empty."
                : "Clipboard text pasted.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async void OnLookupReady(DictionaryPopupRequest request)
    {
        try
        {
            await ShowLookupAsync(request);
            ViewModel.StatusText = "Lookup opened.";
        }
        catch (Exception ex)
        {
            ClearLookup();
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task ShowLookupAsync(DictionaryPopupRequest request)
    {
        var overlay = EnsurePopupOverlay();
        _ = overlay.PrewarmAsync(RootGrid.XamlRoot);
        DictionaryPanelRoot.Visibility = Visibility.Visible;
        await overlay.ShowLookupAsync(
            request.Results,
            request.Styles,
            request.DisplaySettings,
            0,
            0,
            1,
            1,
            RootGrid.XamlRoot,
            isVertical: false,
            request.Theme,
            request.AudioSettings,
            request.AnkiSettings,
            request.MiningContext,
            request.TraceId);
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

    private void OnLookupCleared()
    {
        ClearLookup();
    }

    private void ClearLookup()
    {
        _popupOverlay?.Dismiss();
        DictionaryPanelRoot.Visibility = Visibility.Collapsed;
    }

    private void OnDictionaryPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _popupOverlay?.UpdateRootSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    public void Dispose()
    {
        ViewModel.LookupReady -= OnLookupReady;
        ViewModel.LookupCleared -= OnLookupCleared;
        RootGrid.Loaded -= OnLoaded;
        DictionaryPanelRoot.SizeChanged -= OnDictionaryPanelSizeChanged;
        Closed -= OnClosed;
        _desktopAcrylicThinBackdrop?.Dispose();
        _desktopAcrylicThinBackdrop = null;

        if (_popupOverlay is not null)
        {
            _popupOverlay.Dismissed -= OnPopupOverlayDismissed;
            _popupOverlay.Dispose();
            _popupOverlay = null;
        }
    }
}
