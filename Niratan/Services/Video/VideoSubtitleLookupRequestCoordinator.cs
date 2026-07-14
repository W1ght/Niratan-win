using System;
using System.Collections.Generic;
using System.Threading;

namespace Niratan.Services.Video;

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
        VideoSubtitlePopupCommit Commit,
        bool IsAccepted = false);

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPopupCommit> _popupCommits =
        new(StringComparer.Ordinal);
    private long _version;
    private CancellationTokenSource? _currentCts;

    public bool HasPopupCommitCandidates
    {
        get
        {
            lock (_gate)
                return _popupCommits.Count > 0;
        }
    }

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

    public void StagePopupCommit(
        VideoSubtitleLookupRequest request,
        string commitIdentity,
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

            RemoveCandidatesForRequest(request.Version);

            _popupCommits[commitIdentity] = new PendingPopupCommit(
                request.Version,
                new VideoSubtitlePopupCommit(selectionStart, matchedText));
        }
    }

    public string CreatePopupCommitIdentity(
        VideoSubtitleLookupRequest request,
        string? sourceTraceId) =>
        $"video-{request.Version}:{sourceTraceId ?? "-"}";

    public void MarkPopupCommitAccepted(string commitIdentity)
    {
        lock (_gate)
        {
            if (_popupCommits.TryGetValue(commitIdentity, out var pending))
            {
                _popupCommits[commitIdentity] = pending with { IsAccepted = true };
                RemoveSupersededCandidatesExcept(commitIdentity);
            }
        }
    }

    public void CancelPopupCommit(string? commitIdentity)
    {
        if (commitIdentity is null)
            return;

        lock (_gate)
            _popupCommits.Remove(commitIdentity);
    }

    public bool TryTakePopupCommit(
        string? commitIdentity,
        out VideoSubtitlePopupCommit commit)
    {
        lock (_gate)
        {
            commit = default;
            if (commitIdentity is null
                || !_popupCommits.Remove(commitIdentity, out var pending))
            {
                return false;
            }

            commit = pending.Commit;
            return true;
        }
    }

    public void CancelCurrentRequest()
    {
        var current = InvalidateCurrentRequest(clearPopupCommits: false);
        current?.Cancel();
        current?.Dispose();
    }

    public void CancelCurrent()
    {
        var current = InvalidateCurrentRequest(clearPopupCommits: true);
        current?.Cancel();
        current?.Dispose();
    }

    private CancellationTokenSource? InvalidateCurrentRequest(bool clearPopupCommits)
    {
        lock (_gate)
        {
            _version++;
            var current = _currentCts;
            _currentCts = null;
            if (clearPopupCommits)
                _popupCommits.Clear();
            return current;
        }
    }

    private void RemoveCandidatesForRequest(long requestVersion)
    {
        string? existingTraceId = null;
        foreach (var pair in _popupCommits)
        {
            if (pair.Value.RequestVersion == requestVersion)
            {
                existingTraceId = pair.Key;
                break;
            }
        }

        if (existingTraceId is not null)
            _popupCommits.Remove(existingTraceId);
    }

    private void RemoveSupersededCandidatesExcept(string acceptedTraceId)
    {
        List<string>? staleTraceIds = null;
        foreach (var pair in _popupCommits)
        {
            if (!string.Equals(pair.Key, acceptedTraceId, StringComparison.Ordinal)
                && pair.Value.RequestVersion != _version)
            {
                (staleTraceIds ??= []).Add(pair.Key);
            }
        }

        if (staleTraceIds is null)
            return;

        foreach (var traceId in staleTraceIds)
            _popupCommits.Remove(traceId);
    }

    public void Dispose() => CancelCurrent();
}
