using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Models.Games;

public enum GalHookDiagnosticOutcome
{
    Passed,
    Pending,
    Failed,
    NotRun,
    NotApplicable,
    Unavailable,
}

public enum GalHookDiagnosticBoundary
{
    ProcessFound,
    HelperReady,
    IpcReady,
    TextObserved,
    TextThreadSelected,
    ResourceObserved,
    PcmObserved,
    Paired,
    LoopbackObserved,
    CardE2e,
}

public sealed record GalHookDiagnosticResult(
    GalHookDiagnosticBoundary Boundary,
    GalHookDiagnosticOutcome Outcome,
    string Evidence);

public static class GalHookDiagnosticFunctions
{
    public static IReadOnlyList<GalHookDiagnosticResult> Evaluate(
        GalHookSessionState state,
        int capturedLineCount,
        ulong? selectedThreadId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var ipc = state.Ipc;
        var hasPcm = ipc is not null
            && (ipc.TotalWritten > 0 || ipc.ClipWriteCount > 0);
        var hasLoopback = ipc?.HasLoopbackAudio == true;
        var hasResource = ipc is not null
            && GalHookDiagnosticBits.HasResourceEvidence(
                ipc.HookDiagnostics,
                ipc.ReservedLunaDiagnostics,
                ipc.ReservedHookDiagnostics);
        var isFailure = state.Phase == GalHookSessionPhase.Error;
        var stopped = false;
        var results = new List<GalHookDiagnosticResult>();

        AddGate(
            GalHookDiagnosticBoundary.ProcessFound,
            state.GamePid is > 0,
            state.GamePid is > 0
                ? string.Join(" · ", new[] { $"PID {state.GamePid}", state.Architecture }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty);
        AddGate(
            GalHookDiagnosticBoundary.HelperReady,
            ipc is not null,
            string.Empty);
        AddGate(
            GalHookDiagnosticBoundary.IpcReady,
            ipc?.IsCompatible == true,
            ipc is null
                ? string.Empty
                : $"HVH1 v{ipc.Version} · IPC v{ipc.IpcProtocolVersion}");
        AddGate(
            GalHookDiagnosticBoundary.TextObserved,
            capturedLineCount > 0 || ipc?.TextWriteCount > 0,
            ipc is null
                ? string.Empty
                : Math.Max((ulong)Math.Max(0, capturedLineCount), ipc.TextWriteCount).ToString());
        AddGate(
            GalHookDiagnosticBoundary.TextThreadSelected,
            selectedThreadId is > 0 || ipc?.SelectedTextThreadId > 0,
            selectedThreadId is > 0
                ? $"0x{selectedThreadId.Value:x}"
                : ipc?.SelectedTextThreadId > 0
                    ? $"0x{ipc.SelectedTextThreadId:x}"
                    : string.Empty);

        if (stopped)
        {
            AddNotRun(GalHookDiagnosticBoundary.ResourceObserved);
            AddNotRun(GalHookDiagnosticBoundary.PcmObserved);
            AddNotRun(GalHookDiagnosticBoundary.Paired);
            AddNotRun(GalHookDiagnosticBoundary.LoopbackObserved);
            AddNotRun(GalHookDiagnosticBoundary.CardE2e);
            return results;
        }

        results.Add(new(
            GalHookDiagnosticBoundary.ResourceObserved,
            hasResource
                ? GalHookDiagnosticOutcome.Passed
                : hasPcm || hasLoopback
                    ? GalHookDiagnosticOutcome.NotApplicable
                    : isFailure
                        ? GalHookDiagnosticOutcome.Failed
                        : GalHookDiagnosticOutcome.Pending,
            string.Empty));
        results.Add(new(
            GalHookDiagnosticBoundary.PcmObserved,
            hasPcm
                ? GalHookDiagnosticOutcome.Passed
                : hasLoopback
                    ? GalHookDiagnosticOutcome.NotApplicable
                    : isFailure
                        ? GalHookDiagnosticOutcome.Failed
                        : GalHookDiagnosticOutcome.Pending,
            ipc is null ? string.Empty : $"{ipc.TotalWritten}|{ipc.ClipWriteCount}"));
        results.Add(new(
            GalHookDiagnosticBoundary.Paired,
            hasLoopback && !hasResource && !hasPcm
                ? GalHookDiagnosticOutcome.NotApplicable
                : GalHookDiagnosticOutcome.Unavailable,
            string.Empty));
        results.Add(new(
            GalHookDiagnosticBoundary.LoopbackObserved,
            hasLoopback
                ? GalHookDiagnosticOutcome.Passed
                : hasPcm
                    ? GalHookDiagnosticOutcome.NotApplicable
                    : isFailure
                        ? GalHookDiagnosticOutcome.Failed
                        : GalHookDiagnosticOutcome.Pending,
            ipc is null
                ? string.Empty
                : $"0x{ipc.LoopbackDiagnostics:x}|{ipc.LoopbackTotalWritten}|{ipc.LoopbackMarkerCount}"));
        results.Add(new(
            GalHookDiagnosticBoundary.CardE2e,
            GalHookDiagnosticOutcome.Unavailable,
            string.Empty));
        return results;

        void AddGate(
            GalHookDiagnosticBoundary boundary,
            bool passed,
            string evidence)
        {
            if (stopped)
            {
                AddNotRun(boundary);
                return;
            }

            if (passed)
            {
                results.Add(new(boundary, GalHookDiagnosticOutcome.Passed, evidence));
                return;
            }

            results.Add(new(
                boundary,
                isFailure
                    ? GalHookDiagnosticOutcome.Failed
                    : GalHookDiagnosticOutcome.Pending,
                evidence));
            stopped = true;
        }

        void AddNotRun(GalHookDiagnosticBoundary boundary) =>
            results.Add(new(boundary, GalHookDiagnosticOutcome.NotRun, string.Empty));
    }
}

public static class GalHookDiagnosticBits
{
    // Mirrors native/galgame_hook/include/voice_hook_ipc.h. The companion
    // contract test pins every value used for user-facing diagnosis.
    private static readonly (uint Mask, string Name)[] Primary =
    [
        (0x00000001u, "kDiagStartupAudioHooksReady"),
        (0x00000002u, "kDiagUnityIl2CppHooksReady"),
        (0x00000004u, "kDiagUnityIl2CppClipCaptured"),
        (0x00000008u, "kDiagUnityIl2CppPlaybackObserved"),
        (0x00000010u, "kDiagUnityIl2CppGetDataRejected"),
        (0x00000020u, "kDiagUnityResourceExtractorReady"),
        (0x00000040u, "kDiagUnityResourceExtracted"),
        (0x00000080u, "kDiagUnityResourceExtractFailed"),
        (0x00000100u, "kDiagUnityTmpTextHooksReady"),
        (0x00000200u, "kDiagUnityNaninovelTextHookReady"),
        (0x00000400u, "kDiagLunaHostReady"),
        (0x00000800u, "kDiagLunaConnected"),
        (0x00001000u, "kDiagLunaOutputObserved"),
        (0x00002000u, "kDiagLunaInjectFailed"),
        (0x00004000u, "kDiagSiglusExactTextHookReady"),
        (0x00008000u, "kDiagSiglusExactTextObserved"),
        (0x00010000u, "kDiagFfmpegResourceHooksReady"),
        (0x00020000u, "kDiagFfmpegResourceCaptured"),
        (0x00040000u, "kDiagVisualArtsOvkHooksReady"),
        (0x00080000u, "kDiagVisualArtsOvkCaptured"),
        (0x00100000u, "kDiagKirikiriVorbisOpenHookReady"),
        (0x00200000u, "kDiagFfmpegVoiceResourceObserved"),
        (0x00400000u, "kDiagTyranoAsarHooksReady"),
        (0x00800000u, "kDiagTyranoAsarVoiceCaptured"),
        (0x01000000u, "kDiagBgiArcHooksReady"),
        (0x02000000u, "kDiagBgiArcVoiceCaptured"),
        (0x04000000u, "kDiagArtemisPfsHooksReady"),
        (0x08000000u, "kDiagArtemisPfsVoiceCaptured"),
        (0x10000000u, "kDiagCatSystem2PcmHooksReady"),
        (0x20000000u, "kDiagCatSystem2PcmVoiceCaptured"),
        (0x40000000u, "kDiagMalieLibpHooksReady"),
        (0x80000000u, "kDiagMalieLibpVoiceCaptured"),
    ];

