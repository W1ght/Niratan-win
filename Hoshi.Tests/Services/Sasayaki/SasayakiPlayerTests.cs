using FluentAssertions;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Tests.Services.Sasayaki;

public sealed class SasayakiPlayerTests
{
    [Theory]
    [InlineData(-5, 120, 0)]
    [InlineData(42.5, 120, 42.5)]
    [InlineData(150, 120, 120)]
    [InlineData(42.5, 0, 42.5)]
    public void NormalizeSeekSeconds_ClampsToAvailableMediaRange(
        double requested,
        double duration,
        double expected)
    {
        SasayakiPlayer.NormalizeSeekSeconds(requested, duration).Should().Be(expected);
    }

    [Fact]
    public void NormalizeSeekSeconds_RejectsNonFiniteRequestedValues()
    {
        SasayakiPlayer.NormalizeSeekSeconds(double.NaN, 120).Should().Be(0);
        SasayakiPlayer.NormalizeSeekSeconds(double.PositiveInfinity, 120).Should().Be(0);
        SasayakiPlayer.NormalizeSeekSeconds(double.NegativeInfinity, 120).Should().Be(0);
    }
}
