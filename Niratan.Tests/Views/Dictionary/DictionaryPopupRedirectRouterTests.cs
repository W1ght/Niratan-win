using FluentAssertions;
using Niratan.Views.Dictionary;

namespace Niratan.Tests.Views.Dictionary;

public sealed class DictionaryPopupRedirectRouterTests
{
    [Fact]
    public void StructuredLinkWithoutSelectionCoordinatesRedirectsInPlace()
    {
        DictionaryPopupRedirectRouter.Resolve(new DictionaryPopupRedirectRequest("語"))
            .Should().Be(DictionaryPopupRedirectMode.InPlace);
    }

    [Fact]
    public void SelectedPopupTextCreatesNestedPopup()
    {
        DictionaryPopupRedirectRouter.Resolve(new DictionaryPopupRedirectRequest(
                "語", X: 20, Y: 30, Width: 10, Height: 14, Source: "click"))
            .Should().Be(DictionaryPopupRedirectMode.Nested);
    }
}
