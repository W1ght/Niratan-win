using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;
using Moq;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelChapterTests
{
    [Fact]
    public void ReplaceChapters_BuildsRowsAndHighlightsCurrentChapterFromPlaybackPosition()
    {
        var sut = CreateSut();

        sut.ReplaceChapters(
        [
            new VideoChapter(0, "Opening", TimeSpan.Zero),
            new VideoChapter(1, "Part A", TimeSpan.FromSeconds(60)),
            new VideoChapter(2, "Part B", TimeSpan.FromSeconds(120)),
        ]);

        sut.UpdatePosition(TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(180));

        sut.HasChapters.Should().BeTrue();
        sut.ChapterRows.Select(row => row.Title).Should().Equal("Opening", "Part A", "Part B");
        sut.ChapterRows.Select(row => row.IsCurrent).Should().Equal(false, true, false);
        sut.ChapterRows[1].AutomationName.Should().Be("Part A, current chapter");
    }

    private static VideoPlayerViewModel CreateSut() =>
        new(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());
}
