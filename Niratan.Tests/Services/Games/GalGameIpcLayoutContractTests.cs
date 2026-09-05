using FluentAssertions;

namespace Niratan.Tests.Services.Games;

public sealed class GalGameIpcLayoutContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void ManagedReader_IsPinnedToTheNativeV21Layout()
    {
        var nativeHeader = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "native",
            "galgame_hook",
            "include",
            "voice_hook_ipc.h"));
        var managedReader = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Niratan",
            "Services",
            "Games",
            "GalGameIpcReader.cs"));

        nativeHeader.Should().Contain("kSharedVersion = 21");
        managedReader.Should().Contain("SharedHeaderSize = 21560");
        managedReader.Should().Contain("XAudioDiagnosticsOffset = 20");
        managedReader.Should().Contain("XAudioDiagnostics2Offset = 24");
        managedReader.Should().Contain("XAudioDiagnostics = ReadUInt32(view, XAudioDiagnosticsOffset)");
        managedReader.Should().Contain("XAudioDiagnostics2 = ReadUInt32(view, XAudioDiagnostics2Offset)");
        managedReader.Should().Contain("SampleRateOffset = 28");
        managedReader.Should().Contain("LoopbackRingOffset = 21128");
        managedReader.Should().Contain("LookupRegionOffset = 21224");
        managedReader.Should().Contain("LookupGeometryAdmissionModeOffset = 21464");
        managedReader.Should().Contain("LookupGeometryAdmissionRequestSequenceOffset = 21472");
        managedReader.Should().NotContain("SharedHeaderSize = 432");
    }
}
