using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Hoshi.Enums;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Anki;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Serilog;

namespace Hoshi.Views.Dictionary;

public sealed class DictionaryPopupOverlay : IDisposable
{
    private const double PopupPadding = DictionaryPopupLayoutCalculator.PopupPadding;
    private const double ScreenBorderPadding = DictionaryPopupLayoutCalculator.ScreenBorderPadding;
    private const double MinPopupWidth = 360;
    private const double MaxPopupWidth = 860;
    private const double MinPopupHeight = 200;
    private const double MaxPopupHeight = 820;
    private const int RootPopupZIndex = 10;
    private const int ChildPopupZIndexBase = 20;
    private const int PopupZIndexStep = 10;

    private Canvas _canvas;
    private readonly IDictionaryLookupService _lookupService;
    private readonly List<DictionaryLookupPopup> _childHosts = [];
    private readonly List<DictionaryLookupPopup> _childHostPool = [];
    private readonly SemaphoreSlim _redirectSemaphore = new(1, 1);
    private long _redirectVersion;
    private string _lastRedirectQuery = "";
    private DictionaryLookupPopup? _lastRedirectParent;
    private DictionaryLookupPopup _rootHost = null!;
    private Dictionary<string, string> _currentStyles = new();
    private DictionaryDisplaySettings _displaySettings = new();
    private double _rootPointX;
    private double _rootPointY;
    private double _rootSelectionWidth;
    private double _rootSelectionHeight;
    private bool _isVertical;
    private bool _rootWarm;
    private bool _rootVisible;
    private ThemeMode _currentTheme;
    private AudioSettings _currentAudioSettings = new();
    private AnkiSettings _currentAnkiSettings = new();
    private string? _currentTraceId;
    private AnkiMiningContext _currentMiningContext = new();
    private Panel? _embeddedPanel;
    private XamlRoot? _currentXamlRoot;

    public event EventHandler? Dismissed;

