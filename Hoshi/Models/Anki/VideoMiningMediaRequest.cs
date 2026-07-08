using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Models.Anki;

public sealed record VideoMiningMediaRequest(
    bool CaptureScreenshot,
    bool CaptureAudioClip,
    string? DirectMediaDirectory);

public sealed record VideoMiningMediaResult(
    string? ScreenshotPath = null,
    string? AudioClipPath = null,
    string? ScreenshotTag = null,
    string? AudioClipTag = null,
    string? AudioClipErrorMessage = null);

public delegate Task<VideoMiningMediaResult> VideoMiningMediaProvider(
    VideoMiningMediaRequest request,
    CancellationToken ct);
