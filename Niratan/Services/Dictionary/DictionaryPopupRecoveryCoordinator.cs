using System;

namespace Niratan.Services.Dictionary;

internal readonly record struct DictionaryPopupRecoveryTicket(
    long Generation,
    long FailedEpoch,
    long Attempt);

internal static class DictionaryPopupDocumentEpoch
{
    public static bool Matches(long currentEpoch, long commandEpoch) =>
        currentEpoch == commandEpoch;
}

internal sealed class DictionaryPopupRecoveryCoordinator
{
    private readonly object _gate = new();
    private long? _generation;
    private long? _failedEpoch;
    private long _nextAttempt;
    private long? _activeAttempt;

    public bool TryStartAttempt(
        long generation,
        long failedEpoch,
        out DictionaryPopupRecoveryTicket ticket)
    {
        lock (_gate)
        {
            ticket = default;
            if (_generation is null)
            {
                _generation = generation;
                _failedEpoch = failedEpoch;
            }
            else if (_generation != generation || _failedEpoch != failedEpoch)
            {
                return false;
            }

            if (_activeAttempt is not null)
                return false;

            var attempt = ++_nextAttempt;
            _activeAttempt = attempt;
            ticket = new DictionaryPopupRecoveryTicket(generation, failedEpoch, attempt);
            return true;
        }
    }

    public void FailAttempt(DictionaryPopupRecoveryTicket ticket)
    {
        lock (_gate)
        {
            if (MatchesActive(ticket))
                _activeAttempt = null;
        }
    }

    public bool TryComplete(DictionaryPopupRecoveryTicket ticket, long freshEpoch)
    {
        lock (_gate)
        {
            if (!MatchesActive(ticket) || freshEpoch <= ticket.FailedEpoch)
                return false;

            Clear();
            return true;
        }
    }

    public bool Cancel(DictionaryPopupRecoveryTicket ticket)
    {
        lock (_gate)
        {
            if (!MatchesActive(ticket))
                return false;
            Clear();
            return true;
        }
    }

    public bool Cancel(long generation, long failedEpoch)
    {
        lock (_gate)
        {
            if (_generation != generation || _failedEpoch != failedEpoch)
                return false;
            Clear();
            return true;
        }
    }

    public bool IsRecovering(long generation, long failedEpoch)
    {
        lock (_gate)
            return _generation == generation && _failedEpoch == failedEpoch;
    }

    public bool CanCompleteAccepted(long generation, long documentEpoch)
    {
        lock (_gate)
            return _generation != generation || _failedEpoch != documentEpoch;
    }

    private bool MatchesActive(DictionaryPopupRecoveryTicket ticket) =>
        _generation == ticket.Generation
        && _failedEpoch == ticket.FailedEpoch
        && _activeAttempt == ticket.Attempt;

    private void Clear()
    {
        _generation = null;
        _failedEpoch = null;
        _activeAttempt = null;
    }
}
