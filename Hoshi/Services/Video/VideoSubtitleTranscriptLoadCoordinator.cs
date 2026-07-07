using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Video;

internal sealed record VideoSubtitleTranscriptLoadResult(
    VideoTrackInfo Track,
    IReadOnlyList<VideoSubtitleCue> Cues,
    bool IsCurrent,
    bool WasCancelled);

internal sealed class VideoSubtitleTranscriptLoadCoordinator(
    IVideoSubtitleTranscriptExtractor extractor) : System.IDisposable
{
    private readonly object _syncRoot = new();
    private int _generation;
    private CancellationTokenSource? _loadCts;

    public async Task<VideoSubtitleTranscriptLoadResult> LoadAsync(
        string videoPath,
        VideoTrackInfo track,
        CancellationToken ct = default)
    {
        CancellationTokenSource linkedCts;
        int generation;
        lock (_syncRoot)
        {
            CancelCore();
            generation = ++_generation;
            _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts = _loadCts;
        }

        try
        {
            var cues = await extractor.ExtractAsync(videoPath, track, linkedCts.Token);
            return new VideoSubtitleTranscriptLoadResult(
                track,
                cues,
                IsCurrent(generation, linkedCts),
                WasCancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new VideoSubtitleTranscriptLoadResult(track, [], IsCurrent: false, WasCancelled: true);
        }
    }

    public void Cancel()
    {
        lock (_syncRoot)
        {
            CancelCore();
            _generation++;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            CancelCore();
        }
    }

    private bool IsCurrent(int generation, CancellationTokenSource linkedCts)
    {
        lock (_syncRoot)
        {
            return ReferenceEquals(_loadCts, linkedCts)
                && _generation == generation
                && !linkedCts.IsCancellationRequested;
        }
    }

    private void CancelCore()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }
}
