using FluentAssertions;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class ReaderLyricsAdaptiveFontPolicyTests
{
    [Fact]
    public void HorizontalLineThatFits_KeepsBaseFontSize()
    {
        ReaderLyricsAdaptiveFontPolicy.FitHorizontal(
                baseFontSize: 48,
                measuredTextWidth: 400,
                availableWidth: 480)
            .Should().Be(48);
    }

    [Fact]
    public void HorizontalLongLine_ShrinksIntoPaddedWidthWithoutLegacyFloor()
    {
        var fitted = ReaderLyricsAdaptiveFontPolicy.FitHorizontal(
            baseFontSize: 48,
            measuredTextWidth: 960,
            availableWidth: 480);

        fitted.Should().BeApproximately(22.8, 0.001);
        fitted.Should().BeLessThan(24);
    }

    [Fact]
    public void HorizontalVeryLongLine_CanShrinkBelowTwelveToAvoidClipping()
    {
        ReaderLyricsAdaptiveFontPolicy.FitHorizontal(
                baseFontSize: 48,
                measuredTextWidth: 4800,
                availableWidth: 480)
            .Should().BeApproximately(4.56, 0.001);
    }

    [Fact]
    public void VerticalLongLine_UsesOnlyNinetyPercentOfAvailableHeight()
    {
        var fitted = ReaderLyricsAdaptiveFontPolicy.FitVertical(
            baseFontSize: 44,
            glyphCount: 20,
            availableHeight: 600,
            availableColumnWidth: 100);

        fitted.Should().BeApproximately(25, 0.001);
        (20 * fitted * ReaderLyricsAdaptiveFontPolicy.VerticalRowHeightRatio)
            .Should().BeApproximately(540, 0.001);
    }

    [Fact]
    public void VerticalNarrowColumn_AlsoConstrainsFontByWidth()
    {
        var fitted = ReaderLyricsAdaptiveFontPolicy.FitVertical(
            baseFontSize: 44,
            glyphCount: 5,
            availableHeight: 600,
            availableColumnWidth: 35);

        (fitted * ReaderLyricsAdaptiveFontPolicy.VerticalColumnWidthRatio)
            .Should().BeApproximately(35, 0.001);
    }

    [Fact]
    public void InvalidViewportValues_ProduceFiniteRenderableFontSize()
    {
        ReaderLyricsAdaptiveFontPolicy.FitHorizontal(48, double.NaN, double.NaN)
            .Should().Be(48);
        ReaderLyricsAdaptiveFontPolicy.FitVertical(44, 20, double.NaN, double.PositiveInfinity)
            .Should().Be(1);
    }
}
