using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Enums;
using Niratan.Models.Dictionary;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Graphics;
using Windows.System;

namespace Niratan.Views.Dictionary;

public sealed partial class GlobalLookupPopupWindow : Window, IDisposable
{
    private readonly TaskCompletionSource _loadedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private DictionaryPopupOverlay? _popupOverlay;
    private Windows.UI.Color _hostSurfaceColor = Windows.UI.Color.FromArgb(0xFF, 0x24, 0x24, 0x24);
    private bool _isDisposed;

    public event EventHandler<DictionaryPopupContentCommittedEventArgs>? PopupContentCommitted;
    public event EventHandler<DictionaryPopupExternalChildRequest>? ChildPopupRequested;
    public event EventHandler? PopupSurfaceClicked;

    public GlobalLookupPopupWindow()
    {
        InitializeComponent();
        Title = "Niratan Lookup Popup";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = null;
        RootGrid.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        DictionaryOverlayCanvas.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        RootGrid.Loaded += OnLoaded;
        Closed += OnClosed;

        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        ApplyBorderlessHostChrome();
    }

    public async Task PrewarmAsync(
        ThemeMode themeMode = ThemeMode.System,
        CancellationToken ct = default)
    {
        await _loadedCompletion.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        ApplyPopupSizedHostSurface(themeMode);
        var overlay = EnsurePopupOverlay();
        await overlay.PrewarmAsync(RootGrid.XamlRoot, themeMode);
    }

    public async Task ShowRequestAsync(
        DictionaryPopupRequest request,
        RectInt32 anchorScreenBounds,
        RectInt32 workArea,
        double displayScale,
        CancellationToken ct = default)
    {
        await _loadedCompletion.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        StatusSurface.Visibility = Visibility.Collapsed;
        DictionaryOverlayCanvas.Visibility = Visibility.Visible;
        var overlay = EnsurePopupOverlay();
        ApplyPopupSizedHostSurface(request.Theme);

        var xamlRoot = RootGrid.XamlRoot;
        var scale = Math.Max(0.01, displayScale);
        var anchorX = Math.Clamp(
            (anchorScreenBounds.X - workArea.X) / scale,
            0,
            Math.Max(1, xamlRoot.Size.Width - 1));
        var anchorY = Math.Clamp(
            (anchorScreenBounds.Y - workArea.Y) / scale,
            0,
            Math.Max(1, xamlRoot.Size.Height - 1));
        var anchorWidth = Math.Max(1 / scale, anchorScreenBounds.Width / scale);
        var anchorHeight = Math.Max(1 / scale, anchorScreenBounds.Height / scale);

        await overlay.ShowLookupAsync(
            request.Results,
            request.Styles,
            request.DisplaySettings,
            anchorX,
            anchorY,
            anchorWidth,
            anchorHeight,
            xamlRoot,
            isVertical: false,
            request.Theme,
            request.AudioSettings,
            request.AnkiSettings,
            request.MiningContext,
            traceId: request.TraceId,
            cancellationToken: ct,
            layoutViewportWidth: workArea.Width / scale,
            layoutViewportHeight: workArea.Height / scale);

    }

    public async Task ShowStatusAsync(
        string message,
        ThemeMode themeMode,
        CancellationToken ct = default)
    {
        await _loadedCompletion.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        ApplyPopupSizedHostSurface(themeMode);
        DictionaryOverlayCanvas.Visibility = Visibility.Collapsed;
        StatusTextBlock.Text = message;
        StatusSurface.Visibility = Visibility.Visible;
    }

    public DictionaryPopupHostBounds? GetRootPopupBounds() =>
        _popupOverlay?.GetRootPopupBounds();

    public void ActivateForStaging(RectInt32 stagingRect)
    {
        // Moving and activating can make the presenter restore native chrome and
        // can also undo an empty region. Keep the host protected until it is
        // safely offscreen, then let WebView2 compose in a real, uncloaked HWND.
        PrepareForStaging();
        AppWindow.MoveAndResize(stagingRect);
        ApplyBorderlessHostChrome();
        Activate();
        ApplyBorderlessHostChrome();
        ClearHostRegion();
        RootGrid.Opacity = 1;
        RootGrid.UpdateLayout();
        SetHostCloaked(cloaked: false);
    }

