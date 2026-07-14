using FluentAssertions;
using Niratan.Models.Anki;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class AnkiFieldMappingResolverTests
{
    [Fact]
    public void ResolveForMining_UsesAnimeLapisDefaultsWhenSavedMappingsAreEmptyForVideoContext()
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
        mappings["SentenceAudio"].Should().Be("{video-audio-clip}");
        mappings["Picture"].Should().Be("{video-screenshot}");
        mappings["MiscInfo"].Should().Be("{video-file-name} ({video-timestamp})");
        mappings["IsWordAndSentenceCard"].Should().Be("x");
    }
}
