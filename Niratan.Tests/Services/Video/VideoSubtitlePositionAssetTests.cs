using FluentAssertions;

namespace Niratan.Tests.Services.Video;

public sealed class VideoSubtitlePositionAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Niratan"));

    [Fact]
    public void SettingsAndInspector_UseTopBottomRelativeSlidersWithoutNumericValue()
    {
        var settingsXaml = ReadProjectFile("Views", "Pages", "VideoSettingsPage.xaml");
        var playerXaml = ReadProjectFile("Views", "Video", "VideoPlayerWindow.xaml");

        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"VideoSubtitleVerticalPositionSlider\"");
        playerXaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorSubtitleVerticalPositionSlider\"");
        settingsXaml.Should().Contain("Minimum=\"0\"");
        settingsXaml.Should().Contain("Maximum=\"1\"");
        playerXaml.Should().Contain("Minimum=\"0\"");
        playerXaml.Should().Contain("Maximum=\"1\"");
        settingsXaml.Should().Contain("VerticalAlignment=\"Top\"");
        settingsXaml.Should().Contain("VerticalAlignment=\"Bottom\"");
        playerXaml.Should().Contain("VerticalAlignment=\"Top\"");
        playerXaml.Should().Contain("VerticalAlignment=\"Bottom\"");
        settingsXaml.Should().NotContain("SubtitleVerticalPositionText");
        playerXaml.Should().NotContain("SubtitleVerticalPositionText");
    }

    [Fact]
    public void Overlay_UsesMeasuredRelativeTravelInsideMpvPictureViewport()
    {
        var overlayCode = ReadProjectFile("Views", "Video", "VideoPlayerWindow.SubtitleOverlay.cs");
        var playbackCode = ReadProjectFile("Views", "Video", "VideoPlayerWindow.Playback.cs");
        var engineCode = ReadProjectFile("Services", "Video", "MpvPlaybackEngine.cs");
        var playerXaml = ReadProjectFile("Views", "Video", "VideoPlayerWindow.xaml");

        overlayCode.Should().Contain("VideoSubtitlePositionPolicy.OriginY(");
        overlayCode.Should().Contain("geometry.TopMargin");
        overlayCode.Should().Contain("geometry.BottomMargin");
        overlayCode.Should().NotContain("SubtitlePanelTransform.Y = -ViewModel.SubtitleVerticalPosition");
        playbackCode.Should().Contain("GetVideoViewportGeometryAsync()");
        engineCode.Should().Contain("osd-dimensions/mt");
        engineCode.Should().Contain("osd-dimensions/mb");
        playerXaml.Should().Contain("x:Name=\"SubtitlePanelBorder\"");
        playerXaml.Should().NotContain("Margin=\"16,0,16,112\"");
    }

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));
}
