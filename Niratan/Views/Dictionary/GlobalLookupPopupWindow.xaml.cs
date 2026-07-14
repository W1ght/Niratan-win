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
using Windows.Graphics;
using Windows.System;

namespace Niratan.Views.Dictionary;

public sealed partial class GlobalLookupPopupWindow : Window, IDisposable
{
    private readonly TaskCompletionSource _loadedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private DictionaryPopupOverlay? _popupOverlay;
    private DictionaryDesktopAcrylicThinBackdrop? _desktopAcrylicThinBackdrop;
    private Windows.UI.Color _hostSurfaceColor = Windows.UI.Color.FromArgb(0xFF, 0x24, 0x24, 0x24);
    private bool _canDismissOnDeactivate;
    private bool _isDisposed;

    public GlobalLookupPopupWindow()
    {
        InitializeComponent();
        Title = "Niratan Lookup Popup";
        ExtendsContentIntoTitleBar = true;
        _desktopAcrylicThinBackdrop = DictionaryPopupMaterial.TryApplyDesktopAcrylicThin(this, RootGrid);
        RootGrid.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        DictionaryOverlayCanvas.Background = DictionaryPopupMaterial.CreateTransparentBrush();
        RootGrid.Loaded += OnLoaded;
        Activated += OnActivated;
        Closed += OnClosed;

        AppWindow.SetIcon("Assets/AppIcon.ico");

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        ApplyBorderlessHostChrome();
    }

    public async Task ShowRequestAsync(
        DictionaryPopupRequest request,
        PointInt32 anchorPoint,
        CancellationToken ct = default)
    {
        await _loadedCompletion.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        var overlay = EnsurePopupOverlay();
        ApplyPopupSizedHostSurface(request.Theme);

        var xamlRoot = RootGrid.XamlRoot;
        var anchorX = Math.Clamp(anchorPoint.X, 0, Math.Max(1, xamlRoot.Size.Width - 1));
        var anchorY = Math.Clamp(anchorPoint.Y, 0, Math.Max(1, xamlRoot.Size.Height - 1));

        await overlay.ShowLookupAsync(
            request.Results,
            request.Styles,
            request.DisplaySettings,
            anchorX,
            anchorY,
            1,
            1,
            xamlRoot,
            isVertical: false,
            request.Theme,
            request.AudioSettings,
            request.AnkiSettings,
            request.MiningContext,
            request.TraceId);

        _canDismissOnDeactivate = true;
    }

    public DictionaryPopupHostBounds? GetRootPopupBounds() =>
        _popupOverlay?.GetRootPopupBounds();

    public void MoveRootPopupToOrigin() =>
        _popupOverlay?.MoveRootPopupToOrigin();

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

    public void ApplyRoundedHostRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var size = AppWindow.Size;
        ApplyRoundedHostRegion(hwnd, size.Width, size.Height, RasterizationScale);
    }

    public void ClearHostRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    private DictionaryPopupOverlay EnsurePopupOverlay()
    {
        if (_popupOverlay is null)
        {
            _popupOverlay = new DictionaryPopupOverlay();
            _popupOverlay.Dismissed += OnPopupOverlayDismissed;
        }

        _popupOverlay.UseCanvas(DictionaryOverlayCanvas);
        _popupOverlay.UseStandaloneWindowVisuals();
        _popupOverlay.SetRootReadyOpacity(1);
        return _popupOverlay;
    }

    private void ApplyPopupSizedHostSurface(ThemeMode themeMode)
    {
        _desktopAcrylicThinBackdrop?.SetTheme(themeMode);
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

        var borderColor = ToColorRef(hostSurfaceColor);
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

    private static uint ToColorRef(Windows.UI.Color color) =>
        color.R | ((uint)color.G << 8) | ((uint)color.B << 16);

    private static void ApplyNativeBorderlessHostStyles(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsBorder | WsDlgFrame | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
        style |= WsPopup | WsClipChildren | WsClipSiblings;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle &= ~(WsExAppWindow | WsExDlgModalFrame | WsExClientEdge | WsExStaticEdge);
        exStyle |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    private static void ApplyRoundedHostRegion(
        IntPtr hwnd,
        int width,
        int height,
        double rasterizationScale)
    {
        if (width <= 0 || height <= 0)
            return;

        var radius = Math.Max(1, (int)Math.Ceiling(PopupCornerRadiusDip * rasterizationScale));
        var diameter = radius * 2;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero)
            return;

        if (SetWindowRgn(hwnd, region, true) == 0)
            DeleteObject(region);
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
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_canDismissOnDeactivate
            || args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        Close();
    }

    private void OnPopupOverlayDismissed(object? sender, EventArgs e)
    {
        Close();
    }

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
        Activated -= OnActivated;
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

    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
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
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

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
}