    private static readonly (uint Mask, string Name)[] ReservedLuna =
    [
        (0x00020000u, "kDiagKirikiriVoiceStreamHookReady"),
        (0x00080000u, "kDiagKirikiriVoiceStreamDumped"),
        (0x10000000u, "kDiagSiglusOvkHooksReady"),
    ];

    private static readonly (uint Mask, string Name)[] ReservedHook =
    [
        (0x00000001u, "kDiagMalieArchiveHandleTracked"),
        (0x00000002u, "kDiagMalieReadRangeObserved"),
        (0x00000004u, "kDiagMalieMappingTracked"),
        (0x00000008u, "kDiagMalieMappedRangeObserved"),
        (0x00000010u, "kDiagMalieVoiceRangeQueued"),
        (0x00000020u, "kDiagQlieVorbisHooksReady"),
        (0x00000040u, "kDiagQlieVorbisOpenObserved"),
        (0x00000080u, "kDiagQlieVorbisPcmCaptured"),
        (0x00000100u, "kDiagQlieVorbisFloatHookReady"),
        (0x00000200u, "kDiagQlieVorbisFloatPcmCaptured"),
        (0x00800000u, "kDiagElfAi6ArcHooksReady"),
        (0x01000000u, "kDiagElfAi6ArcVoiceCaptured"),
        (0x02000000u, "kDiagElfAi6ArcHandleTracked"),
        (0x04000000u, "kDiagElfAi6ArcReadObserved"),
        (0x08000000u, "kDiagElfAi6ArcOggObserved"),
        (0x10000000u, "kDiagElfAi6ArcVoiceQueued"),
        (0x20000000u, "kDiagElfAi6ArcTaskRejected"),
    ];

    private const uint PrimaryResourceEvidenceMask =
        0x00000004u | 0x00000040u | 0x00020000u | 0x00080000u
        | 0x00200000u | 0x00800000u | 0x02000000u | 0x08000000u
        | 0x20000000u | 0x80000000u;

    private const uint ReservedLunaResourceEvidenceMask = 0x00080000u;
    private const uint ReservedHookResourceEvidenceMask =
        0x00000080u | 0x00000200u | 0x01000000u;

    public static bool HasResourceEvidence(
        uint primary,
        uint reservedLuna,
        uint reservedHook) =>
        (primary & PrimaryResourceEvidenceMask) != 0
        || (reservedLuna & ReservedLunaResourceEvidenceMask) != 0
        || (reservedHook & ReservedHookResourceEvidenceMask) != 0;

    public static IReadOnlyList<string> Explain(
        uint primary,
        uint reservedLuna,
        uint reservedHook) =>
        Primary
            .Where(flag => (primary & flag.Mask) != 0)
            .Select(flag => flag.Name)
            .Concat(ReservedLuna
                .Where(flag => (reservedLuna & flag.Mask) != 0)
                .Select(flag => flag.Name))
            .Concat(ReservedHook
                .Where(flag => (reservedHook & flag.Mask) != 0)
                .Select(flag => flag.Name))
            .ToArray();
}
