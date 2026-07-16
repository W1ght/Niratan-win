using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoWindowAspectLayoutTests
{
    [Theory]
    [InlineData(1920, 1080, 0, 16d / 9d)]
    [InlineData(720, 1280, 90, 16d / 9d)]
    [InlineData(1920, 1080, 270, 9d / 16d)]
    public void VideoDisplayInfo_ResolvesDisplayAspectRatio(
        int width,
        int height,
        int rotation,
        double expected)
    {
        new VideoDisplayInfo(width, height, rotation).AspectRatio
            .Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void FitContentSize_PreservesVideoAspectAndAddsSidebar()
    {
        var result = VideoWindowAspectLayout.FitContentSize(
            new VideoLayoutSize(1100, 720),
            16d / 9d,
            sidebarWidth: 380,
            new VideoLayoutSize(1920, 1080));

        result.Height.Should().Be(720);
        (result.Width - 380).Should().BeApproximately(720 * (16d / 9d), 0.001);
    }

    [Fact]
    public void FitContentSize_ShrinksHeightWhenScreenWidthIsLimited()
    {
        var result = VideoWindowAspectLayout.FitContentSize(
            new VideoLayoutSize(1500, 900),
            16d / 9d,
            sidebarWidth: 300,
            new VideoLayoutSize(1400, 1000));

        result.Width.Should().BeApproximately(1400, 0.001);
        ((result.Width - 300) / result.Height).Should().BeApproximately(16d / 9d, 0.001);
    }

    [Fact]
    public void ConstrainFrameSize_KeepsVideoAreaAtSourceAspect()
    {
        var result = VideoWindowAspectLayout.ConstrainFrameSize(
            currentFrameSize: new VideoLayoutSize(1300, 760),
            proposedFrameSize: new VideoLayoutSize(1500, 760),
            frameDecorationSize: new VideoLayoutSize(16, 40),
            videoAspectRatio: 16d / 9d,
            sidebarWidth: 0,
            minimumFrameSize: new VideoLayoutSize(336, 220));

        (result.Width - 16).Should().BeApproximately((result.Height - 40) * (16d / 9d), 0.001);
    }
}
