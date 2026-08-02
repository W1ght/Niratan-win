using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class VideoSeriesViewModelTests
{
    [Fact]
    public void BuildsOrderedSeasonsAndKeepsSpecialsSeparate()
    {
        var seriesId = Guid.NewGuid();
        var regular = new VideoItem
        {
            Id = "regular", CatalogSeriesNodeId = seriesId, CatalogSeriesTitle = "作品",
            CatalogNodeKind = VideoCatalogNodeKind.Episode, SeasonNumber = 1, EpisodeNumber = 1,
            FilePath = "D:\\Anime\\S01E01.mkv",
        };
        var special = new VideoItem
        {
            Id = "special", CatalogSeriesNodeId = seriesId, CatalogSeriesTitle = "作品",
            CatalogNodeKind = VideoCatalogNodeKind.Episode, SeasonNumber = 0, EpisodeNumber = 1,
            IsSpecialEpisode = true, FilePath = "D:\\Anime\\OVA.mkv",
        };

        var result = new VideoSeriesViewModel(seriesId, [special, regular]);

        result.Title.Should().Be("作品");
        result.Episodes.Select(item => item.Video.Id).Should().Equal("regular");
        result.SpecialFeatures.Select(item => item.Video.Id).Should().Equal("special");
        result.Seasons.Should().ContainSingle(season => season.SeasonNumber == 1);
        result.HasSpecialFeatures.Should().BeTrue();
    }

    [Fact]
    public void AbsoluteEpisodeOrder_DoesNotCreateAnEmptySeasonPosterRow()
    {
        var seriesId = Guid.NewGuid();
        var episode = new VideoItem
        {
            Id = "absolute", CatalogSeriesNodeId = seriesId, CatalogSeriesTitle = "作品",
            CatalogNodeKind = VideoCatalogNodeKind.Episode, AbsoluteEpisodeNumber = 8,
            EpisodeNumber = 8, FilePath = "D:\\Anime\\08.mkv",
        };

        var result = new VideoSeriesViewModel(seriesId, [episode]);

        result.HasSeasons.Should().BeFalse();
        result.Episodes.Should().ContainSingle();
    }
}
