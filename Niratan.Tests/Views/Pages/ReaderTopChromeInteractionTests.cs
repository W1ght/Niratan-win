using FluentAssertions;
using Niratan.Views.Pages;

namespace Niratan.Tests.Views.Pages;

public sealed class ReaderTopChromeInteractionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void HiddenChrome_TopBlankClickOpens(double y)
    {
        ReaderTopChromeInteraction.ResolveBlankClick(false, false, false, y)
            .Should().Be(ReaderTopChromeClickAction.Open);
    }

    [Fact]
    public void HiddenChrome_BodyBlankClickDoesNothing()
    {
        ReaderTopChromeInteraction.ResolveBlankClick(false, false, false, 64.01)
            .Should().Be(ReaderTopChromeClickAction.None);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(500)]
    public void OpenChrome_AnyBlankClickCloses(double y)
    {
        ReaderTopChromeInteraction.ResolveBlankClick(true, false, false, y)
            .Should().Be(ReaderTopChromeClickAction.Close);
    }

    [Fact]
    public void PopupBlankClick_ClosesOpenChromeButDoesNotOpenHiddenChrome()
    {
        ReaderTopChromeInteraction.ResolveBlankClick(true, false, true, 20)
            .Should().Be(ReaderTopChromeClickAction.Close);
        ReaderTopChromeInteraction.ResolveBlankClick(false, false, true, 20)
            .Should().Be(ReaderTopChromeClickAction.None);
    }

    [Fact]
    public void FocusMode_NeverOpensChrome()
    {
        ReaderTopChromeInteraction.ResolveBlankClick(false, true, false, 20)
            .Should().Be(ReaderTopChromeClickAction.None);
    }
}