    public void PrepareForStaging()
    {
        SetHostCloaked(cloaked: true);
        RootGrid.Opacity = 0;
        StatusSurface.Visibility = Visibility.Collapsed;
        DictionaryOverlayCanvas.Visibility = Visibility.Visible;
        ApplyEmptyHostRegion();
    }

    public void MoveRootPopupToOriginAndResize(SizeInt32 pixelSize, double displayScale)
    {
        _popupOverlay?.MoveRootPopupToOrigin();
        var scale = Math.Max(0.01, displayScale);
        _popupOverlay?.SetRootPopupSize(
            Math.Max(1, pixelSize.Width / scale),
            Math.Max(1, pixelSize.Height / scale));
    }

    public async Task RevealFinalSurfaceAsync(
        RectInt32 surfaceRect,
        double displayScale,
        CancellationToken ct = default)
    {
        ApplyBorderlessHostChrome();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        ct.ThrowIfCancellationRequested();
        var width = Math.Max(1, surfaceRect.Width);
        var height = Math.Max(1, surfaceRect.Height);
        if (!GetWindowRect(hwnd, out var stagingRect))
            return;

        // Resize and compose at the final size while the HWND is still offscreen.
        // The last operation below is move-only, so no unpainted WebView surface
        // can be exposed at the on-screen destination.
        var insets = ResolveClientInsets(hwnd);
        SetWindowPos(
            hwnd,
            HwndTopMost,
            stagingRect.Left,
            stagingRect.Top,
            width + insets.Horizontal,
            height + insets.Vertical,
            SwpNoActivate);

        var resolvedInsets = ResolveClientInsets(hwnd);
        if (resolvedInsets != insets)
        {
            insets = resolvedInsets;
            SetWindowPos(
                hwnd,
                HwndTopMost,
                stagingRect.Left,
                stagingRect.Top,
                width + insets.Horizontal,
                height + insets.Vertical,
                SwpNoActivate);
            insets = ResolveClientInsets(hwnd);
        }

        ApplyRoundedHostRegion(
            hwnd,
            insets.Left,
            insets.Top,
            width,
            height,
            displayScale);
        RootGrid.Opacity = 1;
        await WaitForFinalCompositionAsync(ct);
        ct.ThrowIfCancellationRequested();

        SetWindowPos(
            hwnd,
            HwndTopMost,
            surfaceRect.X - insets.Left,
            surfaceRect.Y - insets.Top,
            0,
            0,
            SwpNoSize | SwpNoActivate);
        LogFinalWindowGeometry(hwnd, surfaceRect, insets);
    }

