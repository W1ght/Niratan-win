using FluentAssertions;
using Niratan.Models.Anki;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class AnkiMiningPreflightTests
{
    [Fact]
    public void ResolveMediaNeedsForMining_UsesRenderedFieldMappingsBeforeVideoMediaCapture()
    {
        var noteType = new AnkiNoteType
        {
            Id = 1,
            Name = "Anime",
            Fields = ["Sentence", "Screenshot", "Audio"],
        };
        var mappings = new Dictionary<string, string>
        {
            ["Sentence"] = "{video-subtitle}",
            ["Screenshot"] = "{video-screenshot}",
            ["Audio"] = "{video-audio-clip}",
        };

        var needs = AnkiFieldMappingResolver.ResolveMediaNeedsForMining(
            noteType,
            mappings,
            new AnkiMiningContext { VideoFileName = "Episode.mkv" });

        needs.NeedsVideoScreenshot.Should().BeTrue();
        needs.NeedsVideoAudioClip.Should().BeTrue();
        needs.NeedsSasayakiAudio.Should().BeFalse();
    }

    [Fact]
    public void ResolveMediaNeedsForMining_DoesNotRequestVideoMediaWhenFieldsDoNotUseIt()
    {
        var noteType = new AnkiNoteType
        {
            Id = 1,
            Name = "Anime",
            Fields = ["Sentence"],
        };

        var needs = AnkiFieldMappingResolver.ResolveMediaNeedsForMining(
            noteType,
            new Dictionary<string, string> { ["Sentence"] = "{video-subtitle}" },
            new AnkiMiningContext { VideoFileName = "Episode.mkv" });

        needs.NeedsVideoScreenshot.Should().BeFalse();
        needs.NeedsVideoAudioClip.Should().BeFalse();
        needs.NeedsSasayakiAudio.Should().BeFalse();
    }

    [Fact]
    public void ResolveMediaNeedsForMining_RequestsSasayakiAudioForNovelSentenceAudioMapping()
    {
        var noteType = new AnkiNoteType
        {
            Id = 1,
            Name = "Lapis",
            Fields = ["Sentence", "SentenceAudio"],
        };

        var needs = AnkiFieldMappingResolver.ResolveMediaNeedsForMining(
            noteType,
            new Dictionary<string, string> { ["SentenceAudio"] = "{sasayaki-audio}" },
            new AnkiMiningContext { DocumentTitle = "化物語" });

        needs.NeedsVideoScreenshot.Should().BeFalse();
        needs.NeedsVideoAudioClip.Should().BeFalse();
        needs.NeedsSasayakiAudio.Should().BeTrue();
    }
}
