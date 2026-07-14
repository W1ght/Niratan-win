using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class DictionaryPopupAppearanceConstraintsTests
{
    [Fact]
    public void Defaults_MatchNiratan()
    {
        var settings = new DictionaryDisplaySettings();

        DictionaryPopupAppearanceConstraints.MinWidth.Should().Be(100);
        DictionaryPopupAppearanceConstraints.MaxWidth.Should().Be(1400);
        DictionaryPopupAppearanceConstraints.WidthStep.Should().Be(10);
        DictionaryPopupAppearanceConstraints.MinHeight.Should().Be(100);
        DictionaryPopupAppearanceConstraints.MaxHeight.Should().Be(800);
        DictionaryPopupAppearanceConstraints.HeightStep.Should().Be(10);
        DictionaryPopupAppearanceConstraints.MinScale.Should().Be(0.8);
        DictionaryPopupAppearanceConstraints.MaxScale.Should().Be(1.5);
        DictionaryPopupAppearanceConstraints.ScaleStep.Should().Be(0.05);
        settings.PopupMaxWidth.Should().Be(320);
        settings.PopupMaxHeight.Should().Be(250);
        settings.PopupScale.Should().Be(1.0);
        settings.PopupActionBar.Should().BeFalse();
        settings.PopupFullWidth.Should().BeFalse();
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(320, 320)]
    [InlineData(1600, 1400)]
    public void NormalizeWidth_ClampsToNiratanRange(int value, int expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeWidth(value).Should().Be(expected);

    [Theory]
    [InlineData(50, 100)]
    [InlineData(250, 250)]
    [InlineData(820, 800)]
    public void NormalizeHeight_ClampsToNiratanRange(int value, int expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeHeight(value).Should().Be(expected);

    [Theory]
    [InlineData(0.5, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.5)]
    public void NormalizeScale_ClampsToNiratanRange(double value, double expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeScale(value).Should().Be(expected);

    [Fact]
    public void NormalizeScale_NonFiniteValueUsesDefault()
    {
        DictionaryPopupAppearanceConstraints.NormalizeScale(double.NaN).Should().Be(1.0);
        DictionaryPopupAppearanceConstraints.NormalizeScale(double.PositiveInfinity).Should().Be(1.0);
    }
}
