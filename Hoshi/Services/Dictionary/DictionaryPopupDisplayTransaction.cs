using System;

namespace Hoshi.Services.Dictionary;

internal readonly record struct DictionaryPopupContentCommit(long Generation, string? TraceId);

internal sealed class DictionaryPopupDisplayTransaction
{
    private long? _pendingGeneration;
    private string? _pendingTraceId;

    public bool HasCommittedContent { get; private set; }
    public long? PendingGeneration => _pendingGeneration;
    public long? CommitInFlightGeneration { get; private set; }
    public long? CommittedGeneration { get; private set; }

    public bool TryBeginPending(
        long generation,
        string? traceId,
        out bool preserveCommittedContent)
    {
        preserveCommittedContent = HasCommittedContent;
        if (CommitInFlightGeneration is not null
            || _pendingGeneration is not null)
        {
            return false;
        }

        _pendingGeneration = generation;
        _pendingTraceId = traceId;
        return true;
    }

    public bool TryAcceptCommit(long generation)
    {
        if (_pendingGeneration != generation || CommitInFlightGeneration is not null)
            return false;

        CommitInFlightGeneration = generation;
        _pendingGeneration = null;
        return true;
    }

    public bool TryGetPending(out DictionaryPopupContentCommit pending)
    {
        if (_pendingGeneration is not long generation)
        {
            pending = default;
            return false;
        }

        pending = new DictionaryPopupContentCommit(generation, _pendingTraceId);
        return true;
    }

    public bool TryCompleteCommit(long generation, out DictionaryPopupContentCommit commit)
    {
        commit = default;
        if (CommitInFlightGeneration != generation)
            return false;

        commit = new DictionaryPopupContentCommit(generation, _pendingTraceId);
        CommitInFlightGeneration = null;
        _pendingTraceId = null;
        HasCommittedContent = true;
        CommittedGeneration = generation;
        return true;
    }

    public bool TryAbortCommit(long generation)
    {
        if (CommitInFlightGeneration != generation)
            return false;

        CommitInFlightGeneration = null;
        _pendingTraceId = null;
        return true;
    }

    public bool TryCancelPending(
        long generation,
        string? traceId,
        out DictionaryPopupContentCommit aborted)
    {
        aborted = default;
        if (_pendingGeneration != generation)
            return false;
        if (!string.Equals(traceId, _pendingTraceId, StringComparison.Ordinal))
            return false;

        _pendingGeneration = null;
        _pendingTraceId = null;
        aborted = new DictionaryPopupContentCommit(generation, traceId);
        return true;
    }

    public void Dismiss()
    {
        _pendingGeneration = null;
        _pendingTraceId = null;
        CommitInFlightGeneration = null;
        HasCommittedContent = false;
        CommittedGeneration = null;
    }
}
