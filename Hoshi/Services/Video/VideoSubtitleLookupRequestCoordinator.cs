using System;
using System.Threading;

namespace Hoshi.Services.Video;

internal readonly record struct VideoSubtitleLookupRequest(
    long Version,
    CancellationToken CancellationToken);

internal sealed class VideoSubtitleLookupRequestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private long _version;
    private CancellationTokenSource? _currentCts;

    public VideoSubtitleLookupRequest BeginRequest()
    {
        CancellationTokenSource? previous;
        VideoSubtitleLookupRequest request;
        lock (_gate)
        {
            previous = _currentCts;
            _currentCts = new CancellationTokenSource();
            request = new VideoSubtitleLookupRequest(++_version, _currentCts.Token);
        }

        previous?.Cancel();
        previous?.Dispose();
        return request;
    }

    public bool IsCurrent(VideoSubtitleLookupRequest request)
    {
        lock (_gate)
        {
            return request.Version == _version
                && !request.CancellationToken.IsCancellationRequested;
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? current;
        lock (_gate)
        {
            _version++;
            current = _currentCts;
            _currentCts = null;
        }

        current?.Cancel();
        current?.Dispose();
    }

    public void Dispose() => CancelCurrent();
}
