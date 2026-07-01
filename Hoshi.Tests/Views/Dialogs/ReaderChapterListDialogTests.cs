using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Views.Dialogs;

namespace Hoshi.Tests.Views.Dialogs;

public sealed class ReaderChapterListDialogTests
{
    [Fact]
    public void BuildChapterRows_SelectsVisibleTocRowByCurrentCharacterCount()
    {
        var chapters = new List<EpubChapter>
        {
            new() { Href = "Text/cover.xhtml", SpineIndex = 0 },
            new() { Href = "Text/nav.xhtml", SpineIndex = 1 },
            new() { Href = "Text/spring.xhtml", SpineIndex = 2 },
            new() { Href = "Text/stars.xhtml", SpineIndex = 3 },
            new() { Href = "Text/waves.xhtml", SpineIndex = 4 },
            new() { Href = "Text/afterword.xhtml", SpineIndex = 5 },
        };
        var toc = new List<EpubTocItem>
        {
            new() { Label = "cover", Href = "Text/cover.xhtml" },
            new() { Label = "toc", Href = "Text/nav.xhtml" },
            new() { Label = "spring", Href = "Text/spring.xhtml" },
            new() { Label = "stars", Href = "Text/stars.xhtml" },
            new() { Label = "waves", Href = "Text/waves.xhtml" },
            new() { Label = "afterword", Href = "Text/afterword.xhtml" },
        };
        var starts = new[] { 0, 115, 170, 49112, 102566, 162393 };

        var rows = ReaderChapterListDialog.BuildChapterRows(chapters, toc, 0, starts, 49962);

        rows.Single(row => row.IsCurrent).DisplayTitle.Should().Be("stars");
    }

    [Fact]
    public void BuildChapterRows_KeepsPreviousVisibleTocRowSelectedBetweenChapters()
    {
        var chapters = new List<EpubChapter>
        {
            new() { Href = "chapter-19.xhtml", SpineIndex = 0 },
            new() { Href = "chapter-20.xhtml", SpineIndex = 1 },
            new() { Href = "chapter-21.xhtml", SpineIndex = 2 },
        };
        var toc = new List<EpubTocItem>
        {
            new() { Label = "19. Different Ways", Href = "chapter-19.xhtml" },
            new() { Label = "20. The Rain", Href = "chapter-20.xhtml" },
            new() { Label = "21. A Deer Hunt", Href = "chapter-21.xhtml" },
        };
        var starts = new[] { 189943, 202338, 210893 };

        var rows = ReaderChapterListDialog.BuildChapterRows(chapters, toc, 0, starts, 203728);

        rows.Single(row => row.IsCurrent).DisplayTitle.Should().Be("20. The Rain");
    }
}
