using System;

namespace Niratan.Services.Sasayaki;

internal sealed class SasayakiSeekLandingState
{
    internal const double LandingToleranceSeconds = 0.75;

    private readonly object _gate = new();
    private double? _pendingSeconds;

    public double? PendingSeconds
    {
        get
        {
            lock (_gate)
                return _pendingSeconds;
        }
    }

    public void Request(double seconds)
    {
        lock (_gate)
            _pendingSeconds = seconds;
    }

    public double ResolvePosition(double playerPositionSeconds)
    {
        lock (_gate)
            return _pendingSeconds ?? playerPositionSeconds;
    }

    public bool TryAcceptPosition(double playerPositionSeconds)
    {
        if (!double.IsFinite(playerPositionSeconds))
            return false;

        lock (_gate)
        {
            if (!_pendingSeconds.HasValue)
                return true;

            if (Math.Abs(playerPositionSeconds - _pendingSeconds.Value) > LandingToleranceSeconds)
                return false;

            _pendingSeconds = null;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
            _pendingSeconds = null;
    }
}
