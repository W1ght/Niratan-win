using FluentAssertions;
using Niratan.Models.Anki;

namespace Niratan.Tests.Models.Anki;

public sealed class AnkiMiningResultTests
{
    [Fact]
    public void Duplicate_KeepsEveryMatchingNote()
    {
        var result = AnkiMiningResult.Duplicate("dupe", [77, 88, 99]);

        // Collapsing this to one id is what limited the magnifier to a single card.
        result.OpenableNoteIds.Should().Equal(77, 88, 99);
        result.NoteId.Should().Be(77);
        result.WebStatus.Should().Be("duplicate");
    }

    [Fact]
    public void Duplicate_DropsInvalidAndRepeatedNoteIds()
    {
        var result = AnkiMiningResult.Duplicate("dupe", [0, 77, -1, 77, 88]);

        result.OpenableNoteIds.Should().Equal(77, 88);
    }

    [Fact]
    public void Duplicate_WithoutNoteIds_HasNothingToOpen()
    {
        var result = AnkiMiningResult.Duplicate("dupe");

        result.OpenableNoteIds.Should().BeEmpty();
        result.NoteId.Should().BeNull();
    }

    [Fact]
    public void Added_CarriesTheNewNote()
    {
        var result = AnkiMiningResult.Added(4242, "added");

        result.OpenableNoteIds.Should().Equal(4242);
        result.NoteId.Should().Be(4242);
    }

    [Fact]
    public void Failed_HasNoOpenableNotes()
    {
        var result = AnkiMiningResult.Failed("nope");

        result.OpenableNoteIds.Should().BeEmpty();
        result.NoteId.Should().BeNull();
    }
}
