using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Video;

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
}
