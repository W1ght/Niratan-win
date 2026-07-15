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
}
