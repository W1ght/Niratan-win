using FluentAssertions;
using Hoshi.Models;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;
using Moq;

namespace Hoshi.Tests.ViewModels.Pages;

public class VideoPlayerViewModelMiningHistoryTests
{
    [Fact]
    public void ReplaceMiningHistory_BuildsSourceGroupedRows()
    {
        var sut = new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());

        sut.ReplaceMiningHistoryItems(
        [
            new VideoMiningHistoryItem { Id = "one", SubtitleText = "一", SubtitleSourceName = "Episode 1" },
            new VideoMiningHistoryItem { Id = "two", SubtitleText = "二", SubtitleSourceName = "Episode 1" },
            new VideoMiningHistoryItem { Id = "three", SubtitleText = "三", SubtitleSourceName = "Episode 2" },
        ]);

        sut.HasMiningHistory.Should().BeTrue();
        sut.MiningHistoryRows.Select(row => row.ShowSourceHeader).Should().Equal(true, false, true);
        sut.MiningHistoryRows.Select(row => row.SourceHeader).Should().Equal("Episode 1", "", "Episode 2");
    }
}
