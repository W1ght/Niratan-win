using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Niratan.Enums;
using Niratan.Models.Dictionary;
using Niratan.Views.Dictionary;
using Serilog;
using Windows.Graphics;

namespace Niratan.Services.Dictionary;

internal sealed class GlobalLookupPopupService : IGlobalLookupPopupService, IDisposable
{
    private readonly IDictionaryPopupRequestService _requestService;
    private readonly SemaphoreSlim _showSemaphore = new(1, 1);
    private readonly List<PopupEntry> _popupStack = [];
    private readonly Queue<GlobalLookupPopupWindow> _standbyWindows = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly LowLevelMouseProc _mouseHookProc;
    private CancellationTokenSource? _statusDismissCts;
    private Task? _standbyRefillTask;
    private IntPtr _mouseHook;
    private int _mouseDispatchQueued;
    private POINT _pendingMousePoint;
    private bool _disposed;

    public GlobalLookupPopupService(IDictionaryPopupRequestService requestService)
    {
        _requestService = requestService;
        _mouseHookProc = OnLowLevelMouse;
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _disposeCts.Token);
        await RunOnUiThreadAsync(() => EnsureStandbyPoolCoreAsync(linkedCts.Token));
    }

    public async Task ShowAsync(SelectedTextSnapshot selection, CancellationToken ct = default)
    {
        var query = selection.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        Log.Information("[GlobalLookup] Global lookup popup requested for '{Query}'", query);
        await _showSemaphore.WaitAsync(ct);
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                CloseAllCore();
                return Task.CompletedTask;
            });

            var request = await Task.Run(
                () => _requestService.CreateAsync(
                    query,
                    traceId: $"global-popup-{Guid.NewGuid():N}",
                    ct: ct),
                ct);
            var cursorPoint = GetCursorPoint();
            if (request is null)
            {
                await RunOnUiThreadAsync(() => ShowStatusCoreAsync(
                    new SelectedTextSnapshot(query, selection.ScreenBounds),
                    cursorPoint,
                    ct));
                return;
            }

            await RunOnUiThreadAsync(() => PresentPopupCoreAsync(
                request,
                NormalizeAnchorRect(selection.ScreenBounds, cursorPoint),
                ct));
        }
        finally
        {
            _showSemaphore.Release();
        }
    }

    private static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue
            ?? throw new InvalidOperationException("Main window is not ready for global lookup popup presentation.");
        if (dispatcher.HasThreadAccess)
        {
            await action();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        if (!queued)
            throw new InvalidOperationException("Unable to schedule global lookup popup presentation.");

        await completion.Task;
    }

    private async Task PresentPopupCoreAsync(
        DictionaryPopupRequest request,
        RectInt32 anchorRect,
        CancellationToken ct)
    {
        CancelStatusDismiss();
        var displayPoint = new PointInt32(
            anchorRect.X + Math.Max(1, anchorRect.Width) / 2,
            anchorRect.Y + Math.Max(1, anchorRect.Height) / 2);
        var displayArea = DisplayArea.GetFromPoint(displayPoint, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var displayScale = GetDisplayScale(displayPoint);
        var stagingRect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(
            workArea,
            new SizeInt32(
                Math.Max(1, workArea.Width),
                Math.Max(1, workArea.Height)));

        var window = AcquirePopupWindowCore();
        var entry = new PopupEntry(window)
        {
            DisplayScale = displayScale,
            Pending = new PendingPlacement(
                request.TraceId,
                anchorRect,
                workArea,
                displayScale),
        };
        SubscribeWindow(window);
        _popupStack.Add(entry);

        window.ActivateForStaging(stagingRect);

        try
        {
            await window.ShowRequestAsync(
                request,
                anchorRect,
                workArea,
                displayScale,
                ct);
        }
        catch
        {
            var index = _popupStack.IndexOf(entry);
            if (index >= 0)
                CloseFromIndexCore(index);
            throw;
        }

        Log.Information(
            "[GlobalLookup] Popup panel created depth={Depth} trace={TraceId}",
            _popupStack.Count,
            request.TraceId ?? "-");
    }

    private async Task ShowStatusCoreAsync(
        SelectedTextSnapshot selection,
        PointInt32 cursorPoint,
        CancellationToken ct)
    {
        var anchorRect = NormalizeAnchorRect(selection.ScreenBounds, cursorPoint);
        var displayPoint = new PointInt32(
            anchorRect.X + Math.Max(1, anchorRect.Width) / 2,
            anchorRect.Y + Math.Max(1, anchorRect.Height) / 2);
        var displayArea = DisplayArea.GetFromPoint(displayPoint, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var scale = GetDisplayScale(displayPoint);
        var stagingRect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(
            workArea,
            new SizeInt32(workArea.Width, workArea.Height));

        var window = AcquirePopupWindowCore();
        var entry = new PopupEntry(window)
        {
            DisplayScale = scale,
        };
        SubscribeWindow(window);
        _popupStack.Add(entry);
        window.ActivateForStaging(stagingRect);
        await window.ShowStatusAsync(
            "No dictionary result found.",
            ThemeMode.System,
            ct);

        var statusSize = new SizeInt32(
            Math.Max(1, (int)Math.Ceiling(360 * scale)),
            Math.Max(1, (int)Math.Ceiling(92 * scale)));
        var gap = Math.Max(
            1,
            (int)Math.Ceiling(GlobalLookupPopupWindowPlacement.PopupGap * scale));
        var finalRect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            anchorRect,
            workArea,
            statusSize,
            gap);
        await window.RevealFinalSurfaceAsync(finalRect, scale, entry.LifetimeToken);
        EnsureDismissMonitor();
        Log.Information(
            "[GlobalLookup] No-result status revealed at ({X},{Y}) size={Width}x{Height}",
            finalRect.X,
            finalRect.Y,
            finalRect.Width,
            finalRect.Height);

        _statusDismissCts = new CancellationTokenSource();
        _ = DismissStatusAfterDelayAsync(window, _statusDismissCts.Token);
    }

    private async Task DismissStatusAfterDelayAsync(
        GlobalLookupPopupWindow window,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await RunOnUiThreadAsync(() =>
            {
                var index = FindWindowIndex(window);
                if (index >= 0)
                    CloseFromIndexCore(index);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async void OnPopupContentCommitted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        if (sender is not GlobalLookupPopupWindow window)
            return;

        var index = FindWindowIndex(window);
        if (index < 0)
            return;

        var entry = _popupStack[index];
        var pending = entry.Pending;
        if (pending is null
            || !string.Equals(e.TraceId, pending.TraceId, StringComparison.Ordinal))
        {
            return;
        }

        var bounds = window.GetRootPopupBounds();
        if (bounds is null)
            return;

        var scale = pending.DisplayScale;
        var popupSize = new SizeInt32(
            Math.Max(1, (int)Math.Ceiling(bounds.Value.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(bounds.Value.Height * scale)));
        var gap = Math.Max(
            1,
            (int)Math.Ceiling(GlobalLookupPopupWindowPlacement.PopupGap * scale));
        var finalRect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            pending.AnchorRect,
            pending.WorkArea,
            popupSize,
            gap);

        window.MoveRootPopupToOriginAndResize(popupSize, scale);
        try
        {
            await window.RevealFinalSurfaceAsync(
                finalRect,
                scale,
                entry.LifetimeToken);
        }
        catch (OperationCanceledException) when (entry.LifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GlobalLookup] Failed to compose final popup surface");
            var failedIndex = FindWindowIndex(window);
            if (failedIndex >= 0)
                CloseEntryCore(failedIndex);
            return;
        }

        var committedIndex = FindWindowIndex(window);
        if (committedIndex < 0
            || !ReferenceEquals(_popupStack[committedIndex], entry)
            || !ReferenceEquals(entry.Pending, pending))
        {
            return;
        }

        entry.Pending = null;
        entry.DisplayScale = scale;
        EnsureDismissMonitor();
        Log.Information(
            "[GlobalLookup] Popup panel revealed depth={Depth} anchor=({AnchorX},{AnchorY},{AnchorWidth},{AnchorHeight}) placement={Placement} at ({X},{Y}) size={Width}x{Height} scale={Scale:F2}",
            committedIndex + 1,
            pending.AnchorRect.X,
            pending.AnchorRect.Y,
            pending.AnchorRect.Width,
            pending.AnchorRect.Height,
            finalRect.Y >= pending.AnchorRect.Y + pending.AnchorRect.Height
                ? "below"
                : "above",
            finalRect.X,
            finalRect.Y,
            finalRect.Width,
            finalRect.Height,
            scale);
    }

    private async void OnChildPopupRequested(
        object? sender,
        DictionaryPopupExternalChildRequest request)
    {
        if (sender is not GlobalLookupPopupWindow parentWindow)
            return;

        try
        {
            var parentIndex = FindWindowIndex(parentWindow);
            if (parentIndex < 0)
                return;

            CloseAfterIndexCore(parentIndex);
            var parentEntry = _popupStack[parentIndex];
            var parentSurfaceOrigin = parentWindow.GetPopupSurfaceScreenOrigin();
            var scale = Math.Max(0.01, parentEntry.DisplayScale);
            var anchorRect = new RectInt32(
                parentSurfaceOrigin.X + (int)Math.Floor(request.AnchorX * scale),
                parentSurfaceOrigin.Y + (int)Math.Floor(request.AnchorY * scale),
                Math.Max(1, (int)Math.Ceiling(request.AnchorWidth * scale)),
                Math.Max(1, (int)Math.Ceiling(request.AnchorHeight * scale)));

            await PresentPopupCoreAsync(
                request.PopupRequest,
                anchorRect,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GlobalLookup] Failed to present child popup panel");
        }
    }

    private void OnPopupSurfaceClicked(object? sender, EventArgs e)
    {
        if (sender is not GlobalLookupPopupWindow window)
            return;

        var index = FindWindowIndex(window);
        if (index >= 0)
            CloseAfterIndexCore(index);
    }

    private void SubscribeWindow(GlobalLookupPopupWindow window)
    {
        window.Closed += OnWindowClosed;
        window.PopupContentCommitted += OnPopupContentCommitted;
        window.ChildPopupRequested += OnChildPopupRequested;
        window.PopupSurfaceClicked += OnPopupSurfaceClicked;
    }

    private void UnsubscribeWindow(GlobalLookupPopupWindow window)
    {
        window.Closed -= OnWindowClosed;
        window.PopupContentCommitted -= OnPopupContentCommitted;
        window.ChildPopupRequested -= OnChildPopupRequested;
        window.PopupSurfaceClicked -= OnPopupSurfaceClicked;
    }

    private GlobalLookupPopupWindow AcquirePopupWindowCore()
    {
        var window = _standbyWindows.Count > 0
            ? _standbyWindows.Dequeue()
            : new GlobalLookupPopupWindow();

        // Active windows are intentionally never returned to the pool because
        // they can still own WebView2 render callbacks. Replenish with a fresh
        // prewarmed window as soon as one is checked out so every stack depth
        // normally has a warm renderer available.
        ScheduleStandbyRefill();
        return window;
    }

    private void ScheduleStandbyRefill()
    {
        if (_disposed || _standbyRefillTask is { IsCompleted: false })
            return;

        _standbyRefillTask = RefillStandbyPoolAsync();
    }

    private async Task RefillStandbyPoolAsync()
    {
        try
        {
            await EnsureStandbyPoolCoreAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GlobalLookup] Standby popup refill failed");
        }
        finally
        {
            _standbyRefillTask = null;
        }
    }

    private async Task EnsureStandbyPoolCoreAsync(CancellationToken ct)
    {
        while (!_disposed && _standbyWindows.Count < StandbyWindowCount)
        {
            ct.ThrowIfCancellationRequested();
            var cursorPoint = GetCursorPoint();
            var workArea = DisplayArea
                .GetFromPoint(cursorPoint, DisplayAreaFallback.Nearest)
                .WorkArea;
            var stagingRect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(
                workArea,
                new SizeInt32(
                    Math.Max(1, workArea.Width),
                    Math.Max(1, workArea.Height)));
            var window = new GlobalLookupPopupWindow();
            try
            {
                window.ActivateForStaging(stagingRect);
                await window.PrewarmAsync(ThemeMode.System, ct);
                window.PrepareForStaging();
                ct.ThrowIfCancellationRequested();
                if (_standbyWindows.Count >= StandbyWindowCount)
                {
                    window.Close();
                    break;
                }

                _standbyWindows.Enqueue(window);
                Log.Information(
                    "[GlobalLookup] Standby popup prewarmed pool={PoolCount}/{TargetCount}",
                    _standbyWindows.Count,
                    StandbyWindowCount);
            }
            catch
            {
                window.Close();
                throw;
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not GlobalLookupPopupWindow window)
            return;

        var index = FindWindowIndex(window);
        if (index < 0)
            return;

        var entry = _popupStack[index];
        entry.Cancel();
        CloseAfterIndexCore(index);
        UnsubscribeWindow(window);
        _popupStack.RemoveAt(index);
        if (_popupStack.Count == 0)
        {
            CancelStatusDismiss();
            RemoveDismissMonitor();
            ScheduleStandbyRefill();
        }
    }

    private int FindWindowIndex(GlobalLookupPopupWindow window) =>
        _popupStack.FindIndex(entry => ReferenceEquals(entry.Window, window));

    private void CloseAfterIndexCore(int parentIndex)
    {
        for (var i = _popupStack.Count - 1; i > parentIndex; i--)
            CloseEntryCore(i);
    }

    private void CloseFromIndexCore(int index)
    {
        for (var i = _popupStack.Count - 1; i >= index; i--)
            CloseEntryCore(i);
    }

    private void CloseAllCore()
    {
        CancelStatusDismiss();
        CloseFromIndexCore(0);
        RemoveDismissMonitor();
    }

    private void CloseEntryCore(int index)
    {
        var entry = _popupStack[index];
        _popupStack.RemoveAt(index);
        entry.Cancel();
        UnsubscribeWindow(entry.Window);
        entry.Window.Close();
        if (_popupStack.Count == 0)
            ScheduleStandbyRefill();
    }

    private void EnsureDismissMonitor()
    {
        if (_mouseHook != IntPtr.Zero)
            return;

        _mouseHook = SetWindowsHookEx(
            WhMouseLl,
            _mouseHookProc,
            GetModuleHandle(null),
            0);
    }

    private void RemoveDismissMonitor()
    {
        if (_mouseHook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr OnLowLevelMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0
            && IsMouseButtonDown(unchecked((uint)wParam.ToInt64()))
            && Interlocked.Exchange(ref _mouseDispatchQueued, 1) == 0)
        {
            _pendingMousePoint = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Point;
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher is null || !dispatcher.TryEnqueue(HandleGlobalMouseDown))
                Interlocked.Exchange(ref _mouseDispatchQueued, 0);
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void HandleGlobalMouseDown()
    {
        Interlocked.Exchange(ref _mouseDispatchQueued, 0);
        var point = _pendingMousePoint;

        for (var i = _popupStack.Count - 1; i >= 0; i--)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_popupStack[i].Window);
            if (hwnd == IntPtr.Zero || !ContainsVisibleSurface(hwnd, point))
            {
                continue;
            }

            CloseAfterIndexCore(i);
            return;
        }

        CloseAllCore();
    }

    private static bool IsMouseButtonDown(uint message) =>
        message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown;

    private static bool ContainsVisibleSurface(IntPtr hwnd, POINT point)
    {
        if (!GetWindowRect(hwnd, out var rect)
            || point.X < rect.Left
            || point.X >= rect.Right
            || point.Y < rect.Top
            || point.Y >= rect.Bottom)
        {
            return false;
        }

        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero)
            return true;

        try
        {
            return GetWindowRgn(hwnd, region) == RegionError
                || PtInRegion(region, point.X - rect.Left, point.Y - rect.Top);
        }
        finally
        {
            DeleteObject(region);
        }
    }

    private void CancelStatusDismiss()
    {
        _statusDismissCts?.Cancel();
        _statusDismissCts?.Dispose();
        _statusDismissCts = null;
    }

    private static RectInt32 NormalizeAnchorRect(
        RectInt32? screenBounds,
        PointInt32 fallbackPoint)
    {
        if (screenBounds is { Width: > 0, Height: > 0 } bounds)
            return bounds;

        return new RectInt32(fallbackPoint.X, fallbackPoint.Y, 1, 1);
    }

    private static PointInt32 GetCursorPoint() =>
        GetCursorPos(out var point)
            ? new PointInt32(point.X, point.Y)
            : new PointInt32(0, 0);

    private static double GetDisplayScale(PointInt32 point)
    {
        var monitor = MonitorFromPoint(
            new POINT(point.X, point.Y),
            MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(
                monitor,
                MonitorDpiTypeEffective,
                out var dpiX,
                out _) == 0
            && dpiX > 0)
        {
            return dpiX / 96d;
        }

        var systemDpi = GetDpiForSystem();
        return systemDpi > 0 ? systemDpi / 96d : 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeCts.Cancel();
        CloseAllCore();
        while (_standbyWindows.TryDequeue(out var standbyWindow))
            standbyWindow.Close();
        _disposeCts.Dispose();
        _showSemaphore.Dispose();
    }

    private sealed class PopupEntry(GlobalLookupPopupWindow window)
    {
        private readonly CancellationTokenSource _lifetimeCts = new();

        public GlobalLookupPopupWindow Window { get; } = window;
        public PendingPlacement? Pending { get; set; }
        public double DisplayScale { get; set; } = 1;
        public CancellationToken LifetimeToken => _lifetimeCts.Token;

        public void Cancel()
        {
            if (!_lifetimeCts.IsCancellationRequested)
                _lifetimeCts.Cancel();
        }
    }

    private sealed record PendingPlacement(
        string? TraceId,
        RectInt32 AnchorRect,
        RectInt32 WorkArea,
        double DisplayScale);

    private const uint MonitorDefaultToNearest = 2;
    private const int StandbyWindowCount = 2;
    private const int MonitorDpiTypeEffective = 0;
    private const int RegionError = 0;
    private const int WhMouseLl = 14;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmXButtonDown = 0x020B;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

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
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelMouseProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
