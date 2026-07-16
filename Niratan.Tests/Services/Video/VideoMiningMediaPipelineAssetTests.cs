using FluentAssertions;

namespace Niratan.Tests.Services.Video;

public class VideoMiningMediaPipelineAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void VideoLookup_DoesNotCaptureMiningMediaBeforePopupMiningPreflight()
    {
        var windowCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));
        var popupCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs"));

        var lookupStart = windowCode.IndexOf("private async Task LookupCurrentSubtitleAsync", StringComparison.Ordinal);
        var lookupEnd = windowCode.IndexOf("private DictionaryPopupOverlay EnsurePopupOverlay", StringComparison.Ordinal);
        lookupStart.Should().BeGreaterThanOrEqualTo(0);
        lookupEnd.Should().BeGreaterThan(lookupStart);
        var lookupBody = windowCode[lookupStart..lookupEnd];

        lookupBody.Should().NotContain("CaptureMiningMediaAsync()");
        lookupBody.Should().Contain("CreateLookupRequestAsync");

        popupCode.Should().Contain("PreflightMiningAsync");
        popupCode.Should().Contain("RequestVideoMiningMediaAsync");
        popupCode.Should().Contain("RequestSasayakiMiningMediaAsync");
    }

    [Fact]
    public void DirectMediaGeneration_IsAwaitedAndFailuresBlockMining()
    {
        var windowCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));
        var popupCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs"));

        windowCode.Should().Contain("return await GenerateDirectVideoMiningMediaAsync(");
        windowCode.Should().NotContain("_ = GenerateDirectVideoMiningMediaAsync(");
        windowCode.Should().Contain("ScreenshotErrorMessage: screenshotError");
        popupCode.Should().Contain("result.ScreenshotErrorMessage ?? \"Unable to capture the video screenshot.\"");
    }

    [Fact]
    public void StandaloneScreenshot_WaitsForDecodedFrame()
    {
        var extractorCode = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Services",
            "Video",
            "LibMpvVideoMiningMediaExtractor.cs"));

        extractorCode.Should().Contain("MpvEventIdPlaybackRestart = 21");
        extractorCode.Should().Contain("mpvEvent.EventId == MpvEventIdPlaybackRestart");
        extractorCode.Should().Contain("SetOptionStringChecked(handle, \"vo\", \"null\")");
        extractorCode.Should().Contain("SetOptionStringChecked(handle, \"screenshot-sw\", \"yes\")");
        extractorCode.Should().NotContain("SetOptionStringChecked(handle, \"pause\", \"yes\")");
    }
}
