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
    public void BuildDuplicateSearchQuery_EscapesQuotesInUserText()
    {
        var settings = new AnkiSettings();

        var query = AnkiService.BuildDuplicateSearchQuery("星\"空", Deck, NoteType, settings);

        query.Should().Be("\"note:Lapis\" \"expression:星\\\"空\"");
    }

    [Fact]
    public void BuildDuplicateSearchQuery_EscapesAnkiSearchWildcards()
    {
        var settings = new AnkiSettings();

        var query = AnkiService.BuildDuplicateSearchQuery("A*B_C:D", Deck, NoteType, settings);

        query.Should().Be("\"note:Lapis\" \"expression:A\\*B\\_C\\:D\"");
    }

    [Fact]
    public void BuildDuplicateSearchQuery_WhenExpressionIsNotInTheFirstField_SearchesBothFields()
    {
        var customNoteType = new AnkiNoteType
        {
            Id = 9,
            Name = "Custom",
            Fields = ["Key", "Word", "Sentence"],
        };
        var settings = new AnkiSettings
        {
            FieldMappings = new Dictionary<string, string>
            {
                ["Key"] = "{furigana-plain}",
                ["Word"] = "{expression}",
                ["Sentence"] = "{sentence}",
            },
        };

        var query = AnkiService.BuildDuplicateSearchQuery("星", Deck, customNoteType, settings);

        query.Should().Be("\"note:Custom\" (\"key:星\" or \"word:星\")");
    }

    [Fact]
    public void ResolveDuplicateSearchFields_KeepsTheFirstFieldFirst()
    {
        var customNoteType = new AnkiNoteType
        {
            Id = 9,
            Name = "Custom",
            Fields = ["Key", "Word"],
        };
        var settings = new AnkiSettings
        {
            FieldMappings = new Dictionary<string, string> { ["Word"] = "{expression}" },
        };

        var fields = AnkiService.ResolveDuplicateSearchFields(customNoteType, settings);

        fields.Should().Equal("Key", "Word");
    }

    [Fact]
    public void BuildDuplicateSearchQuery_LapisSearchesMushoByExpression()
    {
        var query = AnkiService.BuildDuplicateSearchQuery(
            "無償",
            Deck,
            NoteType,
            new AnkiSettings());

        query.Should().Be("\"note:Lapis\" \"expression:無償\"");
    }
}
