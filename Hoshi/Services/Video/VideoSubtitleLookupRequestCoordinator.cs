using System;
using System.Threading;

namespace Hoshi.Services.Video;

internal readonly record struct VideoSubtitleLookupRequest(
    long Version,
    CancellationToken CancellationToken);

internal readonly record struct VideoSubtitlePopupCommit(
    int SelectionStart,
    string MatchedText);

internal sealed class VideoSubtitleLookupRequestCoordinator : IDisposable
{
    private readonly record struct PendingPopupCommit(
        long RequestVersion,
        string TraceId,
        VideoSubtitlePopupCommit Commit);

    private readonly object _gate = new();
    private long _version;
    private CancellationTokenSource? _currentCts;
    private PendingPopupCommit? _pendingPopupCommit;

    public VideoSubtitleLookupRequest BeginRequest()
    {
        CancellationTokenSource? previous;
        VideoSubtitleLookupRequest request;
        lock (_gate)
        {
            previous = _currentCts;
            _currentCts = new CancellationTokenSource();
            _pendingPopupCommit = null;
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

    public void StagePopupCommit(
        VideoSubtitleLookupRequest request,
        string traceId,
        int selectionStart,
        string matchedText)
    {
        lock (_gate)
        {
            if (request.Version != _version
                || request.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            _pendingPopupCommit = new PendingPopupCommit(
                request.Version,
                traceId,
                new VideoSubtitlePopupCommit(selectionStart, matchedText));
        }
    }

    public bool TryTakePopupCommit(
        string? traceId,
        out VideoSubtitlePopupCommit commit)
    {
        lock (_gate)
        {
            commit = default;
            if (_pendingPopupCommit is not PendingPopupCommit pending
                || pending.RequestVersion != _version
                || !string.Equals(
                    pending.TraceId,
                    traceId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            commit = pending.Commit;
            _pendingPopupCommit = null;
            return true;
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
            _pendingPopupCommit = null;
        }

        current?.Cancel();
        current?.Dispose();
    }

    public void Dispose() => CancelCurrent();
}
