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

    public bool BeginPending(long generation, string? traceId)
    {
        if (CommitInFlightGeneration is not null)
            return HasCommittedContent;

        _pendingGeneration = generation;
        _pendingTraceId = traceId;
        return HasCommittedContent;
    }

    public bool TryAcceptCommit(long generation)
    {
        if (_pendingGeneration != generation || CommitInFlightGeneration is not null)
            return false;

        CommitInFlightGeneration = generation;
        _pendingGeneration = null;
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

    public bool CancelPending(long generation, string? traceId)
    {
        if (_pendingGeneration != generation)
            return false;
        if (!string.Equals(traceId, _pendingTraceId, StringComparison.Ordinal))
            return false;

        _pendingGeneration = null;
        _pendingTraceId = null;
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
