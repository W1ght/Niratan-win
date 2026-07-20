using FluentAssertions;
using Niratan.Enums;
using Niratan.Models.Settings;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelReaderContentStylesTests
{
    [Theory]
    [InlineData(false, "horizontal-tb")]
    [InlineData(true, "vertical-rl")]
    public void GenerateCss_OverridesRootAndTtuWrapperWritingMode(
        bool verticalWriting,
        string expectedWritingMode)
    {
        var settings = new ReaderSettings
        {
            VerticalWriting = verticalWriting,
        };

        var css = NovelReaderContentStyles.GenerateCss(settings, ThemeMode.Light);

        css.Should().Contain("html, body, .ttu-book-html-wrapper");
        css.Should().Contain($"writing-mode: {expectedWritingMode} !important;");
        css.Should().Contain($"-webkit-writing-mode: {expectedWritingMode} !important;");
    }

    [Fact]
    public void GenerateCss_ProvidesReaderImageBlurPresentation()
    {
        var css = NovelReaderContentStyles.GenerateCss(
            new ReaderSettings(),
            ThemeMode.Light);

        css.Should().Contain(".niratan-blur-wrapper");
        css.Should().Contain("img.block-img.niratan-blurred");
        css.Should().Contain("svg.niratan-blurred");
        css.Should().Contain("filter: blur(24px) !important");
    }

    [Fact]
    public void GenerateCss_AddsTwoColumnSpreadAndParagraphSpacing()
    {
        var css = NovelReaderContentStyles.GenerateCss(
            new ReaderSettings
            {
                VerticalWriting = false,
                ContinuousMode = false,
                TwoColumnHorizontalPages = true,
                LayoutAdvanced = true,
                ParagraphSpacing = 1.2,
            },
            ThemeMode.Light);

        css.Should().Contain("column-count: 2 !important");
        css.Should().Contain("--reader-column-gap: 32px");
        css.Should().Contain("margin-top: 1.2em !important");
        css.Should().Contain("margin-bottom: 1.2em !important");
    }

    [Fact]
    public void GenerateCss_DeclaresImportedFontFromControlledHost()
    {
        var css = NovelReaderContentStyles.GenerateCss(
            new ReaderSettings
            {
                SelectedFont = "'NiratanImportedABC', serif",
                SelectedFontFileName = "book.otf",
            },
            ThemeMode.Light);

        css.Should().Contain("@font-face");
        css.Should().Contain("https://niratan-reader-fonts.local/book.otf");
    }
}
