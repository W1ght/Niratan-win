using System.Globalization;
using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderSearchEngineTests
{
    [Fact]
    public void Search_IgnoresRubyScriptsAndStylesWhileKeepingVisibleSpacing()
    {
        var document = CreateDocument(
            [
                Chapter(0, "c0.xhtml", 0, 3),
                Chapter(1, "c1.xhtml", 3, 3),
            ],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body><ruby>漢<rt>かん</rt></ruby>字 A<script>bad</script><style>bad</style></body></html>",
                ["c1.xhtml"] = "<html><body>猫と犬</body></html>",
            },
            new Dictionary<int, string> { [0] = "First", [1] = "Second" });

        var result = new ReaderSearchEngine(document).Search("漢字A").Single();

        result.ChapterIndex.Should().Be(0);
        result.ChapterLabel.Should().Be("First");
        result.Character.Should().Be(0);
        result.Snippet.Should().Contain("漢字 A");
        result.Snippet.Should().NotContain("かん");
        result.Snippet.Should().NotContain("bad");
    }

    [Fact]
    public void Search_IsPunctuationInsensitiveButSnippetKeepsDisplayPunctuation()
    {
        var document = CreateDocument(
            [Chapter(0, "c0.xhtml", 0, 7)],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>吾輩は「猫、です。」犬</body></html>",
            });

        var result = new ReaderSearchEngine(document).Search("猫!です").Single();

        result.Character.Should().Be(3);
        result.Snippet.Should().Contain("「猫、です。」");
        SliceTextElements(result.Snippet, result.SnippetMatchStart, result.SnippetMatchEnd)
            .Should()
            .Be("猫、です");
    }

    [Fact]
    public void Search_DecodesHtmlEntitiesAndReportsNonBmpDisplayOffsets()
    {
        var document = CreateDocument(
            [Chapter(0, "c0.xhtml", 0, 4)],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>A🙂B&amp;C</body></html>",
            });
        var engine = new ReaderSearchEngine(document);

        var emojiResult = engine.Search("AB").Single();
        var entityResult = engine.Search("BC").Single();

        emojiResult.Snippet.Should().Be("A🙂B&C");
        SliceTextElements(emojiResult.Snippet, emojiResult.SnippetMatchStart, emojiResult.SnippetMatchEnd)
            .Should()
            .Be("A🙂B");
        SliceTextElements(entityResult.Snippet, entityResult.SnippetMatchStart, entityResult.SnippetMatchEnd)
            .Should()
            .Be("B&C");
    }

    [Fact]
    public void SearchResult_ExposesSnippetSegmentsForMacAlignedResultHighlighting()
    {
        var document = CreateDocument(
            [Chapter(0, "c0.xhtml", 0, 5)],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>前🙂「猫、です。」後</body></html>",
            });

        var result = new ReaderSearchEngine(document).Search("猫!です").Single();

        result.SnippetBeforeMatch.Should().Be("前🙂「");
        result.SnippetMatch.Should().Be("猫、です");
        result.SnippetAfterMatch.Should().Be("。」後");
        (result.SnippetBeforeMatch + result.SnippetMatch + result.SnippetAfterMatch)
            .Should()
            .Be(result.Snippet);
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndRejectsEmptyOrPunctuationOnlyQueries()
    {
        var document = CreateDocument(
            [Chapter(0, "c0.xhtml", 0, 6)],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>CATcat</body></html>",
            });
        var engine = new ReaderSearchEngine(document);

        engine.Search("cat").Select(r => r.Character).Should().Equal(0, 3);
        engine.Search("").Should().BeEmpty();
        engine.Search(" ! ").Should().BeEmpty();
    }

    [Fact]
    public void Search_RejectsCrossChapterMatchesWithoutSkippingNextChapterMatch()
    {
        var document = CreateDocument(
            [
                Chapter(0, "c0.xhtml", 0, 2),
                Chapter(1, "c1.xhtml", 2, 2),
            ],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>ab</body></html>",
                ["c1.xhtml"] = "<html><body>bc</body></html>",
            });

        var results = new ReaderSearchEngine(document).Search("bc");

        results.Select(r => r.Character).Should().Equal(2);
        results.Select(r => r.ChapterIndex).Should().Equal(1);
    }

    [Fact]
    public void Search_RespectsResultLimitAndFallsBackToNearestPreviousChapterLabel()
    {
        var document = CreateDocument(
            [
                Chapter(0, "c0.xhtml", 0, 3),
                Chapter(1, "c1.xhtml", 3, 3),
            ],
            new Dictionary<string, string>
            {
                ["c0.xhtml"] = "<html><body>猫猫猫</body></html>",
                ["c1.xhtml"] = "<html><body>猫猫猫</body></html>",
            },
            new Dictionary<int, string> { [0] = "Top" });

        var results = new ReaderSearchEngine(document).Search("猫", maxResults: 4);

        results.Should().HaveCount(4);
        results.Select(r => r.ChapterLabel).Should().Equal("Top", "Top", "Top", "Top");
    }

    private static ReaderSearchDocument CreateDocument(
        IReadOnlyList<ReaderSearchChapter> chapters,
        IReadOnlyDictionary<string, string> htmlByPath,
        IReadOnlyDictionary<int, string>? labels = null) =>
        new(chapters, htmlByPath, labels ?? new Dictionary<int, string>());

    private static ReaderSearchChapter Chapter(
        int index,
        string path,
        int currentTotal,
        int characterCount) =>
        new(index, path, currentTotal, characterCount);

    private static string SliceTextElements(string value, int start, int end)
    {
        var elements = StringInfo.ParseCombiningCharacters(value);
        var safeStart = Math.Clamp(start, 0, elements.Length);
        var safeEnd = Math.Clamp(end, safeStart, elements.Length);
        var startIndex = safeStart < elements.Length ? elements[safeStart] : value.Length;
        var endIndex = safeEnd < elements.Length ? elements[safeEnd] : value.Length;
        return value[startIndex..endIndex];
    }
}
