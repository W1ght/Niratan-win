using FluentAssertions;
using Niratan.Models.Games;

namespace Niratan.Tests.Models.Games;

public sealed class GalHookDiagnosticFunctionsTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Evaluate_StopsAfterFirstUnobservedBoundary()
    {
        var results = GalHookDiagnosticFunctions.Evaluate(
            new GalHookSessionState(),
            capturedLineCount: 0,
            selectedThreadId: null);

        results.Should().HaveCount(10);
        results[0].Outcome.Should().Be(GalHookDiagnosticOutcome.Pending);
        results.Skip(1).Should().OnlyContain(result =>
            result.Outcome == GalHookDiagnosticOutcome.NotRun);
    }

    [Fact]
    public void Evaluate_DoesNotPromoteCapturedPcmToStablePairing()
    {
        var state = new GalHookSessionState
        {
            Phase = GalHookSessionPhase.Running,
            GamePid = 42,
            Architecture = "x86",
            Ipc = Snapshot(
                hookDiagnostics: 0x00000004u,
                textWriteCount: 3,
                clipWriteCount: 1,
                totalWritten: 4096,
                selectedThreadId: 0x1234),
        };

        var results = GalHookDiagnosticFunctions.Evaluate(
            state,
            capturedLineCount: 3,
            selectedThreadId: 0x1234);

        results.Take(7).Should().OnlyContain(result =>
            result.Outcome == GalHookDiagnosticOutcome.Passed);
        results[7].Outcome.Should().Be(GalHookDiagnosticOutcome.Unavailable);
        results[8].Outcome.Should().Be(GalHookDiagnosticOutcome.NotApplicable);
        results[9].Outcome.Should().Be(GalHookDiagnosticOutcome.Unavailable);
    }

    [Fact]
    public void Evaluate_RecognizesLiveLoopbackAsTheAudioFallbackBoundary()
    {
        var state = new GalHookSessionState
        {
            Phase = GalHookSessionPhase.Running,
            GamePid = 42,
            Architecture = "x86",
            Ipc = Snapshot(
                hookDiagnostics: 0,
                textWriteCount: 3,
                clipWriteCount: 0,
                totalWritten: 0,
                selectedThreadId: 0x1234,
                loopbackTotalWritten: 4096,
                loopbackMarkerCount: 4),
        };

        var results = GalHookDiagnosticFunctions.Evaluate(state, 3, 0x1234);

        results[5].Outcome.Should().Be(GalHookDiagnosticOutcome.NotApplicable);
        results[6].Outcome.Should().Be(GalHookDiagnosticOutcome.NotApplicable);
        results[7].Outcome.Should().Be(GalHookDiagnosticOutcome.NotApplicable);
        results[8].Outcome.Should().Be(GalHookDiagnosticOutcome.Passed);
        results[9].Outcome.Should().Be(GalHookDiagnosticOutcome.Unavailable);
    }

    [Fact]
    public void SymbolicFlags_ArePinnedToNativeHeaderValues()
    {
        var header = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "native",
            "galgame_hook",
            "include",
            "voice_hook_ipc.h"));

        header.Should().Contain("kDiagUnityIl2CppClipCaptured = 0x00000004u");
        header.Should().Contain("kDiagKirikiriVoiceStreamDumped = 0x00080000u");
        GalHookDiagnosticBits.Explain(0x00000004u, 0x00080000u, 0x00000080u)
            .Should().Equal(
                "kDiagUnityIl2CppClipCaptured",
                "kDiagKirikiriVoiceStreamDumped",
                "kDiagQlieVorbisPcmCaptured");
        GalHookDiagnosticBits.HasResourceEvidence(0x00000004u, 0, 0).Should().BeTrue();
        GalHookDiagnosticBits.HasResourceEvidence(0, 0x00080000u, 0).Should().BeTrue();
        GalHookDiagnosticBits.HasResourceEvidence(0, 0, 0x00000080u).Should().BeTrue();
    }

    private static GalGameIpcSnapshot Snapshot(
        uint hookDiagnostics,
        ulong textWriteCount,
        ulong clipWriteCount,
        ulong totalWritten,
        ulong selectedThreadId,
        ulong loopbackTotalWritten = 0,
        ulong loopbackMarkerCount = 0) => new()
    {
        ProcessId = 42,
        Magic = GalGameIpcSnapshot.SharedMagic,
        Version = GalGameIpcSnapshot.SharedVersion,
        IpcProtocolVersion = GalGameIpcSnapshot.StableIpcVersion,
        SampleRate = 48000,
        Channels = 2,
        BitsPerSample = 16,
        RingCapacity = 8192,
        Hooked = 1,
        TextHooked = 1,
        LunaActive = 1,
        HookDiagnostics = hookDiagnostics,
        TotalWritten = totalWritten,
        TextWriteCount = textWriteCount,
        ClipWriteCount = clipWriteCount,
        SelectedTextThreadId = selectedThreadId,
        LoopbackRingOffset = 1024,
        LoopbackRingCapacity = 8192,
        LoopbackSampleRate = 48000,
        LoopbackChannels = 2,
        LoopbackBitsPerSample = 16,
        LoopbackDiagnostics = loopbackTotalWritten > 0 ? 0x0fu : 0,
        LoopbackTotalWritten = loopbackTotalWritten,
        LoopbackMarkerCount = loopbackMarkerCount,
    };
}
