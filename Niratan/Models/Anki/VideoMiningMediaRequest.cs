using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Models.Anki;

public sealed record VideoMiningMediaRequest(
    bool CaptureScreenshot,
    bool CaptureAudioClip,
    string? DirectMediaDirectory,
    TimeSpan? CueStart = null,
    TimeSpan? CueEnd = null);

public sealed record VideoMiningMediaResult(
    string? ScreenshotPath = null,
    string? AudioClipPath = null,
    string? ScreenshotTag = null,
    string? AudioClipTag = null,
    string? AudioClipErrorMessage = null,
    string? ScreenshotErrorMessage = null);

public delegate Task<VideoMiningMediaResult> VideoMiningMediaProvider(
    VideoMiningMediaRequest request,
    CancellationToken ct);
