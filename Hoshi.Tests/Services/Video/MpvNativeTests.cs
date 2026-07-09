using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class MpvNativeTests
{
    [Fact]
    public void BuildLoadFileCommandArgs_AddsStartAndPauseOptionsForRestorePosition()
    {
        MpvPlaybackEngine.BuildLoadFileCommandArgs(
                @"D:\Videos\episode.mkv",
                TimeSpan.FromSeconds(629.562))
            .Should()
            .Equal(
                "loadfile",
                @"D:\Videos\episode.mkv",
                "replace",
                "-1",
                "start=629.562,pause=yes");
    }

    [Fact]
    public void BuildLoadFileCommandArgs_OmitsOptionsWithoutRestorePosition()
    {
        MpvPlaybackEngine.BuildLoadFileCommandArgs(@"D:\Videos\episode.mkv", null)
            .Should()
            .Equal("loadfile", @"D:\Videos\episode.mkv", "replace");
    }

    [Fact]
    public void CandidateLibraryPaths_IncludePackagedWindowsLibmpvDirectory()
    {
        MpvNative.GetCandidateLibraryPaths()
            .Should()
            .Contain(path => path.EndsWith(@"libmpv\win-x64\libmpv-2.dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(@"libmpv\win-arm64\libmpv-2.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_CanInitializeBundledLibmpv()
    {
        var handle = MpvNative.Create();
        handle.Should().NotBe(IntPtr.Zero, MpvNative.GetLoadDiagnostic());

        try
        {
            MpvNative.SetOptionStringChecked(handle, "config", "no");
            MpvNative.SetOptionStringChecked(handle, "vo", "null");
            MpvNative.SetOptionStringChecked(handle, "ao", "null");

            var status = MpvNative.Initialize(handle);
            status.Should().BeGreaterThanOrEqualTo(0, MpvNative.ErrorString(status));
        }
        finally
        {
            if (handle != IntPtr.Zero)
                MpvNative.TerminateDestroy(handle);
        }
    }
}
