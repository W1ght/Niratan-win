using FluentAssertions;

namespace Niratan.Tests.Views.Video;

public sealed class VideoPlayerPlaylistChromeAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void PlayerChrome_ExposesPreviousAndNextEpisodeButtonsWithoutTouchingEngineBoundary()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.Playback.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoPlayerPreviousEpisodeButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoPlayerNextEpisodeButton\"");
        code.Should().Contain("await OpenAdjacentEpisodeAsync(-1);");
        code.Should().Contain("await OpenAdjacentEpisodeAsync(1);");
        code.Should().NotContain("MpvNative.");
    }
}
