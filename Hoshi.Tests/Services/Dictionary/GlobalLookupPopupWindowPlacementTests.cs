using FluentAssertions;
using Hoshi.Services.Dictionary;
using Windows.Graphics;

namespace Hoshi.Tests.Services.Dictionary;

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
            new PointInt32(200, 300),
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(400, 240));

        rect.Should().Be(new RectInt32(216, 316, 400, 240));
    }

    [Fact]
    public void ResolveFinalRect_WhenNearBottomRight_FlipsInsideWorkArea()
    {
        var rect = GlobalLookupPopupWindowPlacement.ResolveFinalRect(
            new PointInt32(950, 760),
            new RectInt32(0, 0, 1000, 800),
            new SizeInt32(400, 240));

        rect.Should().Be(new RectInt32(534, 504, 400, 240));
    }
}
