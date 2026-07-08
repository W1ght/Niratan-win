using FluentAssertions;
using Hoshi.Models.Anki;
using Hoshi.Models.Settings;
using Hoshi.Services.Anki;

namespace Hoshi.Tests.Services.Anki;

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
    }
}
