using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Video;

public sealed record VideoPlaybackRestoreTarget(TimeSpan Position, TimeSpan Duration);

public static class VideoPlaybackRestoreWaiter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultSeekTolerance = TimeSpan.FromSeconds(2);

    public static async Task<VideoPlaybackRestoreTarget?> WaitForRestoreTargetAsync(
        VideoPlaybackState state,
        Func<CancellationToken, Task<TimeSpan>> getDurationAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        if (state.PositionSeconds <= 0)
            return null;

        var duration = await WaitForDurationAsync(
            getDurationAsync,
            delayAsync,
            timeout ?? DefaultTimeout,
            pollInterval ?? DefaultPollInterval,
            ct);
        var position = state.ResolveRestorePosition(duration);
        if (position == null)
            return null;

        return new VideoPlaybackRestoreTarget(
            position.Value,
            duration > TimeSpan.Zero ? duration : StoredDuration(state));
    }

    public static async Task<bool> WaitForSeekPositionAsync(
        TimeSpan target,
        Func<CancellationToken, Task<TimeSpan>> getPositionAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? tolerance = null,
        CancellationToken ct = default)
    {
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var effectivePollInterval = pollInterval ?? DefaultPollInterval;
        effectivePollInterval = effectivePollInterval > TimeSpan.Zero
            ? effectivePollInterval
            : DefaultPollInterval;
        var effectiveTolerance = tolerance ?? DefaultSeekTolerance;
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = await getPositionAsync(ct);
            if (IsWithinTolerance(position, target, effectiveTolerance))
                return true;

            if (effectiveTimeout <= TimeSpan.Zero)
                return false;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            await delayAsync(remaining < effectivePollInterval ? remaining : effectivePollInterval, ct);
        }
    }

    private static async Task<TimeSpan> WaitForDurationAsync(
        Func<CancellationToken, Task<TimeSpan>> getDurationAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var duration = await getDurationAsync(ct);
            if (duration > TimeSpan.Zero || timeout <= TimeSpan.Zero)
                return duration;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return duration;

            await delayAsync(remaining < pollInterval ? remaining : pollInterval, ct);
        }
    }

    private static TimeSpan StoredDuration(VideoPlaybackState state) =>
        state.DurationSeconds > 0 && double.IsFinite(state.DurationSeconds)
            ? TimeSpan.FromSeconds(state.DurationSeconds)
            : TimeSpan.Zero;

    private static bool IsWithinTolerance(TimeSpan position, TimeSpan target, TimeSpan tolerance) =>
        Math.Abs((position - target).TotalSeconds) <= Math.Max(0, tolerance.TotalSeconds);
}
