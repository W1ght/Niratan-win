using FluentAssertions;
using Hoshi.Views.Dictionary;

namespace Hoshi.Tests.Views.Dictionary;

public sealed class DictionaryPopupLayoutCalculatorTests
{
    [Fact]
    public void HorizontalLayout_WhenBelowSelection_TouchesSelectionBottomWithMacPadding()
    {
        var layout = DictionaryPopupLayoutCalculator.Resolve(
            new DictionaryPopupAnchorRect(x: 480, y: 120, width: 42, height: 18),
            screenWidth: 1000,
            screenHeight: 800,
            maxWidth: 500,
            maxHeight: 240,
            minWidth: 360,
            isVertical: false);

        layout.Left.Should().Be(251);
        layout.Top.Should().Be(142);
        layout.Height.Should().Be(240);
        (layout.Left + layout.Width / 2).Should().Be(501);
    }

    [Fact]
    public void HorizontalLayout_WhenAboveSelection_TouchesSelectionTopWithMacPadding()
    {
        var layout = DictionaryPopupLayoutCalculator.Resolve(
            new DictionaryPopupAnchorRect(x: 260, y: 700, width: 42, height: 18),
            screenWidth: 1000,
            screenHeight: 800,
            maxWidth: 500,
            maxHeight: 240,
            minWidth: 360,
            isVertical: false);

        layout.Left.Should().Be(31);
        layout.Top.Should().Be(456);
        (layout.Top + layout.Height).Should().Be(696);
        (layout.Left + layout.Width / 2).Should().Be(281);
    }
}
