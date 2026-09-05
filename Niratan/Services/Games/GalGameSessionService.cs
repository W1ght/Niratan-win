using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

internal sealed class GalGameSessionService : IGalGameSessionService
{
    private const int QueryLimitedInformation = 0x1000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineUnknown = 0;
    private static readonly TimeSpan InjectorReadyTimeout = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan IpcOpenTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CapabilityTimeout = TimeSpan.FromSeconds(5);

    private readonly GalGameHookRuntimeStage _runtimeStage;
    private readonly GalGameIpcReader _ipcReader;
    private readonly ILogger<GalGameSessionService> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Process? _injectorProcess;
    private Task? _stdoutDrainTask;
    private Task? _stderrDrainTask;
    private GalHookSessionState _state = new();
    private ulong _textCursor;
    private bool _disposed;

    public GalGameSessionService(
        GalGameHookRuntimeStage runtimeStage,
        GalGameIpcReader ipcReader,
        ILogger<GalGameSessionService> logger)
    {
        _runtimeStage = runtimeStage;
        _ipcReader = ipcReader;
        _logger = logger;
    }

    public GalHookSessionState State => _state;

    public event EventHandler<GalHookSessionState>? StateChanged;

    public IReadOnlyList<GalGameTextLine> PollText()
    {
        var processId = _state.GamePid;
        if (processId is not > 0 || !_state.IsActive)
            return [];

        RefreshIpcSnapshot(processId.Value);

        var lines = _ipcReader.TryPollText(processId.Value, _textCursor, out var latestCount);
        if (latestCount > _textCursor)
            _textCursor = latestCount;
        return lines;
    }

    public IReadOnlyList<GalGameThreadPreview> ReadThreadPreviews()
    {
        var processId = _state.GamePid;
        if (processId is > 0 && _state.IsActive)
            RefreshIpcSnapshot(processId.Value);
        return processId is > 0 && _state.IsActive
            ? _ipcReader.TryReadThreadPreviews(processId.Value)
            : [];
    }

    public bool SelectTextThread(ulong threadId)
    {
        var processId = _state.GamePid;
        if (processId is not > 0 || !_ipcReader.TrySelectTextThread(processId.Value, threadId))
            return false;

        // A thread switch must immediately make the selected lane readable.
        // Keeping the global cursor here would discard the lane's recent hook
        // text when it was captured before the user opened the selector.
        _textCursor = 0;
        return true;
    }

    public GalGameAudioCapture? CaptureAudio(GalGameTextLine line)
    {
        var processId = _state.GamePid;
        if (processId is not > 0)
            return null;
        return _ipcReader.TryGrabClipNear(processId.Value, line.TimestampMs)
            ?? _ipcReader.TryGrabRecent(processId.Value)
            ?? _ipcReader.TryGrabLoopbackWindow(processId.Value, line.TimestampMs);
    }

    public bool SetIngameLookupEnabled(bool enabled)
    {
        var processId = _state.GamePid;
        return processId is > 0 && _state.Ipc?.HasIngameLookup == true
            && _ipcReader.TrySetLookupEnabled(processId.Value, enabled);
    }

    public GalGameLookupHit? PollIngameLookupHit(
        ulong afterSequence,
        out ulong hitCount)
    {
        var processId = _state.GamePid;
        if (processId is not > 0 || !_state.IsActive)
        {
            hitCount = 0;
            return null;
        }

        return _ipcReader.TryReadLookupHit(processId.Value, afterSequence, out hitCount);
    }

    public IReadOnlyList<GalGameLookupInput> PollIngameLookupInputs(
        ulong afterSequence,
        out ulong inputCount)
    {
        var processId = _state.GamePid;
        if (processId is not > 0 || !_state.IsActive)
        {
            inputCount = 0;
            return [];
        }

        return _ipcReader.TryReadLookupInputs(processId.Value, afterSequence, out inputCount);
    }

    public bool PublishIngameLookupFrame(GalGameLookupCardFrame frame)
    {
        var processId = _state.GamePid;
        return processId is > 0 && _state.Ipc?.HasIngameLookup == true
            && _ipcReader.TryPublishLookupFrame(processId.Value, frame);
    }

