using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class SasayakiViewModelTests
{
    [Fact]
    public void UpdateChapters_BuildsTimedRowsFromMatchedBookChapters()
    {
        var sut = new SasayakiViewModel();
        var book = new EpubBook
        {
            Chapters =
            [
                new EpubChapter { Href = "Text/one.xhtml", SpineIndex = 0 },
                new EpubChapter { Href = "Text/two.xhtml", SpineIndex = 1 },
            ],
            Toc =
            [
                new EpubTocItem { Label = "Opening", Href = "Text/one.xhtml#start" },
                new EpubTocItem { Label = "Second", Href = "Text/two.xhtml" },
            ],
        };
        var match = new SasayakiMatchData
        {
            Matches =
            [
                new SasayakiMatch { ChapterIndex = 0, StartTime = 12 },
                new SasayakiMatch { ChapterIndex = 0, StartTime = 18 },
                new SasayakiMatch { ChapterIndex = 1, StartTime = 75 },
            ],
        };

        sut.UpdateChapters(match, book);

        sut.HasChapters.Should().BeTrue();
        sut.Chapters.Select(row => row.Title).Should().Equal("Opening", "Second");
        sut.Chapters.Select(row => row.StartTime).Should().Equal(12, 75);
        sut.Chapters.Select(row => row.StartTimeText).Should().Equal("00:12", "01:15");
    }

    [Fact]
    public void UpdateCurrentChapter_SelectsLastChapterAtOrBeforePlaybackPosition()
    {
        var sut = new SasayakiViewModel();
        var book = new EpubBook
        {
            Chapters =
            [
                new EpubChapter { Href = "one.xhtml" },
                new EpubChapter { Href = "two.xhtml" },
            ],
        };
        sut.UpdateChapters(
            new SasayakiMatchData
            {
                Matches =
                [
                    new SasayakiMatch { ChapterIndex = 0, StartTime = 10 },
                    new SasayakiMatch { ChapterIndex = 1, StartTime = 50 },
                ],
            },
            book);

        sut.UpdateCurrentChapter(0);
        sut.Chapters.Select(row => row.IsCurrent).Should().Equal(true, false);

        sut.UpdateCurrentChapter(51);

        sut.Chapters.Select(row => row.IsCurrent).Should().Equal(false, true);
    }
}
