using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Video;

internal sealed class MpvPlaybackEngine : IVideoPlaybackEngine
{
    private readonly object _syncRoot = new();
    private IntPtr _handle;
    private bool _disposed;

    public Task InitializeAsync(IntPtr hostHwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hostHwnd == IntPtr.Zero)
            throw new ArgumentException("Video host window is not ready.", nameof(hostHwnd));

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            DestroyHandle();

            try
            {
                _handle = MpvNative.Create();
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(MpvNative.GetLoadDiagnostic(), ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException("The bundled libmpv architecture does not match the running Hoshi process.", ex);
            }

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create the libmpv playback engine.");

            MpvNative.SetOptionStringChecked(_handle, "config", "no");
            MpvNative.SetOptionStringChecked(_handle, "osc", "no");
            MpvNative.SetOptionStringChecked(_handle, "input-default-bindings", "no");
            MpvNative.SetOptionStringChecked(_handle, "input-cursor", "no");
            MpvNative.SetOptionStringChecked(_handle, "sid", "no");
            MpvNative.SetOptionStringChecked(_handle, "keep-open", "yes");
            MpvNative.SetOptionStringChecked(_handle, "force-window", "yes");
            MpvNative.SetOptionStringChecked(_handle, "hwdec", "auto-safe");
            MpvNative.SetOptionStringChecked(_handle, "wid", hostHwnd.ToInt64().ToString());

            var status = MpvNative.Initialize(_handle);
            if (status < 0)
            {
                var message = MpvNative.ErrorString(status);
                DestroyHandle();
                throw new InvalidOperationException($"Unable to initialize libmpv: {message}");
            }
        }

        return Task.CompletedTask;
    }

    public Task OpenAsync(string filePath, string? subtitlePath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Video file path is required.", nameof(filePath));

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            var status = MpvNative.Command(_handle, "loadfile", filePath, "replace");
            if (status < 0)
                throw new InvalidOperationException($"Unable to load video: {MpvNative.ErrorString(status)}");

            if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
            {
                status = MpvNative.Command(_handle, "sub-add", subtitlePath, "select");
                if (status >= 0)
                {
                    var hidden = 0;
                    _ = MpvNative.SetPropertyFlag(_handle, "sub-visibility", MpvNative.MpvFormatFlag, ref hidden);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = paused ? 1 : 0;
            var status = MpvNative.SetPropertyFlag(_handle, "pause", MpvNative.MpvFormatFlag, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update playback state: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.Command(
                _handle,
                "seek",
                MpvNative.FormatSeconds(position),
                "absolute+exact");
            if (status < 0)
                throw new InvalidOperationException($"Unable to seek video: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Clamp(volume, 0, 130);
            var status = MpvNative.SetPropertyDouble(_handle, "volume", MpvNative.MpvFormatDouble, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update volume: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetHardwareDecodingAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.SetPropertyString(_handle, "hwdec", enabled ? "auto-safe" : "no");
            if (status < 0)
                throw new InvalidOperationException($"Unable to update hardware decoding: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task<TimeSpan> GetPositionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetDoubleTimeProperty("time-pos"));
    }

    public Task<TimeSpan> GetDurationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetDoubleTimeProperty("duration"));
    }

    public async Task<string?> CaptureScreenshotAsync(string outputPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return null;

            var status = MpvNative.Command(_handle, "screenshot-to-file", outputPath, "video");
            if (status < 0)
                return null;
        }

        for (var i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(outputPath))
                return outputPath;

            await Task.Delay(50, ct);
        }

        return File.Exists(outputPath) ? outputPath : null;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            DestroyHandle();
            _disposed = true;
        }
    }

    private TimeSpan GetDoubleTimeProperty(string name)
    {
        lock (_syncRoot)
        {
            if (_disposed || _handle == IntPtr.Zero)
                return TimeSpan.Zero;

            var status = MpvNative.GetPropertyDouble(_handle, name, MpvNative.MpvFormatDouble, out var seconds);
            if (status < 0 || !double.IsFinite(seconds) || seconds < 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(seconds);
        }
    }

    private void EnsureInitialized()
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Video playback is not initialized.");
    }

    private void DestroyHandle()
    {
        if (_handle == IntPtr.Zero)
            return;

        MpvNative.TerminateDestroy(_handle);
        _handle = IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
