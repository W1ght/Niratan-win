using System;

namespace Hoshi.Services.Novels;

public sealed class ReaderProgrammaticNavigationTracker
{
    private long _generation;
    private PendingDestination? _pending;

    public bool HasPending => _pending is not null;

    public long? PendingGeneration => _pending?.Generation;

    public long Begin(int chapterIndex)
    {
        var generation = ++_generation;
        _pending = new PendingDestination(generation, chapterIndex);
        return generation;
    }

    public bool TryComplete(
        long generation,
        int chapterIndex,
        double resolvedProgress)
    {
        if (_pending is not { } pending
            || pending.Generation != generation
            || pending.ChapterIndex != chapterIndex
            || !double.IsFinite(resolvedProgress))
        {
            return false;
        }

        _pending = null;
        return true;
    }

    public void Cancel()
    {
        _generation++;
        _pending = null;
    }

    private sealed record PendingDestination(long Generation, int ChapterIndex);
}
