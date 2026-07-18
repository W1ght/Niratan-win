using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public sealed class AnkiDuplicateLookupTests
{
    private static readonly AnkiDeck Deck = new() { Id = 1, Name = "Mining::Japanese" };
    private static readonly AnkiNoteType NoteType = new()
    {
        Id = 2,
        Name = "Lapis",
        Fields = ["Expression", "Sentence"],
    };

    [Fact]
    public void BuildDuplicateSearchQuery_UsesConfiguredDeckRootModelAndFirstField()
    {
        var settings = new AnkiSettings
        {
            DuplicateScope = AnkiDuplicateScope.DeckRoot,
            CheckDuplicatesAcrossAllModels = false,
        };

        var query = AnkiService.BuildDuplicateSearchQuery("星", Deck, NoteType, settings);

        query.Should().Be("\"deck:Mining\" \"note:Lapis\" \"expression:星\"");
    }

    [Fact]
    public void BuildDuplicateSearchQuery_WhenCheckingAllModels_SearchesEachFirstField()
    {
        var settings = new AnkiSettings
        {
            DuplicateScope = AnkiDuplicateScope.Collection,
            CheckDuplicatesAcrossAllModels = true,
            AvailableNoteTypes =
            [
                new AnkiNoteType { Name = "Lapis", Fields = ["Expression"] },
                new AnkiNoteType { Name = "Basic", Fields = ["Front"] },
                new AnkiNoteType { Name = "Duplicate", Fields = ["expression"] },
            ],
        };

        var query = AnkiService.BuildDuplicateSearchQuery("星", Deck, NoteType, settings);

        query.Should().Be("(\"expression:星\" or \"front:星\")");
    }

    [Fact]
    public void BuildDuplicateSearchQuery_RemovesQuotesFromUserText()
    {
        var settings = new AnkiSettings();

        var query = AnkiService.BuildDuplicateSearchQuery("星\"空", Deck, NoteType, settings);

        query.Should().Be("\"note:Lapis\" \"expression:星空\"");
    }
}
