using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Games;

internal enum GalGameInjectorWaitOutcome
{
    Ready,
    Exited,
    TimedOut,
}

internal sealed record GalGameInjectorWaitResult(
    GalGameInjectorWaitOutcome Outcome,
    int ProcessId = 0,
    int? ExitCode = null);

/// <summary>
/// Consumes the line-oriented stdout/stderr contract published by the native
/// injector. The injector remains alive for the session, so callers must keep
/// draining both streams after the ready line has been observed.
/// </summary>
internal sealed partial class GalGameInjectorProtocol
{
    internal const string CapabilityToken = "native_loopback_policy_v1";
    private const int DiagnosticTailLimit = 4096;

    private readonly object _gate = new();
    private readonly StringBuilder _diagnostics = new();
    private readonly TaskCompletionSource<int> _hookedProcessId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _launchedProcessId;
    private string? _failureToken;

    public Task<int> HookedProcessId => _hookedProcessId.Task;

    public int? LaunchedProcessId
    {
        get
        {
            lock (_gate)
                return _launchedProcessId;
        }
    }

    public string? FailureToken
    {
        get
        {
            lock (_gate)
                return _failureToken;
        }
    }

    public string DiagnosticTail
    {
        get
        {
            lock (_gate)
                return _diagnostics.ToString().Trim();
        }
    }

    public void ObserveStandardOutput(string line)
    {
        Append(line);
        if (TryParseProcessId(line, "LAUNCH pid=", out var launched))
        {
            lock (_gate)
                _launchedProcessId = launched;
        }
        if (TryParseProcessId(line, "OK hooked pid=", out var hooked))
            _hookedProcessId.TrySetResult(hooked);
    }

    public void ObserveStandardError(string line)
    {
        Append(line);
        var match = FailureReasonRegex().Match(line);
        if (!match.Success)
            return;
        lock (_gate)
            _failureToken = match.Groups[1].Value;
    }

    internal static async Task<GalGameInjectorWaitResult> WaitForReadyAsync(
        Process injector,
        GalGameInjectorProtocol protocol,
        Task outputDrain,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var exitTask = injector.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(protocol.HookedProcessId, exitTask, timeoutTask);
        if (completed == protocol.HookedProcessId)
        {
            return new(
                GalGameInjectorWaitOutcome.Ready,
                await protocol.HookedProcessId);
        }
        if (completed == timeoutTask)
        {
            ct.ThrowIfCancellationRequested();
            return new(GalGameInjectorWaitOutcome.TimedOut);
        }

        try { await outputDrain.WaitAsync(TimeSpan.FromSeconds(1), ct); }
        catch (TimeoutException) { }
        if (protocol.HookedProcessId.IsCompletedSuccessfully)
        {
            return new(
                GalGameInjectorWaitOutcome.Ready,
                protocol.HookedProcessId.Result,
                injector.ExitCode);
        }
        return new(
            GalGameInjectorWaitOutcome.Exited,
            ExitCode: injector.ExitCode);
    }

    internal static bool TryParseProcessId(string line, string marker, out int processId)
    {
        processId = 0;
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;
        var start = markerIndex + marker.Length;
        var end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
            end++;
        return end > start
            && int.TryParse(line.AsSpan(start, end - start), out processId)
            && processId > 0;
    }

    private void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_gate)
        {
            if (_diagnostics.Length > 0)
                _diagnostics.AppendLine();
            _diagnostics.Append(line.Trim());
            if (_diagnostics.Length <= DiagnosticTailLimit * 2)
                return;
            var keep = _diagnostics.ToString();
            _diagnostics.Clear();
            _diagnostics.Append(keep[^DiagnosticTailLimit..]);
        }
    }

    [GeneratedRegex(@"ERR reason=([a-zA-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FailureReasonRegex();
}
