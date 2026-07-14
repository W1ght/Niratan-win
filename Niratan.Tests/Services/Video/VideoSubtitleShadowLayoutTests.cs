using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSubtitleShadowLayoutTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 3, 0.9)]
    [InlineData(10, 10, 0.9)]
    [InlineData(99, 10, 0.9)]
    public void Create_ProducesOneClampedNiratanShadow(
        double radius,
        float expectedRadius,
        float expectedOpacity)
    {
        var style = VideoSubtitleShadowLayout.Create(radius, 1);

        style.BlurRadius.Should().Be(expectedRadius);
        style.OffsetX.Should().Be(0);
        style.OffsetY.Should().Be(radius <= 0 ? 0 : 1);
        style.Opacity.Should().Be(expectedOpacity);
    }
}
