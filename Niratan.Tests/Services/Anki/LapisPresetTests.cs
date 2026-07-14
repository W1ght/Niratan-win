using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class LapisPresetTests
{
    [Fact]
    public void AutofillDefaults_FillsMissingLapisFieldsWithoutOverwritingCustomMappings()
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
                "RemovedField",
            ],
        };

        var mappings = LapisPreset.AutofillDefaults(
            noteType,
            new Dictionary<string, string>
            {
                ["Expression"] = "{custom-expression}",
                ["MainDefinition"] = "   ",
                ["RemovedField"] = "{removed}",
            },
            AnkiFieldMappingPreset.Novel);

        mappings["Expression"].Should().Be("{custom-expression}");
        mappings["MainDefinition"].Should().Be("{glossary-first}");
        mappings["Sentence"].Should().Be("{sentence}");
        mappings["SentenceAudio"].Should().Be("{sasayaki-audio}");
        mappings["Picture"].Should().Be("{book-cover}");
        mappings["RemovedField"].Should().Be("{removed}");
    }

    [Fact]
    public void ApplyDefaults_RestoresNovelLapisMappingsAndClearsDefinitionPicture()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Lapis",
            Fields =
            [
                "Expression",
                "Sentence",
                "SentenceAudio",
                "Picture",
                "DefinitionPicture",
                "ExtraField",
            ],
        };

        var mappings = LapisPreset.ApplyDefaults(
            noteType,
            new Dictionary<string, string>
            {
                ["Expression"] = "{custom-expression}",
                ["Sentence"] = "{video-subtitle}",
                ["SentenceAudio"] = "{video-audio-clip}",
                ["Picture"] = "{video-screenshot}",
                ["DefinitionPicture"] = "{glossary}",
                ["ExtraField"] = "{custom-extra}",
                ["UnavailableField"] = "{removed}",
            },
            AnkiFieldMappingPreset.Novel);

        mappings["Expression"].Should().Be("{expression}");
        mappings["Sentence"].Should().Be("{sentence}");
        mappings["SentenceAudio"].Should().Be("{sasayaki-audio}");
        mappings["Picture"].Should().Be("{book-cover}");
        mappings.Should().NotContainKey("DefinitionPicture");
        mappings["ExtraField"].Should().Be("{custom-extra}");
        mappings.Should().NotContainKey("UnavailableField");
    }

    [Fact]
    public void ApplyDefaults_RestoresAnimeLapisMediaMappings()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Lapis",
            Fields =
            [
                "Expression",
                "SentenceAudio",
                "Picture",
                "MiscInfo",
                "ExtraField",
            ],
        };

        var mappings = LapisPreset.ApplyDefaults(
            noteType,
            new Dictionary<string, string>
            {
                ["Expression"] = "{custom-expression}",
                ["SentenceAudio"] = "{sasayaki-audio}",
                ["Picture"] = "{book-cover}",
                ["ExtraField"] = "{custom-extra}",
            },
            AnkiFieldMappingPreset.Anime);

        mappings["Expression"].Should().Be("{expression}");
        mappings["SentenceAudio"].Should().Be("{video-audio-clip}");
        mappings["Picture"].Should().Be("{video-screenshot}");
        mappings["MiscInfo"].Should().Be("{video-file-name} ({video-timestamp})");
        mappings["ExtraField"].Should().Be("{custom-extra}");
    }

    [Fact]
    public void HasDefaults_LeavesUnknownNoteTypesUnchanged()
    {
        var noteType = new AnkiNoteType
        {
            Name = "Custom",
            Fields = ["Front"],
        };

        LapisPreset.HasDefaults(noteType).Should().BeFalse();

        var existing = new Dictionary<string, string> { ["Front"] = "{expression}" };
        LapisPreset.AutofillDefaults(noteType, existing, AnkiFieldMappingPreset.Novel)
            .Should()
            .Equal(existing);
    }
}
