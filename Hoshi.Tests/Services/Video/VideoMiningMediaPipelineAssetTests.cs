using FluentAssertions;

namespace Hoshi.Tests.Services.Video;

public class VideoMiningMediaPipelineAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

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
    }
}
