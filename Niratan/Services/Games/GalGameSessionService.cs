using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

internal sealed class GalGameSessionService : IGalGameSessionService
{
    private const int QueryLimitedInformation = 0x1000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineUnknown = 0;

    private readonly GalGameHookRuntimeStage _runtimeStage;
    private readonly GalGameIpcReader _ipcReader;
    private readonly ILogger<GalGameSessionService> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Process? _injectorProcess;
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
                _injectorProcess.Kill(entireProcessTree: true);
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
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            _injectorProcess = Process.Start(startInfo);
            if (_injectorProcess is null)
                return Failure(GalHookFailureReason.InjectionFailed, "The voice_hook injector could not be started.");

            _ = _injectorProcess.StandardError.ReadToEndAsync();

            SetState(_state with
            {
                Phase = GalHookSessionPhase.Injecting,
                InjectorPath = injector,
            });

            int? targetPid;
            if (attachProcessId is not null)
            {
                _ = _injectorProcess.StandardOutput.ReadToEndAsync();
                targetPid = attachProcessId;
            }
            else
            {
                targetPid = await ReadLaunchedProcessIdAsync(_injectorProcess, ct);
            }
            if (targetPid is null || targetPid <= 0)
            {
                await StopResourcesAsync();
                return Failure(
                    GalHookFailureReason.InjectionFailed,
                    "The injector did not report a target process id.");
            }

            SetState(_state with
            {
                GamePid = targetPid,
                Phase = GalHookSessionPhase.OpeningIpc,
            });

            var snapshot = await WaitForIpcAsync(targetPid.Value, ct);
            if (snapshot is null)
            {
                await StopResourcesAsync();
                return Failure(
                    GalHookFailureReason.IpcUnavailable,
                    "The voice_hook shared memory was not created or has an incompatible version.");
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

    private async Task<int?> ReadLaunchedProcessIdAsync(Process injector, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await injector.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
                return null;

            var marker = line.IndexOf("LAUNCH pid=", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                continue;
            var value = line[(marker + "LAUNCH pid=".Length)..].Trim();
            var separator = value.IndexOfAny([' ', '\t']);
            if (separator >= 0)
                value = value[..separator];
            if (int.TryParse(value, out var pid) && pid > 0)
                return pid;
        }
    }

    private async Task<GalGameIpcSnapshot?> WaitForIpcAsync(int processId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = _ipcReader.TryRead(processId);
            if (snapshot?.IsCompatible == true)
                return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
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

    private static IReadOnlyList<string> BuildInjectorArguments(
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
        return arguments;
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
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
        finally
        {
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
