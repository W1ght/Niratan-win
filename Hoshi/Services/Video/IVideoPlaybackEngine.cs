using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Video;

public interface IVideoPlaybackEngine : IDisposable
{
    Task InitializeAsync(IntPtr hostHwnd, CancellationToken ct = default);

    Task OpenAsync(string filePath, string? subtitlePath = null, CancellationToken ct = default);

    Task SetPausedAsync(bool paused, CancellationToken ct = default);

    Task SeekAsync(TimeSpan position, CancellationToken ct = default);

    Task SetVolumeAsync(double volume, CancellationToken ct = default);

    Task SetHardwareDecodingAsync(bool enabled, CancellationToken ct = default);

    Task<TimeSpan> GetPositionAsync(CancellationToken ct = default);

    Task<TimeSpan> GetDurationAsync(CancellationToken ct = default);

    Task<string?> CaptureScreenshotAsync(string outputPath, CancellationToken ct = default);
}
