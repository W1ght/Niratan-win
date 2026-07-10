using FluentAssertions;
using Hoshi.Services.Video;
using Windows.Foundation;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleCanvasRendererTests
{
    [Theory]
    [InlineData(800, 28, 744)]
    [InlineData(2000, 160, 1680)]
    public void CalculateLayoutBounds_MatchesHiddenSelectionBridgeWidth(
        double canvasWidth,
        double expectedX,
        double expectedWidth)
    {
        var bounds = VideoSubtitleCanvasRenderer.CalculateLayoutBounds(
            new Size(canvasWidth, 240));

        bounds.Should().Be(new Rect(expectedX, 0, expectedWidth, 240));
    }
}
