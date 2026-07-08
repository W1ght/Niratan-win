using FluentAssertions;
using Hoshi.Models.Anki;
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

    [Fact]
    public void Renderer_UsesSasayakiSoundTagWhenAvailable()
    {
        var rendered = AnkiHandlebarRenderer.Render(
            "{sasayaki-audio}",
            new AnkiMiningPayload(),
            new AnkiMiningContext
            {
                SasayakiAudioPath = "D:\\Temp\\clip.m4a",
                SasayakiAudioTag = "[sound:hoshi_sasayaki_clip.m4a]",
            });

        rendered.Should().Be("[sound:hoshi_sasayaki_clip.m4a]");
    }
}
