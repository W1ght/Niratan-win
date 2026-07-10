using System;

namespace Hoshi.Services.Dictionary;

internal readonly record struct DictionaryPopupContentCommit(long Generation, string? TraceId);

internal sealed class DictionaryPopupDisplayTransaction
{
    private long? _pendingGeneration;
    private string? _pendingTraceId;

    public bool HasCommittedContent { get; private set; }
    public long? PendingGeneration => _pendingGeneration;
    public long? CommittedGeneration { get; private set; }

    public bool BeginPending(long generation, string? traceId)
    {
        _pendingGeneration = generation;
        _pendingTraceId = traceId;
        return HasCommittedContent;
    }

    public bool TryCommit(long generation, out DictionaryPopupContentCommit commit)
    {
        commit = default;
        if (_pendingGeneration != generation)
            return false;

        commit = new DictionaryPopupContentCommit(generation, _pendingTraceId);
        _pendingGeneration = null;
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
        HasCommittedContent = false;
        CommittedGeneration = null;
    }
}