    public PointInt32 GetPopupSurfaceScreenOrigin()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var origin = new POINT(0, 0);
        return hwnd != IntPtr.Zero && ClientToScreen(hwnd, ref origin)
            ? new PointInt32(origin.X, origin.Y)
            : AppWindow.Position;
    }

    public double RasterizationScale =>
        RootGrid.XamlRoot?.RasterizationScale ?? 1;

    public void ApplyBorderlessHostChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        ApplyNativeBorderlessHostStyles(hwnd);
        ApplyDwmBorderlessChrome(hwnd, _hostSurfaceColor);
    }

    public void ApplyRoundedHostRegion(
        SizeInt32? pixelSize = null,
        double? displayScale = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var insets = ResolveClientInsets(hwnd);
        var size = pixelSize ?? new SizeInt32(
            Math.Max(1, AppWindow.Size.Width - insets.Horizontal),
            Math.Max(1, AppWindow.Size.Height - insets.Vertical));
        ApplyRoundedHostRegion(
            hwnd,
            insets.Left,
            insets.Top,
            size.Width,
            size.Height,
            displayScale ?? RasterizationScale);
    }

    public void ClearHostRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    private void ApplyEmptyHostRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero)
            return;

        if (SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region);
    }

    private void SetHostCloaked(bool cloaked)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var value = cloaked ? 1 : 0;
        var result = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
        if (result != 0)
        {
            Log.Warning(
                "[GlobalLookup] DWM cloak update failed cloaked={Cloaked} hresult=0x{HResult:X8}",
                cloaked,
                result);
        }
    }

    private async Task WaitForFinalCompositionAsync(CancellationToken ct)
    {
        var layoutUpdated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? layoutHandler = null;
        layoutHandler = (_, _) => layoutUpdated.TrySetResult();
        RootGrid.LayoutUpdated += layoutHandler;
        try
        {
            RootGrid.InvalidateMeasure();
            RootGrid.UpdateLayout();
            try
            {
                await layoutUpdated.Task.WaitAsync(TimeSpan.FromMilliseconds(80), ct);
            }
            catch (TimeoutException)
            {
                Log.Debug("[GlobalLookup] Final staging layout event timed out; continuing with rendered frames");
            }
        }
        finally
        {
            RootGrid.LayoutUpdated -= layoutHandler;
        }

        await WaitForNextCompositionFrameAsync(ct);
        await WaitForNextCompositionFrameAsync(ct);
        var flushResult = DwmFlush();
        if (flushResult != 0)
        {
            Log.Warning(
                "[GlobalLookup] DwmFlush failed before final popup move hresult=0x{HResult:X8}",
                flushResult);
        }
    }

    private static async Task WaitForNextCompositionFrameAsync(CancellationToken ct)
    {
        var rendered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? renderingHandler = null;
        renderingHandler = (_, _) => rendered.TrySetResult();
        CompositionTarget.Rendering += renderingHandler;
        try
        {
            try
            {
                await rendered.Task.WaitAsync(TimeSpan.FromMilliseconds(80), ct);
            }
            catch (TimeoutException)
            {
                Log.Debug("[GlobalLookup] Composition frame timed out while staging popup");
            }
        }
        finally
        {
            CompositionTarget.Rendering -= renderingHandler;
        }
    }

    private static void LogFinalWindowGeometry(
        IntPtr hwnd,
        RectInt32 surfaceRect,
        WindowFrameInsets insets)
    {
        if (!GetWindowRect(hwnd, out var windowRect)
            || !GetClientRect(hwnd, out var clientRect))
        {
            return;
        }

        var clientOrigin = new POINT(0, 0);
        ClientToScreen(hwnd, ref clientOrigin);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        Log.Information(
            "[GlobalLookup] Final HWND geometry surface=({SurfaceX},{SurfaceY},{SurfaceWidth},{SurfaceHeight}) outer=({OuterLeft},{OuterTop},{OuterWidth},{OuterHeight}) client=({ClientX},{ClientY},{ClientWidth},{ClientHeight}) insets=({InsetLeft},{InsetTop},{InsetRight},{InsetBottom}) style=0x{Style:X} exStyle=0x{ExStyle:X}",
            surfaceRect.X,
            surfaceRect.Y,
            surfaceRect.Width,
            surfaceRect.Height,
            windowRect.Left,
            windowRect.Top,
            Math.Max(0, windowRect.Right - windowRect.Left),
            Math.Max(0, windowRect.Bottom - windowRect.Top),
            clientOrigin.X,
            clientOrigin.Y,
            Math.Max(0, clientRect.Right - clientRect.Left),
            Math.Max(0, clientRect.Bottom - clientRect.Top),
            insets.Left,
            insets.Top,
            insets.Right,
            insets.Bottom,
            style,
            exStyle);
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay is null)
        {
            _popupOverlay = new DictionaryPopupOverlay();
            _popupOverlay.Dismissed += OnPopupOverlayDismissed;
            _popupOverlay.RootContentCommitted += OnRootContentCommitted;
            _popupOverlay.ExternalChildRequested += OnExternalChildRequested;
            _popupOverlay.ExternalTapInsideRequested += OnExternalTapInsideRequested;
        }

        _popupOverlay.UseCanvas(DictionaryOverlayCanvas);
        _popupOverlay.UseNakedFloatingWindowVisuals();
        _popupOverlay.UseExternalChildWindows();
        // A WebView2 hosted in a separate HWND cannot safely fade its parent
        // before the desktop compositor has presented that HWND. The window is
        // staged offscreen and moved in as one fully composed surface instead.
        _popupOverlay.UseImmediateOpacityTransitions();
        _popupOverlay.SetRootReadyOpacity(1);
        return _popupOverlay;
    }

    private void ApplyPopupSizedHostSurface(ThemeMode themeMode)
    {
        _hostSurfaceColor = DictionaryPopupMaterial.GetOpaqueSurfaceColor(themeMode);
        RootGrid.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        DictionaryOverlayCanvas.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        ApplyBorderlessHostChrome();
    }

    private static void ApplyDwmBorderlessChrome(IntPtr hwnd, Windows.UI.Color hostSurfaceColor)
    {
        var ncRenderingPolicy = DwmNcRenderingPolicyDisabled;
        DwmSetWindowAttribute(
            hwnd,
            DwmwaNcRenderingPolicy,
            ref ncRenderingPolicy,
            sizeof(int));

        var cornerPreference = DwmWindowCornerPreferenceDoNotRound;
        DwmSetWindowAttribute(
            hwnd,
            DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        _ = hostSurfaceColor;
        var borderColor = DwmColorNone;
        DwmSetWindowAttribute(
            hwnd,
            DwmwaBorderColor,
            ref borderColor,
            sizeof(uint));

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static void ApplyNativeBorderlessHostStyles(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsBorder | WsDlgFrame | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
        style |= WsPopup | WsClipChildren | WsClipSiblings;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle &= ~(
            WsExAppWindow
            | WsExDlgModalFrame
            | WsExWindowEdge
            | WsExClientEdge
            | WsExStaticEdge);
        exStyle |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    private static void ApplyRoundedHostRegion(
        IntPtr hwnd,
        int left,
        int top,
        int width,
        int height,
        double rasterizationScale)
    {
        if (width <= 0 || height <= 0)
            return;

        var radius = Math.Max(1, (int)Math.Ceiling(PopupCornerRadiusDip * rasterizationScale));
        var diameter = radius * 2;
        var region = CreateRoundRectRgn(
            left,
            top,
            left + width,
            top + height,
            diameter,
            diameter);
        if (region == IntPtr.Zero)
            return;

        if (SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region);
    }

    private static WindowFrameInsets ResolveClientInsets(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var windowRect)
            || !GetClientRect(hwnd, out var clientRect))
        {
            return default;
        }

        var clientOrigin = new POINT(0, 0);
        if (!ClientToScreen(hwnd, ref clientOrigin))
            return default;

        var clientWidth = Math.Max(0, clientRect.Right - clientRect.Left);
        var clientHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
        return new WindowFrameInsets(
            Math.Max(0, clientOrigin.X - windowRect.Left),
            Math.Max(0, clientOrigin.Y - windowRect.Top),
            Math.Max(0, windowRect.Right - clientOrigin.X - clientWidth),
            Math.Max(0, windowRect.Bottom - clientOrigin.Y - clientHeight));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loadedCompletion.Task.IsCompleted)
            _loadedCompletion.SetResult();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;

        e.Handled = true;
        if (DictionaryOverlayCanvas.Visibility == Visibility.Visible
            && _popupOverlay is not null)
        {
            _popupOverlay.Dismiss();
        }
        else
        {
            Close();
        }
    }

    private void OnPopupOverlayDismissed(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnRootContentCommitted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        PopupContentCommitted?.Invoke(this, e);
    }

    private void OnExternalChildRequested(
        object? sender,
        DictionaryPopupExternalChildRequest request) =>
        ChildPopupRequested?.Invoke(this, request);

    private void OnExternalTapInsideRequested(object? sender, EventArgs e) =>
        PopupSurfaceClicked?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        RootGrid.Loaded -= OnLoaded;
        Closed -= OnClosed;
        if (_popupOverlay is not null)
        {
            _popupOverlay.Dismissed -= OnPopupOverlayDismissed;
            _popupOverlay.RootContentCommitted -= OnRootContentCommitted;
            _popupOverlay.ExternalChildRequested -= OnExternalChildRequested;
            _popupOverlay.ExternalTapInsideRequested -= OnExternalTapInsideRequested;
            _popupOverlay.Dispose();
            _popupOverlay = null;
        }
    }

    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaCloak = 13;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const int DwmNcRenderingPolicyDisabled = 1;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private const double PopupCornerRadiusDip = 8;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsPopup = 0x80000000L;
    private const long WsClipChildren = 0x02000000L;
    private const long WsClipSiblings = 0x04000000L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExDlgModalFrame = 0x00000001L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopMost = new(-1);

    private readonly record struct WindowFrameInsets(
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public int Horizontal => Left + Right;
        public int Vertical => Top + Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref uint pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, unchecked((int)dwNewLong.ToInt64())));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(
        int x1,
        int y1,
        int x2,
        int y2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

}
