using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoPlaybackEngine : IDisposable
{
    Task InitializeAsync(IntPtr hostHwnd, CancellationToken ct = default);

    Task OpenAsync(
        string filePath,
        string? subtitlePath = null,
        TimeSpan? startPosition = null,
        CancellationToken ct = default);

    Task SetPausedAsync(bool paused, CancellationToken ct = default);

    Task SeekAsync(TimeSpan position, CancellationToken ct = default);

    Task SetVolumeAsync(double volume, CancellationToken ct = default);

    Task SetPlaybackSpeedAsync(double speed, CancellationToken ct = default);

    Task SetAudioDelayAsync(TimeSpan delay, CancellationToken ct = default);

    Task SetSubtitleDelayAsync(TimeSpan delay, CancellationToken ct = default);

    Task SetFileLoopEnabledAsync(bool enabled, CancellationToken ct = default);

    Task SetABLoopAsync(VideoABLoop? loop, CancellationToken ct = default);

    Task SetAspectRatioAsync(string value, CancellationToken ct = default);

    Task SetVideoRotationAsync(int degrees, CancellationToken ct = default);

    Task SetHardwareDecodingAsync(bool enabled, CancellationToken ct = default);

    Task SetDeinterlaceAsync(bool enabled, CancellationToken ct = default);

    Task SetHDREnhancementAsync(bool enabled, CancellationToken ct = default);

    Task SetVideoEqualizerAsync(string adjustment, double value, CancellationToken ct = default);

    Task<IReadOnlyList<VideoTrackInfo>> GetTracksAsync(CancellationToken ct = default);

    Task SelectTrackAsync(VideoTrackType type, int? trackId, CancellationToken ct = default);

    Task<IReadOnlyList<VideoChapter>> GetChaptersAsync(CancellationToken ct = default);

    Task SeekChapterAsync(int chapterId, CancellationToken ct = default);

    Task<VideoSubtitleCue?> GetCurrentSubtitleCueAsync(CancellationToken ct = default);

    Task<TimeSpan> GetPositionAsync(CancellationToken ct = default);

    Task<TimeSpan> GetDurationAsync(CancellationToken ct = default);

    Task<string?> CaptureScreenshotAsync(string outputPath, CancellationToken ct = default);
}
