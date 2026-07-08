using FluentAssertions;
using Hoshi.Services.Anki;

namespace Hoshi.Tests.Services.Anki;

public class AnkiMediaMarkupTests
{
    [Fact]
    public void ForFieldPlaceholder_RendersCompleteImageOrAudioMarkup()
    {
        AnkiMediaMarkup.ForFieldPlaceholder("frame.webp")
            .Should()
            .Be("<img src=\"frame.webp\">");

        AnkiMediaMarkup.ForFieldPlaceholder("clip.m4a")
            .Should()
            .Be("[sound:clip.m4a]");
    }

    [Fact]
    public void ForDictionaryHtmlReference_ReturnsFilenameForExistingImgSrc()
    {
        AnkiMediaMarkup.ForDictionaryHtmlReference("gaiji.svg")
            .Should()
            .Be("gaiji.svg");
    }
}
