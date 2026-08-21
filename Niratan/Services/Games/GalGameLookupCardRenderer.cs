using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Niratan.Models.Dictionary;
using Niratan.Models.Games;
using Niratan.Services.Dictionary;
using Niratan.Views.Dictionary;
using Serilog;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Niratan.Services.Games;

/// <summary>
/// Renders the normal dictionary popup in a hidden, borderless WebView2 host
/// and turns that surface into the BGRA frame consumed by the game hook.
/// This deliberately reuses the normal popup DOM, audio controls and Anki
/// bridge instead of introducing a second dictionary implementation.
/// </summary>
public sealed class GalGameLookupCardRenderer : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalLookupPopupWindow? _window;
    private double _displayScale = 1;
    private GalGameLookupCardFrame? _lastFrame;
    private bool _disposed;

    public async Task<GalGameLookupCardFrame?> RenderAsync(
        DictionaryPopupRequest request,
        ulong hitSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunOnUiThreadAsync(() => RenderCoreAsync(
                request,
                hitSequence,
                cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GalLookup] offscreen dictionary card render failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GalGameLookupCardFrame?> InjectAndCaptureAsync(
        GalGameLookupInput input,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastFrame is null)
                return null;

            return await RunOnUiThreadAsync(async () =>
            {
                if (_window is null || _lastFrame is null)
                    return null;

                var scale = Math.Max(0.01, _displayScale);
                await _window.InjectRootLookupInputAsync(
                    input.Kind,
                    input.X / scale,
                    input.Y / scale,
                    input.Wheel,
                    input.Keys,
                    cancellationToken);
                // SendMouseInput/CSS dispatch only queues the browser event;
                // wait for a paint before taking the next frame.
                await Task.Delay(TimeSpan.FromMilliseconds(24), cancellationToken);
                var pixels = await CaptureBgraAsync(_window, cancellationToken);
                if (pixels is null)
                    return null;

                var updated = _lastFrame with
                {
                    Bgra = pixels.Value.Pixels,
                    Width = pixels.Value.Width,
                    Height = pixels.Value.Height,
                    Pitch = checked(pixels.Value.Width * 4),
                };
                _lastFrame = updated;
                return updated;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[GalLookup] offscreen dictionary card input failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GalGameLookupCardFrame?> RenderCoreAsync(
        DictionaryPopupRequest request,
        ulong hitSequence,
        CancellationToken cancellationToken)
    {
        var mainWindow = App.MainWindow
            ?? throw new InvalidOperationException("Main window is not ready for galgame lookup rendering.");
        var displayArea = DisplayArea.GetFromWindowId(
            mainWindow.AppWindow.Id,
            DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        _displayScale = Math.Max(
            0.01,
            mainWindow.Content?.XamlRoot?.RasterizationScale ?? 1);

        _window ??= new GlobalLookupPopupWindow();
        var stagingRect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(
            workArea,
            new SizeInt32(Math.Max(1, workArea.Width), Math.Max(1, workArea.Height)));
        _window.ActivateForStaging(stagingRect);

        var committed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DictionaryPopupContentCommittedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (string.Equals(args.TraceId, request.TraceId, StringComparison.Ordinal))
                committed.TrySetResult(args.TraceId);
        };
        _window.PopupContentCommitted += handler;
        try
        {
            await _window.PrewarmAsync(request.Theme, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var anchor = new RectInt32(
                workArea.X + workArea.Width / 2,
                workArea.Y + workArea.Height / 2,
                1,
                1);
            await _window.ShowRequestAsync(
                request,
                anchor,
                workArea,
                _displayScale,
                cancellationToken);
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            // The commit event is raised by the WebView bridge before the
            // compositor has necessarily laid out the final popup.  A short
            // async yield keeps this path non-blocking while making the first
            // captured frame deterministic.
            await Task.Delay(TimeSpan.FromMilliseconds(32), cancellationToken);
            var bounds = _window.GetRootPopupBounds();
            var requestedSize = new SizeInt32(
                Math.Max(1, (int)Math.Ceiling((bounds?.Width ?? 640) * _displayScale)),
                Math.Max(1, (int)Math.Ceiling((bounds?.Height ?? 240) * _displayScale)));
            _window.MoveRootPopupToOriginAndResize(requestedSize, _displayScale);
            await Task.Delay(TimeSpan.FromMilliseconds(32), cancellationToken);

            var pixels = await CaptureBgraAsync(_window, cancellationToken);
            if (pixels is null || pixels.Value.Width <= 0 || pixels.Value.Height <= 0)
                return null;

            _lastFrame = new GalGameLookupCardFrame
            {
                HitSequence = hitSequence,
                FrameSequence = 0,
                Bgra = pixels.Value.Pixels,
                Width = pixels.Value.Width,
                Height = pixels.Value.Height,
                Pitch = checked(pixels.Value.Width * 4),
            };
            return _lastFrame;
        }
        finally
        {
            _window.PopupContentCommitted -= handler;
        }
    }

    private static async Task<CapturedPixels?> CaptureBgraAsync(
        GlobalLookupPopupWindow window,
        CancellationToken cancellationToken)
    {
        var png = await window.CaptureRootPreviewPngAsync(cancellationToken);
        if (png is null || png.Length == 0)
            return null;

        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        return new CapturedPixels(
            checked((int)decoder.PixelWidth),
            checked((int)decoder.PixelHeight),
            pixels.DetachPixelData());
    }

    private static async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue
            ?? throw new InvalidOperationException("Main window dispatcher is not ready.");
        if (dispatcher.HasThreadAccess)
            return await action();

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            throw new InvalidOperationException("Unable to schedule galgame lookup rendering.");
        }
        return await completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lastFrame = null;
        if (_window is not null)
        {
            try { _window.Close(); }
            catch (Exception ex) { Log.Debug(ex, "[GalLookup] offscreen window close failed"); }
            _window = null;
        }
        _gate.Dispose();
    }

    private readonly record struct CapturedPixels(int Width, int Height, byte[] Pixels);
}
