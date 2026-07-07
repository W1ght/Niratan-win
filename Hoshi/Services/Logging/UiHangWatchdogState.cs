using System;
using System.Threading;

namespace Hoshi.Services.Logging;

public sealed class UiHangWatchdogState
{
    private long _lastUiTickMs;
    private int _hasObservedUiTick;

    public UiHangWatchdogState(long initialTimestampMs)
    {
        _lastUiTickMs = initialTimestampMs;
    }

    public void RecordUiTick(long timestampMs)
    {
        Volatile.Write(ref _lastUiTickMs, timestampMs);
        Volatile.Write(ref _hasObservedUiTick, 1);
    }

    public bool ShouldReportHang(long nowMs, long thresholdMs)
    {
        if (Volatile.Read(ref _hasObservedUiTick) == 0)
            return false;

        return ElapsedSinceLastUiTickMs(nowMs) > Math.Max(0, thresholdMs);
    }

    public long ElapsedSinceLastUiTickMs(long nowMs)
    {
        var elapsed = nowMs - Volatile.Read(ref _lastUiTickMs);
        return Math.Max(0, elapsed);
    }
}
