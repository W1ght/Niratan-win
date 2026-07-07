using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleShadowLayoutTests
{
    [Fact]
    public void CreateOffsets_DefaultAsbplayerShadowProducesVisibleSymmetricHalo()
    {
        var offsets = VideoSubtitleShadowLayout.CreateOffsets(3, 1);

        offsets.Should().HaveCount(VideoSubtitleShadowLayout.LayerCount);
        offsets.Should().OnlyContain(offset => offset.Opacity == 0.9);
        offsets.Should().Contain(offset => offset.X < 0 && offset.Y == 0);
        offsets.Should().Contain(offset => offset.X > 0 && offset.Y == 0);
        offsets.Should().Contain(offset => offset.X == 0 && offset.Y < 0);
        offsets.Should().Contain(offset => offset.X == 0 && offset.Y > 0);
        offsets.Should().Contain(offset => offset.X < 0 && offset.Y < 0);
        offsets.Should().Contain(offset => offset.X > 0 && offset.Y > 0);
        offsets.Should().NotContain(offset => offset.X == 0 && offset.Y == 0);
    }

    [Fact]
    public void CreateOffsets_MultipliesNiratanShadowOpacityBySubtitleMaskOpacity()
    {
        var offsets = VideoSubtitleShadowLayout.CreateOffsets(3, 0.5);

        offsets.Should().OnlyContain(offset => offset.Opacity == 0.45);
    }

    [Fact]
    public void CreateOffsets_ZeroShadowHidesAllLayers()
    {
        var offsets = VideoSubtitleShadowLayout.CreateOffsets(0, 1);

        offsets.Should().HaveCount(VideoSubtitleShadowLayout.LayerCount);
        offsets.Should().OnlyContain(offset => offset.Opacity == 0);
        offsets.Should().OnlyContain(offset => offset.X == 0 && offset.Y == 0);
    }

}
