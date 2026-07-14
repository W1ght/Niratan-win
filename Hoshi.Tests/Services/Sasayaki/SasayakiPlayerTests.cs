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

    [Fact]
    public void SeekLandingState_KeepsTargetAndRejectsStalePlayerSamplesUntilSeekLands()
    {
        var state = new SasayakiSeekLandingState();

        state.Request(25373.1);

        state.ResolvePosition(0).Should().Be(25373.1);
        state.TryAcceptPosition(24060.1).Should().BeFalse();
        state.PendingSeconds.Should().Be(25373.1);
        state.ResolvePosition(24060.1).Should().Be(25373.1);
    }

    [Fact]
    public void SeekLandingState_AcceptsTargetSampleAndClearsPendingSeek()
    {
        var state = new SasayakiSeekLandingState();
        state.Request(25373.1);

        state.TryAcceptPosition(25373.7).Should().BeTrue();

        state.PendingSeconds.Should().BeNull();
        state.ResolvePosition(25373.7).Should().Be(25373.7);
    }

    [Fact]
    public void SeekLandingState_NewerRequestSupersedesEarlierSeek()
    {
        var state = new SasayakiSeekLandingState();
        state.Request(25373.1);
        state.Request(21531.8);

        state.TryAcceptPosition(25373.1).Should().BeFalse();
        state.PendingSeconds.Should().Be(21531.8);
        state.ResolvePosition(25373.1).Should().Be(21531.8);
    }
}
