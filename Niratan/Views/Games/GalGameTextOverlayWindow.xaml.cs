using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Niratan.Models.Games;
using Niratan.Services.Video;
using Windows.Foundation;
using Windows.Graphics;

namespace Niratan.Views.Games;

public sealed partial class GalGameTextOverlayWindow : Window
{
    public ObservableCollection<GalGameThreadPreview> Threads { get; } = [];
    public ObservableCollection<GalGameTextLine> Lines { get; } = [];

    public Func<GalGameTextLine, int, string?, RectInt32, Task>? LookupRequested { get; set; }
    public Func<GalGameThreadPreview, Task>? ThreadSelected { get; set; }
    public Func<Task>? RefreshRequested { get; set; }
    public Func<Task>? StopRequested { get; set; }
    public Func<string, Task>? ToolbarActionRequested { get; set; }

    public event EventHandler? Hidden;

    private GalGameTextLine? _currentLine;
    private bool _following = true;
    private bool _transparent;
    private bool _passThrough;
    private bool _locked;
    private bool _topmost = true;
    private bool _isDragging;
    private UIElement? _dragOwner;
    private PointInt32 _dragStartCursor;
    private PointInt32 _dragStartWindow;
    private bool _isResizing;
    private ResizeEdges _resizeEdges;
    private PointInt32 _resizeStartCursor;
    private PointInt32 _resizeStartWindow;
    private SizeInt32 _resizeStartSize;

