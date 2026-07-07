using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Dictionary;
using Hoshi.Views.Dictionary;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Graphics;

namespace Hoshi.Services.Dictionary;

internal sealed class GlobalLookupPopupService : IGlobalLookupPopupService
{
    private static readonly SizeInt32 StagingSize = new(720, 560);

    private readonly IDictionaryPopupRequestService _requestService;
    private readonly SemaphoreSlim _showSemaphore = new(1, 1);
    private GlobalLookupPopupWindow? _window;

    public GlobalLookupPopupService(IDictionaryPopupRequestService requestService)
    {
        _requestService = requestService;
    }

    public async Task ShowAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;

        Log.Information("[GlobalLookup] Global lookup popup requested for '{Query}'", query);
        await _showSemaphore.WaitAsync(ct);
        try
        {
            var request = await Task.Run(
                () => _requestService.CreateAsync(query, traceId: $"global-popup-{Guid.NewGuid():N}", ct: ct),
                ct);
            if (request is null)
            {
                await CloseWindowAsync();
                return;
            }

            await ShowRequestOnUiThreadAsync(request, GetCursorPoint(), ct);
        }
        finally
        {
            _showSemaphore.Release();
        }
    }

    private async Task ShowRequestOnUiThreadAsync(
        DictionaryPopupRequest request,
        PointInt32 cursorPoint,
        CancellationToken ct)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is { HasThreadAccess: false })
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await ShowRequestCoreAsync(request, cursorPoint, ct);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            if (!queued)
                throw new InvalidOperationException("Unable to schedule global lookup popup.");

            await completion.Task;
            return;
        }

        await ShowRequestCoreAsync(request, cursorPoint, ct);
    }

    private async Task CloseWindowAsync()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is { HasThreadAccess: false })
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(() =>
            {
                try
                {
                    CloseWindowCore();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            if (!queued)
                return;

            await completion.Task;
            return;
        }

        CloseWindowCore();
    }

    private async Task ShowRequestCoreAsync(
        DictionaryPopupRequest request,
        PointInt32 cursorPoint,
        CancellationToken ct)
    {
        if (_window is null)
        {
            _window = new GlobalLookupPopupWindow();
            _window.Closed += OnWindowClosed;
        }

        var window = _window;
        var displayArea = DisplayArea.GetFromPoint(cursorPoint, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var stagingRect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(workArea, StagingSize);
        window.ClearHostRegion();
        window.AppWindow.MoveAndResize(stagingRect);
        window.Activate();
        window.ApplyBorderlessHostChrome();

        var anchorPoint = new PointInt32(
            Math.Clamp(cursorPoint.X - workArea.X, 0, Math.Max(1, stagingRect.Width - 1)),
            Math.Clamp(cursorPoint.Y - workArea.Y, 0, Math.Max(1, stagingRect.Height - 1)));
        await window.ShowRequestAsync(request, anchorPoint, ct);
        if (!ReferenceEquals(window, _window))
            return;

        var bounds = window.GetRootPopupBounds();
        if (bounds is null)
            return;

        var scale = window.RasterizationScale;
        var popupSize = new SizeInt32(
            Math.Max(1, (int)Math.Ceiling(bounds.Value.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(bounds.Value.Height * scale)));
        var finalRect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            cursorPoint,
            workArea,
            popupSize);

        window.MoveRootPopupToOrigin();
        window.AppWindow.MoveAndResize(finalRect);
        window.ApplyBorderlessHostChrome();
        window.ApplyRoundedHostRegion();
    }

    private void CloseWindowCore()
    {
        if (_window is null)
            return;

        var window = _window;
        _window = null;
        window.Closed -= OnWindowClosed;
        window.Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(sender, _window))
        {
            _window!.Closed -= OnWindowClosed;
            _window = null;
        }
    }

    private static PointInt32 GetCursorPoint()
    {
        return GetCursorPos(out var point)
            ? new PointInt32(point.X, point.Y)
            : new PointInt32(0, 0);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }
}