    public bool DismissIngameLookup(ulong hitSequence)
    {
        var processId = _state.GamePid;
        return processId is > 0 && _state.Ipc?.HasIngameLookup == true
            && _ipcReader.TryPublishLookupDismiss(processId.Value, hitSequence);
    }

    public Task<GalHookOperationResult> LaunchAsync(
        GalGameEntry game,
        CancellationToken ct = default) =>
        RunOperationAsync(game.ExePath, game, null, ct);

    public Task<GalHookOperationResult> AttachAsync(
        int processId,
        CancellationToken ct = default) =>
        RunOperationAsync(null, null, processId, ct);

    public async Task StopAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (!_state.IsActive && _injectorProcess is null)
                return;
            SetState(_state with { Phase = GalHookSessionPhase.Stopping });
            await StopResourcesAsync();
            _textCursor = 0;
            SetState(new GalHookSessionState());
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_injectorProcess is { HasExited: false })
                _injectorProcess.Kill();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _injectorProcess?.Dispose();
        _operationGate.Dispose();
    }

    private async Task<GalHookOperationResult> RunOperationAsync(
        string? launchExe,
        GalGameEntry? game,
        int? attachProcessId,
        CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            await StopResourcesAsync();
            _textCursor = 0;
            SetState(new GalHookSessionState
            {
                Phase = GalHookSessionPhase.Resolving,
                LaunchExe = launchExe,
            });

            if (!OperatingSystem.IsWindows())
                return Failure(GalHookFailureReason.UnsupportedPlatform, "Galgame hook is Windows-only.");

            if (launchExe is not null && !File.Exists(launchExe))
                return Failure(GalHookFailureReason.InvalidTarget, "The game executable does not exist.");

            if (attachProcessId is <= 0)
                return Failure(GalHookFailureReason.InvalidTarget, "The target process id is invalid.");

            var architecture = ResolveArchitecture(attachProcessId, launchExe);
            SetState(_state with
            {
                Phase = launchExe is null ? GalHookSessionPhase.Attaching : GalHookSessionPhase.Launching,
                Architecture = architecture,
            });

            var injector = await _runtimeStage.EnsureStagedAsync(architecture);
            if (injector is null)
                return Failure(
                    GalHookFailureReason.HelperMissing,
                    $"No {architecture} voice_hook runtime is bundled with this build.");

            if (!await SupportsRequiredProtocolAsync(injector, ct))
            {
                return Failure(
                    GalHookFailureReason.HelperMissing,
                    ResourceStringHelper.GetString(
                        "GamesHookProtocolMismatch",
                        "The bundled capture helper is incompatible with this build."));
            }

            var arguments = BuildInjectorArguments(
                launchExe,
                game,
                attachProcessId,
                _runtimeStage.GetUnityRuntimeDirectory(architecture));
            var startInfo = new ProcessStartInfo
            {
                FileName = injector,
                WorkingDirectory = Path.GetDirectoryName(injector) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            _injectorProcess = Process.Start(startInfo);
            if (_injectorProcess is null)
                return Failure(GalHookFailureReason.InjectionFailed, "The voice_hook injector could not be started.");

            var protocol = new GalGameInjectorProtocol();
            _stdoutDrainTask = DrainOutputAsync(
                _injectorProcess.StandardOutput,
                protocol.ObserveStandardOutput);
            _stderrDrainTask = DrainOutputAsync(
                _injectorProcess.StandardError,
                protocol.ObserveStandardError);

            SetState(_state with
            {
                Phase = GalHookSessionPhase.Injecting,
                InjectorPath = injector,
            });

            var handshake = await WaitForInjectorReadyAsync(
                _injectorProcess,
                protocol,
                attachProcessId,
                ct);
            if (!handshake.Success)
            {
                var launchedPid = protocol.LaunchedProcessId;
                await StopResourcesAsync();
                if (launchedPid is > 0)
                    SetState(_state with { GamePid = launchedPid });
                return Failure(
                    GalHookFailureReason.InjectionFailed,
                    handshake.Detail);
            }

            var targetPid = handshake.ProcessId;

            SetState(_state with
            {
                GamePid = targetPid,
                Phase = GalHookSessionPhase.OpeningIpc,
            });

            var snapshot = await WaitForIpcAsync(targetPid, ct);
            if (snapshot is null)
            {
                await StopResourcesAsync();
                return Failure(
                    GalHookFailureReason.IpcUnavailable,
                    ResourceStringHelper.GetString(
                        "GamesHookIpcUnavailable",
                        "The capture helper reported ready, but its shared channel could not be opened."));
            }

            SetState(_state with
            {
                Phase = GalHookSessionPhase.WaitingSignals,
                Ipc = snapshot,
            });

            var finalPhase = snapshot.Hooked != 0
                ? GalHookSessionPhase.Running
                : GalHookSessionPhase.Degraded;
            SetState(_state with { Phase = finalPhase, Ipc = snapshot });
            return new GalHookOperationResult(true, null, null, _state);
        }
        catch (OperationCanceledException)
        {
            await StopResourcesAsync();
            return Failure(
                ct.IsCancellationRequested
                    ? GalHookFailureReason.Cancelled
                    : GalHookFailureReason.InjectionFailed,
                ct.IsCancellationRequested
                    ? "The hook operation was cancelled."
                    : "The injector timed out while waiting for the game or shared memory.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Galgame hook operation failed");
            await StopResourcesAsync();
            return Failure(GalHookFailureReason.InjectionFailed, ex.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<(bool Success, int ProcessId, string Detail)> WaitForInjectorReadyAsync(
        Process injector,
        GalGameInjectorProtocol protocol,
        int? expectedAttachProcessId,
        CancellationToken ct)
    {
        var outputDrain = Task.WhenAll(new[] { _stdoutDrainTask, _stderrDrainTask }
            .Where(task => task is not null)
            .Cast<Task>());
        var wait = await GalGameInjectorProtocol.WaitForReadyAsync(
            injector,
            protocol,
            outputDrain,
            InjectorReadyTimeout,
            ct);
        if (wait.Outcome == GalGameInjectorWaitOutcome.Ready)
            return ValidateHookedProcessId(wait.ProcessId, expectedAttachProcessId);

        if (wait.Outcome == GalGameInjectorWaitOutcome.TimedOut)
        {
            return (false, 0, BuildInjectorFailureDetail(
                protocol,
                null,
                ResourceStringHelper.GetString(
                    "GamesHookReadyTimeout",
                    "The capture helper did not report ready within 32 seconds.")));
        }

        return (false, 0, BuildInjectorFailureDetail(
            protocol,
            wait.ExitCode,
            ResourceStringHelper.GetString(
                "GamesHookExitedEarly",
                "The capture helper exited before it reported ready.")));
    }

    private static (bool Success, int ProcessId, string Detail) ValidateHookedProcessId(
        int processId,
        int? expectedAttachProcessId)
    {
        if (expectedAttachProcessId is > 0 && processId != expectedAttachProcessId.Value)
        {
            return (false, 0, ResourceStringHelper.FormatString(
                "GamesHookWrongProcess",
                "The capture helper attached to PID {0}, but PID {1} was requested.",
                processId,
                expectedAttachProcessId.Value));
        }
        return (true, processId, string.Empty);
    }

    private async Task<GalGameIpcSnapshot?> WaitForIpcAsync(int processId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + IpcOpenTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = _ipcReader.TryRead(processId);
            if (snapshot?.IsCompatible == true)
                return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        return null;
    }

    private void RefreshIpcSnapshot(int processId)
    {
        var snapshot = _ipcReader.TryRead(processId);
        if (snapshot?.IsCompatible != true || snapshot == _state.Ipc)
            return;

        var phase = snapshot.Hooked != 0
            ? GalHookSessionPhase.Running
            : GalHookSessionPhase.Degraded;
        SetState(_state with
        {
            Phase = phase,
            Ipc = snapshot,
        });
    }

    internal static IReadOnlyList<string> BuildInjectorArguments(
        string? launchExe,
        GalGameEntry? game,
        int? attachProcessId,
        string? unityRuntimeDirectory)
    {
        var arguments = new List<string>();
        if (launchExe is not null)
        {
            arguments.Add("--launch");
            arguments.Add(launchExe);
            arguments.Add("--hold");
            arguments.Add("--wait-ms");
            arguments.Add("30000");
            if (!string.IsNullOrWhiteSpace(unityRuntimeDirectory))
            {
                arguments.Add("--unity-runtime");
                arguments.Add(unityRuntimeDirectory);
            }
            if (string.Equals(game?.JapaneseLocaleMode, "on", StringComparison.OrdinalIgnoreCase))
                arguments.Add("--japanese-locale");
            if (!string.IsNullOrWhiteSpace(game?.Workdir))
            {
                arguments.Add("--workdir");
                arguments.Add(game.Workdir);
            }
            foreach (var token in game?.LaunchArgumentTokens ?? [])
            {
                arguments.Add("--arg");
                arguments.Add(token);
            }
        }
        else
        {
            arguments.Add("--pid");
            arguments.Add((attachProcessId ?? 0).ToString());
            arguments.Add("--hold");
            arguments.Add("--wait-ms");
            arguments.Add("30000");
        }
        arguments.Add("--native-loopback-policy");
        arguments.Add("deny");
        return arguments;
    }

    private static async Task DrainOutputAsync(StreamReader reader, Action<string> observer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                observer(line);
        }
        catch (IOException)
        {
            // The process can be terminated while an async pipe read is pending.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<bool> SupportsRequiredProtocolAsync(string injector, CancellationToken ct)
    {
        using var probe = Process.Start(new ProcessStartInfo
        {
            FileName = injector,
            WorkingDirectory = Path.GetDirectoryName(injector) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "--capabilities" },
        });
        if (probe is null)
            return false;
        try
        {
            var stdoutTask = probe.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = probe.StandardError.ReadToEndAsync(ct);
            await probe.WaitForExitAsync(ct).WaitAsync(CapabilityTimeout, ct);
            var stdout = (await stdoutTask).Trim();
            _ = await stderrTask;
            return probe.ExitCode == 0
                && string.Equals(
                    stdout,
                    GalGameInjectorProtocol.CapabilityToken,
                    StringComparison.Ordinal);
        }
        catch (TimeoutException)
        {
            try { if (!probe.HasExited) probe.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return false;
        }
    }

    private string BuildInjectorFailureDetail(
        GalGameInjectorProtocol protocol,
        int? exitCode,
        string fallback)
    {
        var reason = InjectorFailureReasonText(protocol.FailureToken, protocol.DiagnosticTail)
            ?? fallback;
        var evidence = LastDiagnosticLine(protocol.DiagnosticTail);
        var suffix = string.Join(" · ", new[]
        {
            exitCode is null ? null : $"exit={exitCode.Value}",
            string.IsNullOrWhiteSpace(evidence) ? null : evidence,
        }.Where(value => value is not null));
        var detail = suffix.Length == 0 ? reason : $"{reason} ({suffix})";
        _logger.LogWarning(
            "Galgame injector failed: token={FailureToken}, exit={ExitCode}, diagnostics={Diagnostics}",
            protocol.FailureToken,
            exitCode,
            protocol.DiagnosticTail);
        return detail;
    }

    private static string? InjectorFailureReasonText(string? token, string diagnostics)
    {
        token ??= diagnostics switch
        {
            var value when value.Contains("OpenProcess(", StringComparison.Ordinal) => "accessDenied",
            var value when value.Contains("位数不匹配", StringComparison.Ordinal) => "bitnessMismatch",
            var value when value.Contains("hook DLL not found", StringComparison.Ordinal) => "hookDllMissing",
            var value when value.Contains("未收到就绪信号", StringComparison.Ordinal) => "readyTimeout",
            _ => null,
        };
        return token?.ToLowerInvariant() switch
        {
            "accessdenied" => ResourceStringHelper.GetString(
                "GamesHookAccessDenied",
                "The game is running with higher privileges. Start Niratan as administrator and try again."),
            "bitnessmismatch" => ResourceStringHelper.GetString(
                "GamesHookBitnessMismatch",
                "The capture helper architecture does not match the game."),
            "hookdllmissing" => ResourceStringHelper.GetString(
                "GamesHookDllMissing",
                "The bundled capture helper is incomplete."),
            "gameexemissing" => ResourceStringHelper.GetString(
                "GamesHookGameMissing",
                "The game executable no longer exists."),
            "stalesession" => ResourceStringHelper.GetString(
                "GamesHookStaleSession",
                "The previous capture session has not been released yet. Try again in a moment."),
            "residenthookmismatch" => ResourceStringHelper.GetString(
                "GamesHookResidentMismatch",
                "A previous capture component is still loaded in the game. Restart the game once."),
            "readytimeout" => ResourceStringHelper.GetString(
                "GamesHookNativeReadyTimeout",
                "The hook library did not finish loading in time. Antivirus scanning may be the cause."),
            "injectionfailed" or "guardedhookfailed" => ResourceStringHelper.GetString(
                "GamesHookInjectionBlocked",
                "Injection into the game was blocked. Check antivirus exclusions for Niratan and the game."),
            "resumefailed" => ResourceStringHelper.GetString(
                "GamesHookResumeFailed",
                "The launched game could not be resumed and was stopped. Launch it again."),
            "steamtimeout" => ResourceStringHelper.GetString(
                "GamesHookSteamTimeout",
                "Steam accepted the request, but the game process did not appear in time."),
            "createprocessfailed" => ResourceStringHelper.GetString(
                "GamesHookCreateProcessFailed",
                "Windows could not start the game process."),
            "elevationrequired" => ResourceStringHelper.GetString(
                "GamesHookElevationRequired",
                "Windows requires administrator permission to start this game."),
            _ => null,
        };
    }

    private static string LastDiagnosticLine(string diagnostics)
    {
        var line = diagnostics
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        const int maxLength = 200;
        return line.Length <= maxLength ? line : $"…{line[^maxLength..]}";
    }

    private async Task AwaitOutputDrainAsync()
    {
        var tasks = new[] { _stdoutDrainTask, _stderrDrainTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
            return;
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { }
    }

    private async Task StopResourcesAsync()
    {
        var process = _injectorProcess;
        _injectorProcess = null;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
        finally
        {
            await AwaitOutputDrainAsync();
            _stdoutDrainTask = null;
            _stderrDrainTask = null;
            process.Dispose();
        }
    }

    private GalHookOperationResult Failure(GalHookFailureReason reason, string detail)
    {
        SetState(_state with
        {
            Phase = GalHookSessionPhase.Error,
            LastError = detail,
            Detail = detail,
        });
        return new GalHookOperationResult(false, reason, detail, _state);
    }

    private void SetState(GalHookSessionState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static string ResolveArchitecture(int? processId, string? launchExe)
    {
        if (processId is > 0)
        {
            var handle = OpenProcess(QueryLimitedInformation, false, processId.Value);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    if (IsWow64Process2(handle, out var processMachine, out var nativeMachine))
                    {
                        return processMachine != ImageFileMachineUnknown
                            || nativeMachine == ImageFileMachineI386
                            ? "x86"
                            : "x64";
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        if (launchExe is not null && TryReadPeArchitecture(launchExe, out var fileArchitecture))
            return fileArchitecture;

        return Environment.Is64BitOperatingSystem ? "x64" : "x86";
    }

    private static bool TryReadPeArchitecture(string path, out string architecture)
    {
        architecture = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64)
                return false;
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset + 6 > stream.Length)
                return false;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
                return false;
            var machine = reader.ReadUInt16();
            architecture = machine == ImageFileMachineI386 ? "x86" : "x64";
            return machine is ImageFileMachineI386 or 0x8664;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        IntPtr process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
