using FluentAssertions;
using Niratan.Models.Anki;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

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
                SasayakiAudioTag = "[sound:niratan_sasayaki_clip.m4a]",
            });

        rendered.Should().Be("[sound:niratan_sasayaki_clip.m4a]");
    }

    [Fact]
    public void Renderer_UsesUploadedCoverTagInsteadOfLocalPath()
    {
        var rendered = AnkiHandlebarRenderer.Render(
            "{book-cover}",
            new AnkiMiningPayload(),
            new AnkiMiningContext
            {
                CoverPath = "D:\\Books\\Private\\cover.jpg",
                CoverTag = "<img src=\"niratan_cover.jpg\">",
            });

        rendered.Should().Be("<img src=\"niratan_cover.jpg\">");
    }

    [Fact]
    public void Renderer_ResolvesUnifiedMediaPlaceholdersFromVideoContext()
    {
        var context = new AnkiMiningContext
        {
            VideoFileName = "episode.mkv",
            VideoScreenshotTag = "<img src=\"frame.webp\">",
            VideoAudioClipTag = "[sound:clip.m4a]",
        };

        AnkiHandlebarRenderer.Render("{book-cover}", new AnkiMiningPayload(), context)
            .Should().Be("<img src=\"frame.webp\">");
        AnkiHandlebarRenderer.Render("{sasayaki-audio}", new AnkiMiningPayload(), context)
            .Should().Be("[sound:clip.m4a]");
    }
}
