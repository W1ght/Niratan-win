using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public sealed class DictionaryPopupScaleCssTests
{
    [Fact]
    public void BuildDeclarations_UsesInvariantNiratanDimensions()
    {
        var css = DictionaryPopupScaleCss.BuildDeclarations(1.25);

        css.Should().Contain("--popup-scale:1.25;");
        css.Should().Contain("--popup-root-font-size:20px;");
        css.Should().Contain("--popup-body-font-size:18.75px;");
        css.Should().Contain("--popup-expression-font-size:32.5px;");
        css.Should().NotContain(",");
    }

    [Fact]
    public void ScaleCustomCss_RewritesSignedAndDecimalPixelLengths()
    {
        DictionaryPopupScaleCss.ScaleCustomCss(".x{margin:-2.5px;padding:8px}")
            .Should()
            .Be(".x{margin:calc(-2.5px * var(--popup-scale));padding:calc(8px * var(--popup-scale))}");
    }
}
