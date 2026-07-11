using System;

namespace Hoshi.Services.Dictionary;

internal sealed class DictionaryPopupPendingLayoutCoordinator<TLayout>
{
    private long? _generation;
    private string? _traceId;
    private TLayout? _layout;

    public bool HasPending => _generation is not null;

    public void Stage(long generation, string? traceId, TLayout layout)
    {
        _generation = generation;
        _traceId = traceId;
        _layout = layout;
    }

    public bool TryGetGeneration(string? traceId, out long generation)
    {
        generation = default;
        if (_generation is not long current
            || !string.Equals(_traceId, traceId, StringComparison.Ordinal))
        {
            return false;
        }

        generation = current;
        return true;
    }

    public bool TryCancel(
        long generation,
        string? traceId,
        bool contentCancellationSucceeded)
    {
        if (!contentCancellationSucceeded || !Matches(generation, traceId))
            return false;

        Clear();
        return true;
    }

    public bool TryComplete(
        long generation,
        string? traceId,
        out TLayout layout)
    {
        layout = default!;
        if (!Matches(generation, traceId))
            return false;

        layout = _layout!;
        Clear();
        return true;
    }

    public bool TryAbort(long generation, string? traceId)
    {
        if (!Matches(generation, traceId))
            return false;

        Clear();
        return true;
    }

    public void Clear()
    {
        _generation = null;
        _traceId = null;
        _layout = default;
    }

    private bool Matches(long generation, string? traceId) =>
        _generation == generation
        && string.Equals(_traceId, traceId, StringComparison.Ordinal);
}
