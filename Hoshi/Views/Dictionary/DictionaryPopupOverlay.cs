using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Serilog;

namespace Hoshi.Views.Dictionary;

public sealed class DictionaryPopupOverlay : IDisposable
{
    private const double PopupWidth = 460;
    private const double PopupMaxHeight = 580;
    private const double PopupPadding = 4;
    private const double ScreenBorderPadding = 6;
    private const int MaxChildPopups = 5;

    private readonly Popup _overlayPopup;
    private readonly Canvas _canvas;
    private readonly IDictionaryLookupService _lookupService;
    private readonly List<DictionaryLookupPopup> _childHosts = [];
    private DictionaryLookupPopup _rootHost = null!;
    private Dictionary<string, string> _currentStyles = new();
    private DictionaryDisplaySettings _displaySettings = new();
    private double _rootPointX;
    private double _rootPointY;
    private double _rootSelectionWidth;
    private double _rootSelectionHeight;
    private bool _isVertical;
    private bool _rootWarm;

    public DictionaryPopupOverlay()
    {
        _lookupService = App.GetService<IDictionaryLookupService>();

        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
        };

        _overlayPopup = new Popup
        {
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = false,
            Child = _canvas,
        };

        _overlayPopup.Closed += OnOverlayClosed;
    }

    public async Task PrewarmAsync(XamlRoot xamlRoot)
    {
        if (_rootWarm) return;

        _overlayPopup.XamlRoot = xamlRoot;
        _rootHost = CreateHost();
        _rootHost.RedirectRequested += OnRootRedirectRequested;
        _rootHost.DismissRequested += OnRootDismissRequested;
        _rootHost.Scrolled += OnRootScrolled;
        _canvas.Children.Add(_rootHost.VisualRoot);

        await _rootHost.WarmAsync();
        _rootWarm = true;
        Log.Information("[DictOverlay] Root popup prewarmed");
    }

    public async Task ShowLookupAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        double pointX, double pointY,
        double selectionWidth, double selectionHeight,
        XamlRoot xamlRoot,
        bool isVertical)
    {
        if (results.Count == 0) return;

        _currentStyles = styles;
        _displaySettings = displaySettings;
        _rootPointX = pointX;
        _rootPointY = pointY;
        _rootSelectionWidth = selectionWidth;
        _rootSelectionHeight = selectionHeight;
        _isVertical = isVertical;

        await EnsureWarmAsync(xamlRoot);

        // Clear child popups
        ClearChildren();

        // Inject results into warm root
        await _rootHost.ShowResultsWarmAsync(results, styles, displaySettings);

        // Position root
        PositionHost(
            _rootHost,
            _rootPointX, _rootPointY,
            _rootSelectionWidth, _rootSelectionHeight,
            _isVertical);

        _overlayPopup.IsOpen = true;
        Log.Information(
            "[DictOverlay] Showing root popup at ({X:F0},{Y:F0}) vertical={Vertical}",
            pointX, pointY, isVertical);
    }

    private async Task EnsureWarmAsync(XamlRoot xamlRoot)
    {
        _overlayPopup.XamlRoot = xamlRoot;
        await PrewarmAsync(xamlRoot);
    }

    private void OnRootRedirectRequested(object? sender, string query)
    {
        _ = HandleRedirectAsync(query);
    }

    private void OnRootDismissRequested(object? sender, EventArgs e)
    {
        _overlayPopup.IsOpen = false;
    }

    private void OnRootScrolled(object? sender, EventArgs e)
    {
        ClearChildren();
    }

    private async Task HandleRedirectAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var results = await _lookupService.LookupAsync(query);
        if (results.Count == 0) return;

        // Limit stack depth
        if (_childHosts.Count >= MaxChildPopups)
        {
            var oldest = _childHosts[0];
            oldest.RedirectRequested -= OnChildRedirectRequested;
            oldest.DismissRequested -= OnChildDismissRequested;
            oldest.Scrolled -= OnChildScrolled;
            oldest.Dispose();
            _canvas.Children.Remove(oldest.VisualRoot);
            _childHosts.RemoveAt(0);
        }

        var child = CreateHost();
        child.RedirectRequested += OnChildRedirectRequested;
        child.DismissRequested += OnChildDismissRequested;
        child.Scrolled += OnChildScrolled;
        _canvas.Children.Add(child.VisualRoot);
        _childHosts.Add(child);

        child.ShowResultsNavigated(results, _currentStyles, _displaySettings);

        // Position child slightly offset from parent
        var parentHost = _childHosts.Count > 1
            ? _childHosts[^2]
            : _rootHost;

        var parentOffset = GetHostOffset(parentHost);
        PositionHostAt(child, parentOffset.x + 20, parentOffset.y + 20, _isVertical);

        Log.Information("[DictOverlay] Child popup created for redirect '{Query}'", query);
    }

    private void OnChildRedirectRequested(object? sender, string query)
    {
        _ = HandleRedirectAsync(query);
    }

    private void OnChildDismissRequested(object? sender, EventArgs e)
    {
        if (sender is not DictionaryLookupPopup host) return;
        RemoveChild(host);
    }

    private void OnChildScrolled(object? sender, EventArgs e)
    {
        ClearChildren();
    }

    private void RemoveChild(DictionaryLookupPopup host)
    {
        host.RedirectRequested -= OnChildRedirectRequested;
        host.DismissRequested -= OnChildDismissRequested;
        host.Scrolled -= OnChildScrolled;
        host.Dispose();
        _canvas.Children.Remove(host.VisualRoot);
        _childHosts.Remove(host);
    }

    private void ClearChildren()
    {
        foreach (var child in _childHosts)
        {
            child.RedirectRequested -= OnChildRedirectRequested;
            child.DismissRequested -= OnChildDismissRequested;
            child.Scrolled -= OnChildScrolled;
            child.Dispose();
            _canvas.Children.Remove(child.VisualRoot);
        }
        _childHosts.Clear();
    }

    private void OnOverlayClosed(object? sender, object e)
    {
        _rootHost.Hide();
        ClearChildren();
    }

    private static DictionaryLookupPopup CreateHost()
    {
        return new DictionaryLookupPopup();
    }

    // --- Positioning (ported from Android LookupPopupLayout) ---

    private void PositionHost(
        DictionaryLookupPopup host,
        double selectionX, double selectionY,
        double selectionWidth, double selectionHeight,
        bool isVertical)
    {
        var rootSize = _overlayPopup.XamlRoot?.Size;
        var screenWidth = rootSize?.Width ?? 1920;
        var screenHeight = rootSize?.Height ?? 1080;

        var maxWidth = PopupWidth;
        var maxHeight = Math.Min(PopupMaxHeight, screenHeight * 0.7);

        double width, height, centerX, centerY;

        if (isVertical)
        {
            width = Math.Min(Math.Max(SpaceLeft(selectionX), SpaceRight(selectionX, selectionWidth, screenWidth)) - ScreenBorderPadding, maxWidth);
            centerX = ShowOnRight(selectionX, selectionWidth, screenWidth)
                ? selectionX + selectionWidth + PopupPadding + width / 2
                : selectionX - PopupPadding - width / 2;
            centerX = Clamp(centerX, width / 2, screenWidth - width / 2);

            height = maxHeight;
            centerY = Clamp(
                selectionY + height / 2,
                height / 2 + ScreenBorderPadding,
                screenHeight - height / 2 - ScreenBorderPadding);
        }
        else
        {
            width = Math.Min(screenWidth - ScreenBorderPadding * 2, maxWidth);
            height = Math.Min(Math.Max(SpaceAbove(selectionY), SpaceBelow(selectionY, selectionHeight, screenHeight)) - ScreenBorderPadding, maxHeight);
            centerX = Clamp(
                selectionX + width / 2,
                width / 2 + ScreenBorderPadding,
                screenWidth - width / 2 - ScreenBorderPadding);
            centerY = ShowBelow(selectionY, selectionHeight, height)
                ? selectionY + selectionHeight + PopupPadding + height / 2
                : selectionY - PopupPadding - height / 2;
            centerY = Clamp(
                centerY,
                height / 2 + ScreenBorderPadding,
                screenHeight - height / 2 - ScreenBorderPadding);
        }

        host.SetSize(width, height);
        Canvas.SetLeft(host.VisualRoot, centerX - width / 2);
        Canvas.SetTop(host.VisualRoot, centerY - height / 2);
    }

    private void PositionHostAt(
        DictionaryLookupPopup host,
        double centerX, double centerY,
        bool isVertical)
    {
        var rootSize = _overlayPopup.XamlRoot?.Size;
        var screenWidth = rootSize?.Width ?? 1920;
        var screenHeight = rootSize?.Height ?? 1080;

        var width = Math.Min(PopupWidth, screenWidth - ScreenBorderPadding * 2);
        var height = Math.Min(PopupMaxHeight, screenHeight * 0.7);

        if (isVertical)
        {
            centerX = Clamp(centerX, width / 2 + ScreenBorderPadding, screenWidth - width / 2 - ScreenBorderPadding);
            centerY = Clamp(centerY, height / 2 + ScreenBorderPadding, screenHeight - height / 2 - ScreenBorderPadding);
        }
        else
        {
            centerX = Clamp(centerX, width / 2 + ScreenBorderPadding, screenWidth - width / 2 - ScreenBorderPadding);
            centerY = Clamp(centerY, height / 2 + ScreenBorderPadding, screenHeight - height / 2 - ScreenBorderPadding);
        }

        host.SetSize(width, height);
        Canvas.SetLeft(host.VisualRoot, centerX - width / 2);
        Canvas.SetTop(host.VisualRoot, centerY - height / 2);
    }

    private static (double x, double y) GetHostOffset(DictionaryLookupPopup host)
    {
        var left = Canvas.GetLeft(host.VisualRoot);
        var top = Canvas.GetTop(host.VisualRoot);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left + host.VisualRoot.Width / 2, top + host.VisualRoot.Height / 2);
    }

    private static double SpaceLeft(double x) => x - PopupPadding;
    private static double SpaceRight(double x, double w, double screenWidth) => screenWidth - x - w - PopupPadding;
    private static double SpaceAbove(double y) => y - PopupPadding;
    private static double SpaceBelow(double y, double h, double screenHeight) => screenHeight - y - h - PopupPadding;
    private static bool ShowOnRight(double x, double w, double screenWidth) => SpaceRight(x, w, screenWidth) >= SpaceLeft(x);
    private static bool ShowBelow(double y, double h, double popupHeight) => SpaceBelow(y, h, popupHeight) >= SpaceAbove(y) || SpaceBelow(y, h, popupHeight) >= popupHeight;
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    public void Dispose()
    {
        _overlayPopup.Closed -= OnOverlayClosed;
        _rootHost.RedirectRequested -= OnRootRedirectRequested;
        _rootHost.DismissRequested -= OnRootDismissRequested;
        _rootHost.Scrolled -= OnRootScrolled;
        _rootHost.Dispose();
        ClearChildren();
        _overlayPopup.IsOpen = false;
    }
}
