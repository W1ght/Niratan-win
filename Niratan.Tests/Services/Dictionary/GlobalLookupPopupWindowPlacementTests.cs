using FluentAssertions;
using Niratan.Services.Dictionary;
using Windows.Graphics;

namespace Niratan.Tests.Services.Dictionary;

public sealed class GlobalLookupPopupWindowPlacementTests
{
    [Fact]
    public void ResolveStagingRect_PlacesWindowOutsideWorkArea()
    {
        var rect = GlobalLookupPopupWindowPlacement.ResolveStagingRect(
            new RectInt32(100, 200, 1600, 900),
            new SizeInt32(720, 560));

        (rect.X + rect.Width).Should().BeLessThan(100);
        (rect.Y + rect.Height).Should().BeLessThan(200);
        rect.Width.Should().Be(720);
        rect.Height.Should().Be(560);
    }

    [Fact]
    public void ResolveFinalRect_WhenSpaceAllows_OffsetsFromCursor()
    {
        var rect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            new RectInt32(180, 280, 40, 20),
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(400, 240));

        rect.Should().Be(new RectInt32(0, 312, 400, 240));
    }

    [Fact]
    public void ResolveFinalRect_WhenNearBottomRight_FlipsInsideWorkArea()
    {
        var rect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            new RectInt32(930, 750, 40, 20),
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(400, 240));

        rect.Should().Be(new RectInt32(600, 498, 400, 240));
    }

    [Fact]
    public void ResolveFinalRect_WhenNeitherSideFits_UsesLargerSideAndClipsHeight()
    {
        var rect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            new RectInt32(450, 390, 100, 20),
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(500, 600));

        rect.Should().Be(new RectInt32(250, 422, 500, 378));
    }

    [Theory]
    [InlineData(450, 390, 100, 20, 500, 300)]
    [InlineData(120, 80, 30, 24, 280, 180)]
    [InlineData(850, 680, 60, 24, 360, 220)]
    public void ResolveFinalRect_AlwaysPlacesPopupStrictlyAboveOrBelowSelection(
        int anchorX,
        int anchorY,
        int anchorWidth,
        int anchorHeight,
        int popupWidth,
        int popupHeight)
    {
        var anchor = new RectInt32(anchorX, anchorY, anchorWidth, anchorHeight);
        var rect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            anchor,
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(popupWidth, popupHeight));

        var isAbove = rect.Y + rect.Height <= anchor.Y - GlobalLookupPopupWindowPlacement.PopupGap;
        var isBelow = rect.Y >= anchor.Y + anchor.Height + GlobalLookupPopupWindowPlacement.PopupGap;
        (isAbove || isBelow).Should().BeTrue();
    }

}
