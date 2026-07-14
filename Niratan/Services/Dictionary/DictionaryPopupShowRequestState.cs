using System.Threading;

namespace Niratan.Services.Dictionary;

internal sealed class DictionaryPopupShowRequestState
{
    private const int Queued = 0;
    private const int GenerationStarted = 1;
    private const int Dropped = 2;

    private int _state;

    public bool TryStartGeneration() =>
        Interlocked.CompareExchange(
            ref _state,
            GenerationStarted,
            Queued) == Queued;

    public bool TryDropBeforeGeneration() =>
        Interlocked.CompareExchange(
            ref _state,
            Dropped,
            Queued) == Queued;
}
