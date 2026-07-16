using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoMiningMediaExtractor
{
    Task<string?> CaptureScreenshotAsync(
        string videoPath,
        string outputPath,
        TimeSpan timestamp,
        CancellationToken ct = default);

    Task<string?> ExportAudioClipAsync(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct = default);

    Task<string?> ExportAudioClipAsync(
        VideoMiningMediaSource source,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct = default) =>
        ExportAudioClipAsync(source.Source, outputPath, start, end, ct);
}
