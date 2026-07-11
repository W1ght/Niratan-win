using System;

namespace Hoshi.Services.Dictionary;

internal readonly record struct DictionaryPopupRootState<TContext, TAnchor, TLayout>(
    long Generation,
    string? TraceId,
    TContext Context,
    TAnchor Anchor,
    TLayout Layout);

internal sealed class DictionaryPopupRootStateCoordinator<TContext, TAnchor, TLayout>
{
    private DictionaryPopupRootState<TContext, TAnchor, TLayout>? _pending;
    private DictionaryPopupRootState<TContext, TAnchor, TLayout>? _committed;

    public bool TryStage(
        long generation,
        string? traceId,
        TContext context,
        TAnchor anchor,
        TLayout layout)
    {
        if (_pending is not null)
            return false;

        _pending = new DictionaryPopupRootState<TContext, TAnchor, TLayout>(
            generation,
            traceId,
            context,
            anchor,
            layout);
        return true;
    }

    public bool TryGetPendingGeneration(string? traceId, out long generation)
    {
        generation = default;
        if (_pending is not { } pending
            || !string.Equals(pending.TraceId, traceId, StringComparison.Ordinal))
        {
            return false;
        }

        generation = pending.Generation;
        return true;
    }

    public bool TryGetPending(
        out DictionaryPopupRootState<TContext, TAnchor, TLayout> pending)
    {
        if (_pending is not { } current)
        {
            pending = default;
            return false;
        }

        pending = current;
        return true;
    }

    public bool TryCommit(
        long generation,
        string? traceId,
        out DictionaryPopupRootState<TContext, TAnchor, TLayout> committed)
    {
        committed = default;
        if (!MatchesPending(generation, traceId))
            return false;

        committed = _pending!.Value;
        _committed = committed;
        _pending = null;
        return true;
    }

    public bool TryAbort(long generation, string? traceId)
    {
        if (!MatchesPending(generation, traceId))
            return false;

        _pending = null;
        return true;
    }

    public bool TryGetCommitted(
        out DictionaryPopupRootState<TContext, TAnchor, TLayout> committed)
    {
        if (_committed is not { } current)
        {
            committed = default;
            return false;
        }

        committed = current;
        return true;
    }

    public bool IsCommitted(long generation, string? traceId) =>
        _committed is { } committed
        && committed.Generation == generation
        && string.Equals(committed.TraceId, traceId, StringComparison.Ordinal);

    public bool TryUpdatePendingLayout(
        long generation,
        string? traceId,
        TLayout layout,
        out DictionaryPopupRootState<TContext, TAnchor, TLayout> updated)
    {
        updated = default;
        if (!MatchesPending(generation, traceId))
            return false;

        updated = _pending!.Value with { Layout = layout };
        _pending = updated;
        return true;
    }

    public bool TryUpdateCommittedLayout(
        long generation,
        string? traceId,
        TLayout layout,
        out DictionaryPopupRootState<TContext, TAnchor, TLayout> updated)
    {
        updated = default;
        if (!IsCommitted(generation, traceId))
            return false;

        updated = _committed!.Value with { Layout = layout };
        _committed = updated;
        return true;
    }

    public void Clear()
    {
        _pending = null;
        _committed = null;
    }

    private bool MatchesPending(long generation, string? traceId) =>
        _pending is { } pending
        && pending.Generation == generation
        && string.Equals(pending.TraceId, traceId, StringComparison.Ordinal);
}
