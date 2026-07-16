using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class VideoSubtitlePositionPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 400)]
    [InlineData(1, 800)]
    public void OriginY_InterpolatesAcrossFullyVisibleTravel(double position, double expected)
    {
        VideoSubtitlePositionPolicy.OriginY(1000, 200, position).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void Normalize_ClampsFiniteValues(double value, double expected)
    {
        VideoSubtitlePositionPolicy.Normalize(value).Should().Be(expected);
    }

    [Fact]
    public void InvalidPosition_UsesDefault()
    {
        VideoSubtitlePositionPolicy.Normalize(double.NaN)
            .Should().Be(VideoSubtitlePositionPolicy.DefaultPosition);
    }

    [Fact]
    public void SubtitleTallerThanViewport_StaysAtTop()
    {
        VideoSubtitlePositionPolicy.OriginY(100, 200, 1).Should().Be(0);
    }

    [Theory]
    [InlineData(0, -70)]
    [InlineData(0.5, 400)]
    [InlineData(1, 870)]
    public void ContainerOriginY_AlignsVisibleContentRatherThanReservedPanel(
        double position,
        double expected)
    {
        VideoSubtitlePositionPolicy.ContainerOriginY(
            viewportHeight: 1000,
            contentTop: 70,
            contentHeight: 60,
            position).Should().Be(expected);
    }

    [Theory]
    [InlineData(-400, 1)]
    [InlineData(-200, 1)]
    [InlineData(-100, 0.95)]
    [InlineData(0, 0.9)]
    [InlineData(100, 0.45)]
    [InlineData(200, 0)]
    [InlineData(400, 0)]
    public void LegacyPosition_MigratesToRelativeFraction(double legacy, double expected)
    {
        VideoSubtitlePositionPolicy.MigrateLegacyPosition(legacy).Should().Be(expected);
    }

    [Fact]
    public void MissingLegacyPosition_UsesDefault()
    {
        VideoSubtitlePositionPolicy.MigrateLegacyPosition(null)
            .Should().Be(VideoSubtitlePositionPolicy.DefaultPosition);
    }
}