    public DictionaryPopupOverlay()
    {
        _lookupService = App.GetService<IDictionaryLookupService>();

        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
            Visibility = Visibility.Visible,
        };
        _canvas.PointerPressed += OnOverlayPointerPressed;
    }

    /// <summary>Use the given canvas as the overlay surface (reader page). Must be called before PrewarmAsync.</summary>
    public void UseCanvas(Canvas overlayCanvas)
    {
        if (!ReferenceEquals(_canvas, overlayCanvas))
        {
            _canvas.PointerPressed -= OnOverlayPointerPressed;
            _canvas = overlayCanvas;
            _canvas.PointerPressed += OnOverlayPointerPressed;
        }

        _canvas.Background = new SolidColorBrush(Colors.Transparent);
        _canvas.IsHitTestVisible = false;
        _canvas.Visibility = Visibility.Visible;
    }

    /// <summary>Embed the root popup directly in a page panel (standalone lookup).</summary>
    public void EmbedRoot(Panel panel)
    {
        _embeddedPanel = panel;
    }

    public async Task PrewarmAsync(XamlRoot xamlRoot)
    {
        if (_rootWarm) return;

        _currentXamlRoot = xamlRoot;
        CollapseCanvasBounds();
        _rootHost = CreateHost();
        _rootHost.RedirectRequested += OnRootRedirectRequested;
        _rootHost.TapOutsideRequested += OnRootTapOutsideRequested;
        _rootHost.Scrolled += OnRootScrolled;

        if (_embeddedPanel != null)
        {
            _embeddedPanel.Children.Add(_rootHost.VisualRoot);
        }

        await _rootHost.WarmAsync();

        _rootWarm = true;
        Log.Information("[DictOverlay] Root popup prewarmed (embedded={Embedded})", _embeddedPanel != null);
    }

    public async Task ShowLookupAsync(
        List<DictionaryLookupResult> results,
        Dictionary<string, string> styles,
        DictionaryDisplaySettings displaySettings,
        double pointX, double pointY,
        double selectionWidth, double selectionHeight,
        XamlRoot xamlRoot,
        bool isVertical,
        ThemeMode themeMode = ThemeMode.System,
        AudioSettings? audioSettings = null,
        AnkiSettings? ankiSettings = null,
        AnkiMiningContext? miningContext = null,
        string? traceId = null)
    {
        if (results.Count == 0) return;

        var sw = Stopwatch.StartNew();
        _currentTraceId = traceId;
        _currentStyles = styles;
        _displaySettings = displaySettings;
        _currentTheme = themeMode;
        _rootPointX = pointX;
        _rootPointY = pointY;
        _rootSelectionWidth = selectionWidth;
        _rootSelectionHeight = selectionHeight;
        _isVertical = isVertical;

        var audio = audioSettings ?? App.GetService<ISettingsService>().Current.AudioSettings;
        _currentAudioSettings = audio;

        var anki = ankiSettings ?? App.GetService<ISettingsService>().Current.AnkiSettings;
        _currentAnkiSettings = anki;
        _currentMiningContext = miningContext ?? new AnkiMiningContext();

        await EnsureWarmAsync(xamlRoot);
        Log.Information(
            "[LookupTrace] trace={TraceId} overlay warmed in {Ms}ms embedded={Embedded}",
            traceId ?? "-", sw.ElapsedMilliseconds, _embeddedPanel != null);

        ResetRedirectDeduplication();
        ClearChildren();

        _rootHost.SetMiningContext(_currentMiningContext);
        var injectSw = Stopwatch.StartNew();
        await _rootHost.ShowResultsWarmAsync(results, styles, displaySettings, themeMode, audio, anki, traceId: traceId);
        Log.Information(
            "[LookupTrace] trace={TraceId} root popup content injected in {Ms}ms total={TotalMs}ms entries={EntryCount}",
            traceId ?? "-", injectSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, results.Count);

        if (_embeddedPanel != null)
        {
            _rootHost.SetSize(
                _embeddedPanel.ActualWidth > 0 ? _embeddedPanel.ActualWidth : double.NaN,
                _embeddedPanel.ActualHeight > 0 ? _embeddedPanel.ActualHeight : double.NaN);
            _embeddedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Add root host to canvas for positioning
            EnsureHostOnCanvas(_rootHost);
            ApplyPopupZOrder();

            UpdateCanvasBounds(xamlRoot);

            PositionHost(
                _rootHost,
                _rootPointX, _rootPointY,
                _rootSelectionWidth, _rootSelectionHeight,
                _isVertical,
                isRoot: true);
        }

        _rootVisible = true;
        _canvas.IsHitTestVisible = true;
        _canvas.Visibility = Visibility.Visible;
        Log.Information(
            "[Lifecycle] Popup shown: entries={EntryCount} at=({X:F0},{Y:F0}) vertical={Vertical} embedded={Embedded}",
            results.Count, pointX, pointY, isVertical, _embeddedPanel != null);
    }

    private async Task EnsureWarmAsync(XamlRoot xamlRoot)
    {
        _currentXamlRoot = xamlRoot;
        await PrewarmAsync(xamlRoot);
    }

    private void OnRootRedirectRequested(object? sender, DictionaryPopupRedirectRequest request)
    {
        _ = HandleRedirectAsync(request, sender as DictionaryLookupPopup);
    }

    private void OnRootTapOutsideRequested(object? sender, EventArgs e)
    {
        ClearChildren();
    }

    private void OnRootScrolled(object? sender, EventArgs e)
    {
        ClearChildren();
    }

    private async Task HandleRedirectAsync(DictionaryPopupRedirectRequest request, DictionaryLookupPopup? parentHost)
    {
        var query = request.Query.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        var redirectVersion = Interlocked.Increment(ref _redirectVersion);

        await _redirectSemaphore.WaitAsync();
        try
        {
            if (redirectVersion != Volatile.Read(ref _redirectVersion))
                return;

            var parent = parentHost ?? _rootHost;
            if (ReferenceEquals(parent, _lastRedirectParent)
                && string.Equals(query, _lastRedirectQuery, StringComparison.Ordinal))
            {
                Log.Debug("[DictOverlay] Ignored duplicate redirect '{Query}' from same parent", query);
                return;
            }

            _lastRedirectParent = parent;
            _lastRedirectQuery = query;

            var traceId = $"{_currentTraceId ?? "popup-redirect"}-child-{redirectVersion}";
            var lookupSw = Stopwatch.StartNew();
            var results = await _lookupService.LookupAsync(
                query,
                _displaySettings.MaxResults,
                _displaySettings.ScanLength,
                traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} child redirect lookup finished in {Ms}ms query='{Query}' results={Count}",
                traceId, lookupSw.ElapsedMilliseconds, query, results.Count);
            if (redirectVersion != Volatile.Read(ref _redirectVersion))
                return;
            if (results.Count == 0) return;

            await HighlightPopupSelectionAsync(parent, results[0].Matched);

            CloseChildrenOfParent(parent);

            var child = GetReusableChildHost();
            EnsureHostOnCanvas(child);
            if (!_childHosts.Contains(child))
                _childHosts.Add(child);
            ApplyPopupZOrder();

            child.SetMiningContext(_currentMiningContext);
            var injectSw = Stopwatch.StartNew();
            await child.ShowResultsWarmAsync(results, _currentStyles, _displaySettings, _currentTheme, _currentAudioSettings, _currentAnkiSettings, traceId: traceId);
            Log.Information(
                "[LookupTrace] trace={TraceId} child popup content injected in {Ms}ms entries={EntryCount}",
                traceId, injectSw.ElapsedMilliseconds, results.Count);

            // Make sure canvas is visible for child popups
            _canvas.Visibility = Visibility.Visible;
            _canvas.IsHitTestVisible = true;

            PositionChildHost(child, parent, request);

            Log.Information("[DictOverlay] Child popup reused for redirect '{Query}'", query);
        }
        finally
        {
            _redirectSemaphore.Release();
        }
    }

    private void OnChildRedirectRequested(object? sender, DictionaryPopupRedirectRequest request)
    {
        _ = HandleRedirectAsync(request, sender as DictionaryLookupPopup);
    }

    private void OnChildTapOutsideRequested(object? sender, EventArgs e)
    {
        if (sender is not DictionaryLookupPopup host) return;
        ClearChildrenAfter(host);
    }

    private void OnChildScrolled(object? sender, EventArgs e)
    {
        if (sender is DictionaryLookupPopup host)
            ClearChildrenAfter(host);
    }

    private void RemoveChild(DictionaryLookupPopup host)
    {
        ClearChildrenAfter(host);
        HideChildHost(host);
        _childHosts.Remove(host);
    }

    private void ClearChildren()
    {
        foreach (var child in _childHosts)
        {
            HideChildHost(child);
        }
        _childHosts.Clear();
        ApplyPopupZOrder();
    }

    private void ClearChildrenAfter(DictionaryLookupPopup parent)
    {
        var parentIndex = _childHosts.IndexOf(parent);
        if (parentIndex < 0)
        {
            ClearChildren();
            return;
        }

        for (var i = _childHosts.Count - 1; i > parentIndex; i--)
        {
            HideChildHost(_childHosts[i]);
            _childHosts.RemoveAt(i);
        }
        ApplyPopupZOrder();
    }

    private void CloseChildrenOfParent(DictionaryLookupPopup parent)
    {
        if (ReferenceEquals(parent, _rootHost))
            ClearChildren();
        else
            ClearChildrenAfter(parent);
    }

    private void ResetRedirectDeduplication()
    {
        _lastRedirectQuery = "";
        _lastRedirectParent = null;
    }

    private static DictionaryLookupPopup CreateHost()
    {
        return new DictionaryLookupPopup();
    }

    private DictionaryLookupPopup GetReusableChildHost()
    {
        foreach (var host in _childHostPool)
        {
            if (!_childHosts.Contains(host))
                return host;
        }

        var child = CreateHost();
        child.RedirectRequested += OnChildRedirectRequested;
        child.TapOutsideRequested += OnChildTapOutsideRequested;
        child.Scrolled += OnChildScrolled;
        _childHostPool.Add(child);
        return child;
    }

    private static void HideChildHost(DictionaryLookupPopup host)
    {
        host.Hide();
    }

    private void EnsureHostOnCanvas(DictionaryLookupPopup host)
    {
        if (!_canvas.Children.Contains(host.VisualRoot))
            _canvas.Children.Add(host.VisualRoot);
    }

    private void ApplyPopupZOrder()
    {
        if (_rootWarm && _embeddedPanel == null)
            Canvas.SetZIndex(_rootHost.VisualRoot, RootPopupZIndex);

        for (var i = 0; i < _childHosts.Count; i++)
            Canvas.SetZIndex(_childHosts[i].VisualRoot, ChildPopupZIndexBase + i * PopupZIndexStep);
    }

    private void OnOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_rootVisible) return;

            var point = e.GetCurrentPoint(_canvas).Position;
            if (IsPointInsideVisibleHost(point.X, point.Y))
                return;

            Dismiss();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictOverlay] OnOverlayPointerPressed crashed");
        }
    }

    private bool IsPointInsideVisibleHost(double x, double y)
    {
        return IsPointInsideHost(_rootHost, x, y)
            || _childHosts.Exists(host => IsPointInsideHost(host, x, y));
    }

    private bool IsPointInsideHost(DictionaryLookupPopup host, double x, double y)
    {
        if (host.VisualRoot.Visibility != Visibility.Visible
            || host.VisualRoot.Opacity <= 0
            || !host.VisualRoot.IsHitTestVisible)
            return false;

        var (left, top) = GetHostCanvasPosition(host);

        var width = host.VisualRoot.ActualWidth > 0 ? host.VisualRoot.ActualWidth : host.VisualRoot.Width;
        var height = host.VisualRoot.ActualHeight > 0 ? host.VisualRoot.ActualHeight : host.VisualRoot.Height;
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    private void UpdateCanvasBounds(XamlRoot xamlRoot)
    {
        _currentXamlRoot = xamlRoot;
        if (_canvas.Parent != null)
        {
            _canvas.Width = double.NaN;
            _canvas.Height = double.NaN;
            return;
        }

        _canvas.Width = xamlRoot.Size.Width;
        _canvas.Height = xamlRoot.Size.Height;
    }

    private void CollapseCanvasBounds()
    {
        if (_canvas.Parent != null)
        {
            _canvas.Width = double.NaN;
            _canvas.Height = double.NaN;
        }
        else
        {
            _canvas.Width = 0;
            _canvas.Height = 0;
        }

        if (_embeddedPanel == null)
            _canvas.Visibility = Visibility.Collapsed;
    }

    // --- Positioning (aligned with Hoshi Reader Mac PopupLayout) ---

    private void PositionHost(
        DictionaryLookupPopup host,
        double selectionX, double selectionY,
        double selectionWidth, double selectionHeight,
        bool isVertical,
        bool isRoot = false)
    {
        var (screenWidth, screenHeight) = GetOverlaySize();

        double maxWidth, maxHeight;

        if (isRoot && !isVertical)
        {
            maxWidth = Math.Min(screenWidth * 0.88, MaxPopupWidth);
            maxHeight = Math.Min(screenHeight * 0.72, MaxPopupHeight);
        }
        else if (isRoot && isVertical)
        {
            maxWidth = Math.Min(screenWidth * 0.55, MaxPopupWidth);
            maxHeight = Math.Min(screenHeight * 0.80, MaxPopupHeight);
        }
        else
        {
            maxWidth = Math.Min(screenWidth * 0.60, 600);
            maxHeight = Math.Min(screenHeight * 0.50, 520);
        }

        var layout = DictionaryPopupLayoutCalculator.Resolve(
            new DictionaryPopupAnchorRect(selectionX, selectionY, selectionWidth, selectionHeight),
            screenWidth,
            screenHeight,
            maxWidth,
            maxHeight,
            MinPopupWidth,
            isVertical);
        host.SetSize(layout.Width, layout.Height);
        Canvas.SetLeft(host.VisualRoot, layout.Left);
        Canvas.SetTop(host.VisualRoot, layout.Top);
    }

    private void PositionChildHost(
        DictionaryLookupPopup host,
        DictionaryLookupPopup parentHost,
        DictionaryPopupRedirectRequest request)
    {
        var (parentLeft, parentTop) = GetHostCanvasPosition(parentHost);

        if (request.X is double x && request.Y is double y)
        {
            var anchorX = parentLeft + x;
            var anchorY = parentTop + y;
            var anchorWidth = request.Width.GetValueOrDefault(1);
            var anchorHeight = request.Height.GetValueOrDefault(1);
            PositionHost(host, anchorX, anchorY, anchorWidth, anchorHeight, isVertical: false);
            return;
        }

        PositionHostAboveOrBelowParent(host, parentHost);
    }

    private static async Task HighlightPopupSelectionAsync(
        DictionaryLookupPopup parent,
        string matchedText)
    {
        try
        {
            await parent.HighlightSelectionAsync(matchedText);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DictOverlay] Failed to highlight popup selection");
        }
    }

    private void PositionHostAboveOrBelowParent(
        DictionaryLookupPopup host,
        DictionaryLookupPopup parentHost,
        double? preferredLeft = null)
    {
        var (screenWidth, screenHeight) = GetOverlaySize();
        var (parentLeft, parentTop, parentWidth, parentHeight) = GetHostBounds(parentHost);

        var maxWidth = Math.Min(screenWidth * 0.60, 600);
        var maxHeight = Math.Min(screenHeight * 0.50, 520);
        var width = ClampDimension(
            Math.Min(screenWidth - ScreenBorderPadding * 2, maxWidth),
            MinPopupWidth,
            maxWidth);

        var spaceAbove = SpaceAbove(parentTop);
        var spaceBelow = SpaceBelow(parentTop, parentHeight, screenHeight);
        var showBelow = ShowBelow(parentTop, parentHeight, screenHeight, maxHeight);
        var availableHeight = showBelow ? spaceBelow : spaceAbove;
        var height = ClampPopupExtent(availableHeight - ScreenBorderPadding, maxHeight);

        var desiredLeft = preferredLeft ?? parentLeft + (parentWidth - width) / 2;
        var centerX = Clamp(
            desiredLeft + width / 2,
            width / 2 + ScreenBorderPadding,
            screenWidth - width / 2 - ScreenBorderPadding);
        var centerY = showBelow
            ? parentTop + parentHeight + PopupPadding + height / 2
            : parentTop - PopupPadding - height / 2;
        centerY = Clamp(
            centerY,
            height / 2 + ScreenBorderPadding,
            screenHeight - height / 2 - ScreenBorderPadding);

        var (left, top) = ClampHostBounds(centerX - width / 2, centerY - height / 2, width, height, screenWidth, screenHeight);
        host.SetSize(width, height);
        Canvas.SetLeft(host.VisualRoot, left);
        Canvas.SetTop(host.VisualRoot, top);
    }

    private (double left, double top) GetHostCanvasPosition(DictionaryLookupPopup host)
    {
        if (_embeddedPanel != null && ReferenceEquals(host, _rootHost))
        {
            try
            {
                var transform = host.VisualRoot.TransformToVisual(_canvas);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                return (point.X, point.Y);
            }
            catch
            {
                return (0, 0);
            }
        }

        var left = Canvas.GetLeft(host.VisualRoot);
        var top = Canvas.GetTop(host.VisualRoot);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left, top);
    }

    private static (double x, double y) GetHostOffset(DictionaryLookupPopup host)
    {
        var left = Canvas.GetLeft(host.VisualRoot);
        var top = Canvas.GetTop(host.VisualRoot);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        return (left + host.VisualRoot.Width / 2, top + host.VisualRoot.Height / 2);
    }

    private (double left, double top, double width, double height) GetHostBounds(DictionaryLookupPopup host)
    {
        var (left, top) = GetHostCanvasPosition(host);
        var width = host.VisualRoot.ActualWidth > 0 ? host.VisualRoot.ActualWidth : host.VisualRoot.Width;
        var height = host.VisualRoot.ActualHeight > 0 ? host.VisualRoot.ActualHeight : host.VisualRoot.Height;
        if (double.IsNaN(width) || width <= 0) width = MinPopupWidth;
        if (double.IsNaN(height) || height <= 0) height = MinPopupHeight;
        return (left, top, width, height);
    }

    private static double SpaceLeft(double x) => x - PopupPadding;
    private static double SpaceRight(double x, double w, double screenWidth) => screenWidth - x - w - PopupPadding;
    private static double SpaceAbove(double y) => y - PopupPadding;
    private static double SpaceBelow(double y, double h, double screenHeight) => screenHeight - y - h - PopupPadding;
    private static bool ShowOnRight(double x, double w, double screenWidth, double popupWidth) => SpaceRight(x, w, screenWidth) >= SpaceLeft(x) || SpaceRight(x, w, screenWidth) >= popupWidth;
    private static bool ShowBelow(double y, double h, double screenHeight, double popupHeight) => SpaceBelow(y, h, screenHeight) >= SpaceAbove(y) || SpaceBelow(y, h, screenHeight) >= popupHeight;
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
    private static double ClampDimension(double value, double min, double max) => Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));
    private static double ClampPopupExtent(double value, double max) => Math.Clamp(value, 0, Math.Max(0, max));

    private (double width, double height) GetOverlaySize()
    {
        var width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : _canvas.Width;
        var height = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : _canvas.Height;
        if (double.IsNaN(width) || width <= 0)
            width = _currentXamlRoot?.Size.Width ?? 1920;
        if (double.IsNaN(height) || height <= 0)
            height = _currentXamlRoot?.Size.Height ?? 1080;
        return (width, height);
    }

    private static (double left, double top) ClampHostBounds(
        double left,
        double top,
        double width,
        double height,
        double screenWidth,
        double screenHeight)
    {
        var maxLeft = Math.Max(ScreenBorderPadding, screenWidth - width - ScreenBorderPadding);
        var maxTop = Math.Max(ScreenBorderPadding, screenHeight - height - ScreenBorderPadding);
        return (
            Clamp(left, ScreenBorderPadding, maxLeft),
            Clamp(top, ScreenBorderPadding, maxTop));
    }

    public void UpdateRootSize(double width, double height)
    {
        if (_rootWarm && _embeddedPanel != null)
        {
            _rootHost.SetSize(
                width > 0 ? width : double.NaN,
                height > 0 ? height : double.NaN);
        }
    }

    public void Dispose()
    {
        _canvas.PointerPressed -= OnOverlayPointerPressed;
        if (_rootWarm)
        {
            _rootHost.RedirectRequested -= OnRootRedirectRequested;
            _rootHost.TapOutsideRequested -= OnRootTapOutsideRequested;
            _rootHost.Scrolled -= OnRootScrolled;
            if (_embeddedPanel != null)
                _embeddedPanel.Children.Remove(_rootHost.VisualRoot);
            _rootHost.Dispose();
        }
        ClearChildren();
        ResetRedirectDeduplication();
        foreach (var pooledHost in _childHostPool)
        {
            pooledHost.RedirectRequested -= OnChildRedirectRequested;
            pooledHost.TapOutsideRequested -= OnChildTapOutsideRequested;
            pooledHost.Scrolled -= OnChildScrolled;
            if (_canvas.Children.Contains(pooledHost.VisualRoot))
                _canvas.Children.Remove(pooledHost.VisualRoot);
            pooledHost.Dispose();
        }
        _childHostPool.Clear();
        _redirectSemaphore.Dispose();
        _canvas.IsHitTestVisible = false;
        CollapseCanvasBounds();
    }

    public void Dismiss()
    {
        Log.Information("[Lifecycle] Popup dismissed: wasVisible={WasVisible}", _rootVisible);
        var wasVisible = _rootVisible;
        if (_rootWarm)
            _rootHost.Hide();
        ClearChildren();
        ResetRedirectDeduplication();
        _rootVisible = false;
        _canvas.IsHitTestVisible = false;
        CollapseCanvasBounds();

        if (_embeddedPanel != null)
            _embeddedPanel.Visibility = Visibility.Collapsed;

        if (wasVisible)
            Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