    public GalGameTextOverlayWindow()
    {
        InitializeComponent();
        Title = "Galgame Capture";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = null;
        RootGrid.ActualThemeChanged += RootGridActualThemeChanged;
        ApplySurfaceBackground();
        // Match Fushi's focused lookup strip: wide enough for a full dialogue
        // line, but not a full-screen black bar. The user can still resize it
        // from the custom content edges afterwards.
        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Nearest);
        var workArea = displayArea?.WorkArea ?? new RectInt32(0, 0, 2240, 900);
        var defaultHeight = Math.Min(DefaultHeight, Math.Max(MinimumHeight, workArea.Height));
        var defaultWidth = Math.Clamp(
            (int)Math.Round(workArea.Width * DefaultWidthRatio),
            Math.Min(MinimumDefaultWidth, workArea.Width),
            Math.Min(MaximumDefaultWidth, workArea.Width));
        AppWindow.Resize(new SizeInt32(defaultWidth, defaultHeight));
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - defaultWidth) / 2),
            workArea.Y + Math.Min(DefaultTopOffset, Math.Max(0, workArea.Height - defaultHeight))));
        var presenter = OverlappedPresenter.CreateForContextMenu();
        // Fushi owns resizing in the content edge. Keeping the Win32
        // thick-frame style would reintroduce a native non-client rim.
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        ApplyBorderlessChrome();
        ApplyToolbarVisuals();
        Closed += OnClosed;
    }

    public void UpdateSnapshot(
        System.Collections.Generic.IReadOnlyList<GalGameThreadPreview> threads,
        System.Collections.Generic.IReadOnlyList<GalGameTextLine> lines,
        string status,
        ulong? selectedThreadId = null)
    {
        if (!Threads.SequenceEqual(threads, GalGameThreadPreviewComparer.Instance))
        {
            Threads.Clear();
            foreach (var thread in threads)
                Threads.Add(thread);
        }

        var visibleLines = selectedThreadId is > 0
            ? lines.Where(line => line.ThreadId == selectedThreadId.Value).ToArray()
            : lines;
        var sameLines = Lines.Count == visibleLines.Count
            && Lines.Zip(visibleLines).All(pair =>
                string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                && string.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal));
        if (!sameLines)
        {
            Lines.Clear();
            foreach (var line in visibleLines)
                Lines.Add(line);
        }

        var nextLine = _following
            ? Lines.LastOrDefault()
            : Lines.FirstOrDefault(line => line.Id == _currentLine?.Id) ?? _currentLine;
        if (nextLine is not null && Lines.All(line => line.Id != nextLine.Id))
            nextLine = Lines.LastOrDefault();
        SetCurrentLine(nextLine);

        // Fushi keeps capture readiness in the workbench. The floating strip
        // is reserved for the sentence and its direct controls, so session
        // status prose is deliberately not rendered here.
        _ = status;
    }

    public void ShowOverlay()
    {
        _isDragging = false;
        ToolbarPanel.Visibility = Visibility.Collapsed;
        Grip.Visibility = Visibility.Visible;
        Activate();
        ApplyBorderlessChrome();
        ApplyToolbarVisuals();
    }

    public void HideOverlay()
    {
        EndDrag(null);
        AppWindow.Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private void SetCurrentLine(GalGameTextLine? line)
    {
        _currentLine = line;
        CurrentTextCanvas.Invalidate();
    }

    private void SurfacePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Grip.Visibility = Visibility.Collapsed;
        ToolbarPanel.Visibility = Visibility.Visible;
    }

    private void SurfacePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_isDragging || _isResizing)
            return;
        Grip.Visibility = Visibility.Visible;
        ToolbarPanel.Visibility = Visibility.Collapsed;
    }

    private async void ToolbarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;

        switch (action)
        {
            case "toggleFollow":
                _following = !_following;
                if (_following)
                    SetCurrentLine(Lines.LastOrDefault());
                ApplyToolbarVisuals();
                return;
            case "toggleTransparency":
                _transparent = !_transparent;
                ApplySurfaceBackground();
                ApplyToolbarVisuals();
                return;
            case "togglePassThrough":
                _passThrough = !_passThrough;
                // Keep the toolbar itself usable while the text surface is
                // temporarily inert. The native Fushi overlay has a separate
                // escape-hatch toolbar; this compact WinUI port keeps that
                // escape hatch in the same top strip.
                CurrentTextCanvas.IsHitTestVisible = !_passThrough;
                ApplyToolbarVisuals();
                return;
            case "lock":
                _locked = !_locked;
                if (_locked)
                    EndDrag(null);
                LockGlyph.Text = _locked ? "🔒" : "🔓";
                ApplyBorderlessChrome();
                ApplyToolbarVisuals();
                return;
            case "topmost":
                _topmost = !_topmost;
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.IsAlwaysOnTop = _topmost;
                ApplyToolbarVisuals();
                return;
            case "close":
                HideOverlay();
                return;
            default:
                if (ToolbarActionRequested is { } callback)
                    await callback(action);
                return;
        }
    }

    private void ApplyToolbarVisuals()
    {
        SetButtonActive(FollowButton, _following);
        SetButtonActive(PassThroughButton, _passThrough);
        SetButtonActive(TransparencyButton, _transparent);
        SetButtonActive(LockButton, _locked);
        SetButtonActive(TopmostButton, _topmost);
    }

    private static void SetButtonActive(Button button, bool active) =>
        button.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(active ? (byte)0xE0 : (byte)0x30,
                active ? (byte)0xA8 : (byte)0xFF,
                active ? (byte)0x7A : (byte)0xFF,
                active ? (byte)0xFF : (byte)0xFF));

    private void RootGridActualThemeChanged(FrameworkElement sender, object args)
    {
        _ = sender;
        _ = args;
        ApplySurfaceBackground();
    }

    private void ApplySurfaceBackground()
    {
        var source = Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
            as SolidColorBrush;
        var color = source?.Color
            ?? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
        color.A = _transparent ? (byte)0x32 : (byte)0xFF;
        Surface.Background = new SolidColorBrush(color);
        RootGrid.Background = new SolidColorBrush(color);
    }

    private void CurrentTextCanvasDraw(
        CanvasControl sender,
        CanvasDrawEventArgs args)
    {
        _ = sender;
        args.DrawingSession.Clear(Colors.Transparent);
        if (_currentLine is not { Text.Length: > 0 } line)
            return;

        VideoSubtitleCanvasRenderer.Draw(
            args.DrawingSession,
            new Size(CurrentTextCanvas.ActualWidth, CurrentTextCanvas.ActualHeight),
            CreateCurrentTextCanvasOptions(line));
    }

    private async void CurrentTextCanvasPointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (_currentLine is not { Text.Length: > 0 } line
            || !e.GetCurrentPoint(CurrentTextCanvas).Properties.IsLeftButtonPressed
            || LookupRequested is not { } callback)
            return;

        var point = e.GetCurrentPoint(CurrentTextCanvas).Position;
        var size = new Size(CurrentTextCanvas.ActualWidth, CurrentTextCanvas.ActualHeight);
        if (!VideoSubtitleCanvasRenderer.TryHitTestCharacter(
                CanvasDevice.GetSharedDevice(),
                size,
                CreateCurrentTextCanvasOptions(line),
                point,
                out var hit))
        {
            return;
        }

        await callback(
            line,
            hit.CharacterIndex,
            null,
            GetScreenBounds(CurrentTextCanvas, hit.Bounds));
    }

    private static VideoSubtitleCanvasRenderOptions CreateCurrentTextCanvasOptions(
        GalGameTextLine line)
    {
        var brush = Application.Current.Resources["TextFillColorPrimaryBrush"]
            as SolidColorBrush;
        return new VideoSubtitleCanvasRenderOptions(
            Text: line.Text,
            FontFamily: "Segoe UI, Yu Gothic UI, Meiryo",
            FontSize: 26,
            FontWeight: 400,
            Foreground: brush?.Color ?? Colors.White,
            ShadowRadius: 0,
            MaskBlurRadius: 0,
            SelectionStart: -1,
            SelectionLength: 0,
            SelectionBackground: Colors.Transparent,
            SelectionForeground: brush?.Color ?? Colors.White);
    }

    private void RootGridPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        TryBeginResize(e);
    }

    private void RootGridPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        if (!_isResizing || !GetCursorPos(out var cursor))
            return;

        var dx = cursor.X - _resizeStartCursor.X;
        var dy = cursor.Y - _resizeStartCursor.Y;
        var width = _resizeStartSize.Width;
        var height = _resizeStartSize.Height;
        var x = _resizeStartWindow.X;
        var y = _resizeStartWindow.Y;

        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            width = Math.Max(MinimumWidth, _resizeStartSize.Width - dx);
            x = _resizeStartWindow.X + _resizeStartSize.Width - width;
        }
        else if (_resizeEdges.HasFlag(ResizeEdges.Right))
        {
            width = Math.Max(MinimumWidth, _resizeStartSize.Width + dx);
        }

        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            height = Math.Max(MinimumHeight, _resizeStartSize.Height - dy);
            y = _resizeStartWindow.Y + _resizeStartSize.Height - height;
        }
        else if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            height = Math.Max(MinimumHeight, _resizeStartSize.Height + dy);
        }

        AppWindow.Resize(new SizeInt32(width, height));
        if (x != _resizeStartWindow.X || y != _resizeStartWindow.Y)
            AppWindow.Move(new PointInt32(x, y));
        e.Handled = true;
    }

    private void RootGridPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        EndResize(e);
    }

    private void RootGridPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        EndResize(e);
    }

    private bool TryBeginResize(PointerRoutedEventArgs e)
    {
        if (_locked || _isDragging || _isResizing)
            return false;

        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsLeftButtonPressed
            || !GetCursorPos(out var cursor))
        {
            return false;
        }

        var edges = GetResizeEdges(point.Position);
        if (edges == ResizeEdges.None)
            return false;

        _isResizing = true;
        _resizeEdges = edges;
        _resizeStartCursor = new PointInt32(cursor.X, cursor.Y);
        _resizeStartWindow = AppWindow.Position;
        _resizeStartSize = AppWindow.Size;
        RootGrid.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void EndResize(PointerRoutedEventArgs e)
    {
        if (!_isResizing)
            return;

        _isResizing = false;
        _resizeEdges = ResizeEdges.None;
        RootGrid.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private ResizeEdges GetResizeEdges(Point point)
    {
        var width = Math.Max(1, RootGrid.ActualWidth);
        var height = Math.Max(1, RootGrid.ActualHeight);
        var edges = ResizeEdges.None;
        if (point.X <= ResizeGripSize)
            edges |= ResizeEdges.Left;
        else if (point.X >= width - ResizeGripSize)
            edges |= ResizeEdges.Right;
        if (point.Y <= ResizeGripSize)
            edges |= ResizeEdges.Top;
        else if (point.Y >= height - ResizeGripSize)
            edges |= ResizeEdges.Bottom;
        return edges;
    }

    private void DragRegionPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TryBeginResize(e))
            return;

        if (_locked || sender is not UIElement region)
            return;

        var point = e.GetCurrentPoint(region);
        if (!point.Properties.IsLeftButtonPressed || !GetCursorPos(out var cursor))
            return;

        _isDragging = true;
        _dragOwner = region;
        _dragStartCursor = new PointInt32(cursor.X, cursor.Y);
        _dragStartWindow = AppWindow.Position;
        region.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DragRegionPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        if (!_isDragging || !GetCursorPos(out var cursor))
            return;

        AppWindow.Move(new PointInt32(
            _dragStartWindow.X + cursor.X - _dragStartCursor.X,
            _dragStartWindow.Y + cursor.Y - _dragStartCursor.Y));
        e.Handled = true;
    }

    private void DragRegionPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        EndDrag(e);
    }

    private void DragRegionPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _ = sender;
        EndDrag(e);
    }

    private void EndDrag(PointerRoutedEventArgs? e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        if (_dragOwner is { } owner && e is { } args)
            owner.ReleasePointerCapture(args.Pointer);
        _dragOwner = null;
        if (e is { })
            e.Handled = true;
    }

    private RectInt32 GetScreenBounds(FrameworkElement element)
    {
        return GetScreenBounds(
            element,
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private RectInt32 GetScreenBounds(FrameworkElement element, Rect localBounds)
    {
        var point = element.TransformToVisual(RootGrid).TransformPoint(
            new Point(localBounds.X, localBounds.Y));
        var scale = Math.Max(0.01, RootGrid.XamlRoot?.RasterizationScale ?? 1);
        return new RectInt32(
            AppWindow.Position.X + (int)Math.Round(point.X * scale),
            AppWindow.Position.Y + (int)Math.Round(point.Y * scale),
            Math.Max(1, (int)Math.Round(localBounds.Width * scale)),
            Math.Max(1, (int)Math.Round(localBounds.Height * scale)));
    }

    private async void RefreshButtonClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (RefreshRequested is { } callback)
            await callback();
    }

    private async void StopButtonClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (StopRequested is { } callback)
            await callback();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootGrid.ActualThemeChanged -= RootGridActualThemeChanged;
        LookupRequested = null;
        ThreadSelected = null;
        RefreshRequested = null;
        StopRequested = null;
        ToolbarActionRequested = null;
    }

    private void ApplyBorderlessChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsBorder | WsDlgFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
        style &= ~WsThickFrame;
        style |= WsPopup | WsClipChildren | WsClipSiblings;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle &= ~(WsExAppWindow | WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge);
        exStyle |= WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

        var ncPolicy = DwmNcRenderingPolicyDisabled;
        DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref ncPolicy, sizeof(int));
        var cornerPreference = DwmWindowCornerPreferenceDoNotRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        var borderColor = DwmColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

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
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmNcRenderingPolicyDisabled = 1;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const double ResizeGripSize = 8;
    private const int DefaultHeight = 320;
    private const int MinimumDefaultWidth = 960;
    private const int MaximumDefaultWidth = 2304;
    private const int DefaultTopOffset = 80;
    private const double DefaultWidthRatio = 0.60;
    private const int MinimumWidth = 240;
    private const int MinimumHeight = 80;

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, unchecked((int)value.ToInt64())));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed class GalGameThreadPreviewComparer :
        System.Collections.Generic.IEqualityComparer<GalGameThreadPreview>
    {
        public static GalGameThreadPreviewComparer Instance { get; } = new();

        public bool Equals(GalGameThreadPreview? x, GalGameThreadPreview? y) =>
            x?.ThreadId == y?.ThreadId
            && x?.Sequence == y?.Sequence
            && x?.LineCount == y?.LineCount
            && string.Equals(x?.Text, y?.Text, StringComparison.Ordinal);

        public int GetHashCode(GalGameThreadPreview obj) =>
            HashCode.Combine(obj.ThreadId, obj.Sequence, obj.LineCount, obj.Text);
    }
}
