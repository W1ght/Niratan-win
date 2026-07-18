using FluentAssertions;
using Niratan.Models.Anki;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class AnkiFieldMappingResolverTests
{
    [Fact]
    public void ResolveForMining_UsesUnifiedLapisDefaultsInVideoContext()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Lapis",
            Fields =
            [
                "Expression",
                "MainDefinition",
                "Sentence",
                "SentenceAudio",
                "Picture",
                "MiscInfo",
                "IsWordAndSentenceCard",
            ],
        };
        var context = new AnkiMiningContext
        {
            VideoFileName = "episode-01.mkv",
            VideoScreenshotPath = "D:\\Media\\shot.webp",
            VideoAudioClipPath = "D:\\Media\\clip.mp3",
        };

        var mappings = AnkiFieldMappingResolver.ResolveForMining(
            noteType,
            new Dictionary<string, string>(),
            context);

        mappings["Expression"].Should().Be("{expression}");
        mappings["MainDefinition"].Should().Be("{glossary-first}");
        mappings["Sentence"].Should().Be("{sentence}");
        mappings["SentenceAudio"].Should().Be("{sasayaki-audio}");
        mappings["Picture"].Should().Be("{book-cover}");
        mappings["MiscInfo"].Should().Be("{document-title}");
        mappings["IsWordAndSentenceCard"].Should().Be("x");
    }

    [Fact]
    public void ResolveForMining_DoesNotRewriteSavedMappingsForVideoContext()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Lapis",
            Fields = ["Expression", "SentenceAudio", "Picture", "MiscInfo"],
        };
        var context = new AnkiMiningContext { VideoFileName = "episode-01.mkv" };

        var mappings = AnkiFieldMappingResolver.ResolveForMining(
            noteType,
            new Dictionary<string, string>
            {
                ["Expression"] = "{custom-expression}",
                ["SentenceAudio"] = "{sasayaki-audio}",
                ["Picture"] = "{book-cover}",
                ["MiscInfo"] = "{document-title}",
            },
            context);

        mappings["Expression"].Should().Be("{custom-expression}");
        mappings["SentenceAudio"].Should().Be("{sasayaki-audio}");
        mappings["Picture"].Should().Be("{book-cover}");
        mappings["MiscInfo"].Should().Be("{document-title}");
    }

    [Fact]
    public void ResolveForMining_PreservesLegacyExplicitVideoMappingsInNovelContext()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Lapis",
            Fields = ["SentenceAudio", "Picture", "MiscInfo"],
        };

        var mappings = AnkiFieldMappingResolver.ResolveForMining(
            noteType,
            new Dictionary<string, string>
            {
                ["SentenceAudio"] = "{video-audio-clip}",
                ["Picture"] = "{video-screenshot}",
                ["MiscInfo"] = "{video-file-name} ({video-timestamp})",
            },
            new AnkiMiningContext { CoverPath = "D:\\Books\\cover.jpg" });

        mappings["SentenceAudio"].Should().Be("{video-audio-clip}");
        mappings["Picture"].Should().Be("{video-screenshot}");
        mappings["MiscInfo"].Should().Be("{video-file-name} ({video-timestamp})");
    }
}
