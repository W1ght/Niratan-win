using FluentAssertions;
using System.Collections.Immutable;
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
        result.PrimaryPlayItem!.Video.Id.Should().Be("regular");
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

    [Fact]
    public void LogicalEpisodes_CollapseAssetVersionsAndFilterBySelectedSeason()
    {
        var seriesId = Guid.NewGuid();
        var seasonOneEpisodeOneNode = Guid.NewGuid();
        var seasonOneEpisodeTwoNode = Guid.NewGuid();
        var seasonTwoEpisodeOneNode = Guid.NewGuid();
        var unavailableVersion = Episode(
            "s1e1-unavailable", seriesId, seasonOneEpisodeOneNode, 1, 1, absolute: 50,
            available: false);
        var availableVersion = Episode(
            "s1e1", seriesId, seasonOneEpisodeOneNode, 1, 1, absolute: 50);
        var seasonOneEpisodeTwo = Episode(
            "s1e2", seriesId, seasonOneEpisodeTwoNode, 1, 2, absolute: 51);
        var seasonTwoEpisodeOne = Episode(
            "s2e1", seriesId, seasonTwoEpisodeOneNode, 2, 1, absolute: 1);
        var special = Episode(
            "special", seriesId, Guid.NewGuid(), 0, 1, absolute: null, special: true);

        var result = new VideoSeriesViewModel(
            seriesId,
            [seasonTwoEpisodeOne, unavailableVersion, special, seasonOneEpisodeTwo, availableVersion]);

        result.RegularEpisodes.Select(item => item.Video.Id)
            .Should().Equal("s1e1", "s1e2", "s2e1");
        result.EpisodeCount.Should().Be(3);
        result.Seasons.Select(season => season.SeasonNumber).Should().Equal(1, 2);
        result.SelectedSeason.Should().Be(result.Seasons[0]);
        result.Episodes.Select(item => item.Video.Id).Should().Equal("s1e1", "s1e2");
        result.SpecialFeatures.Select(item => item.Video.Id).Should().Equal("special");

        result.SelectSeason(2);

        result.Seasons.Select(season => season.IsSelected).Should().Equal(false, true);
        result.Episodes.Select(item => item.Video.Id).Should().Equal("s2e1");
    }

    [Fact]
    public void Details_PreferSeriesOwnerMetadataOverEpisodeMetadata()
    {
        var seriesId = Guid.NewGuid();
        var episode = Episode("episode", seriesId, Guid.NewGuid(), 3, 1, absolute: null);
        episode.CatalogSeriesTitle = "シリーズ";
        episode.CatalogSeriesOriginalTitle = "シリーズ原題";
        episode.CatalogSeriesOverview = "シリーズ概要";
        episode.CatalogSeriesReleaseYear = 2024;
        episode.OriginalTitle = "単話原題";
        episode.Overview = "単話概要";
        episode.ReleaseYear = 2025;

        var result = new VideoSeriesViewModel(seriesId, [episode]);

        result.Title.Should().Be("シリーズ");
        result.OriginalTitle.Should().Be("シリーズ原題");
        result.Overview.Should().Be("シリーズ概要");
        result.YearRangeText.Should().Be("2024");
    }

    [Fact]
    public void RemoteSeasons_MergeMissingEpisodesWithDownloadedEpisodes()
    {
        var seriesId = Guid.NewGuid();
        var localEpisode = Episode("episode-1", seriesId, Guid.NewGuid(), 1, 1, absolute: null);
        localEpisode.Title = "Downloaded episode";
        var localPath = Path.GetTempFileName();
        try
        {
            localEpisode.FilePath = localPath;
            var result = new VideoSeriesViewModel(seriesId, [localEpisode]);

            result.ApplyRemoteSeasons([
                new VideoDiscoverySeason(
                    1,
                    "Season 1",
                    null,
                    "2026-01-01",
                    3,
                    null,
                    [
                        new VideoDiscoveryEpisode(1, "First episode", null, null, null, null, null, null),
                        new VideoDiscoveryEpisode(2, "Second episode", null, null, null, null, null, null),
                        new VideoDiscoveryEpisode(3, "Third episode", null, null, null, null, null, null),
                    ]),
            ]);

            result.Seasons.Should().ContainSingle();
            result.Episodes.Should().ContainSingle().Which.Video.Id.Should().Be("episode-1");
            result.SelectedSeason!.EpisodeSlots.Should().HaveCount(3);
            result.SelectedSeason.EpisodeSlots[0].IsDownloaded.Should().BeTrue();
            result.SelectedSeason.EpisodeSlots[1].IsDownloaded.Should().BeFalse();
            result.SelectedSeason.EpisodeSlots[1].Title.Should().Be("Second episode");
            result.SelectedSeason.EpisodeSlots[2].StatusText.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public void RemoteSeasons_KeepEveryScrapedSeasonAndEpisode()
    {
        var seriesId = Guid.NewGuid();
        var seedEpisode = Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: null);
        var result = new VideoSeriesViewModel(seriesId, [seedEpisode]);
        var remoteSeasons = Enumerable.Range(1, 25)
            .Select(seasonNumber => new VideoDiscoverySeason(
                seasonNumber,
                $"Season {seasonNumber}",
                null,
                null,
                201,
                null,
                ImmutableArray.CreateRange(Enumerable.Range(1, 201).Select(episodeNumber =>
                    new VideoDiscoveryEpisode(
                        episodeNumber,
                        $"Episode {episodeNumber}",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null)))))
            .ToArray();

        result.ApplyRemoteSeasons(remoteSeasons);

        result.Seasons.Should().HaveCount(25);
        result.Seasons.Select(season => season.SeasonNumber)
            .Should().Equal(Enumerable.Range(1, 25).Select(number => (int?)number));
        result.Seasons[^1].EpisodeSlots.Should().HaveCount(201);
        result.Seasons[^1].EpisodeSlots[^1].EpisodeNumber.Should().Be(201);
        result.Seasons[^1].EpisodeSlots[^1].IsDownloaded.Should().BeFalse();
    }

    private static VideoItem Episode(
        string id,
        Guid seriesId,
        Guid nodeId,
        int? season,
        int episode,
        int? absolute,
        bool available = true,
        bool special = false) => new()
    {
        Id = id,
        Title = id,
        CatalogSeriesNodeId = seriesId,
        CatalogSeriesTitle = "作品",
        CatalogNodeId = nodeId,
        CatalogNodeKind = VideoCatalogNodeKind.Episode,
        SeasonNumber = season,
        EpisodeNumber = episode,
        AbsoluteEpisodeNumber = absolute,
        IsSpecialEpisode = special,
        IsAvailable = available,
        FilePath = $@"D:\Anime\{id}.mkv",
    };
}
