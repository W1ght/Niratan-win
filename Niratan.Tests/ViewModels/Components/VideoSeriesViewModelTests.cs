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
    public void MergedSeasonNodes_KeepTheRequestedRootSeriesIdentity()
    {
        var rootSeriesId = Guid.NewGuid();
        var seasonFourSeriesId = Guid.NewGuid();
        var seasonFour = Episode("season-4", seasonFourSeriesId, Guid.NewGuid(), 4, 1, absolute: null);
        seasonFour.CatalogSeriesTitle = "Re:ゼロから始める異世界生活 4th season";
        seasonFour.CatalogSeriesOverview = "Fourth season overview";
        seasonFour.CatalogSeriesReleaseYear = 2026;
        seasonFour.ExternalIds = new Dictionary<string, string> { ["anidb"] = "189046" };
        var root = Episode("root", rootSeriesId, Guid.NewGuid(), 1, 1, absolute: null);
        root.CatalogSeriesTitle = "Re:ゼロから始める異世界生活";
        root.CatalogSeriesOverview = "Root series overview";
        root.CatalogSeriesReleaseYear = 2016;
        root.ExternalIds = new Dictionary<string, string> { ["anidb"] = "21355" };

        var result = new VideoSeriesViewModel(rootSeriesId, [seasonFour, root]);

        result.Title.Should().Be("Re:ゼロから始める異世界生活");
        result.Overview.Should().Be("Root series overview");
        result.MetadataYear.Should().Be(2016);
        result.MetadataIdentity.Should().NotBeNull();
        result.MetadataIdentity!.ProviderId.Should().Be("anidb");
        result.MetadataIdentity.ProviderItemId.Should().Be("21355");
        result.YearRangeText.Should().Be("2016–2026");
    }

    [Fact]
    public void MergedSeasonCards_PreferTheOwningAidSeriesPosterForEachSeason()
    {
        var rootPoster = Path.GetTempFileName();
        var secondSeasonPoster = Path.GetTempFileName();
        var remotePoster = Path.GetTempFileName();
        try
        {
            var rootSeriesId = Guid.NewGuid();
            var secondSeriesId = Guid.NewGuid();
            var firstSeason = Episode(
                "season-1", rootSeriesId, Guid.NewGuid(), 1, 1, absolute: null);
            firstSeason.SeriesPosterPath = rootPoster;
            firstSeason.CatalogSeriesSeasons =
            [
                new VideoDiscoverySeason(
                    1,
                    "First cour from AID 101",
                    null,
                    null,
                    1,
                    null,
                    []),
            ];
            var secondSeason = Episode(
                "season-2", secondSeriesId, Guid.NewGuid(), 2, 1, absolute: null);
            secondSeason.SeriesPosterPath = secondSeasonPoster;
            secondSeason.CatalogSeriesSeasons =
            [
                new VideoDiscoverySeason(
                    2,
                    "Second cour from AID 202",
                    null,
                    null,
                    1,
                    null,
                    [],
                    remotePoster),
            ];

            var result = new VideoSeriesViewModel(rootSeriesId, [firstSeason, secondSeason]);

            result.PosterPath.Should().Be(rootPoster);
            result.Seasons.Single(season => season.SeasonNumber == 1)
                .PosterPath.Should().Be(rootPoster);
            var projectedSecondSeason = result.Seasons.Single(season => season.SeasonNumber == 2);
            projectedSecondSeason.Title.Should().Be("Second cour from AID 202",
                "merged group season metadata must include the non-root AID");
            projectedSecondSeason.PosterPath.Should().Be(secondSeasonPoster,
                    "the AID series owning the season is stronger than root or remote fallback artwork");
        }
        finally
        {
            File.Delete(rootPoster);
            File.Delete(secondSeasonPoster);
            File.Delete(remotePoster);
        }
    }

    [Fact]
    public void AnimeSeries_PrefersAniDbIdentityOverSupplementalTmdbIdentity()
    {
        var seriesId = Guid.NewGuid();
        var episode = Episode("episode", seriesId, Guid.NewGuid(), 1, 1, absolute: 1);
        episode.LibraryMediaType = VideoLibraryMediaType.Anime;
        episode.CatalogSeriesTitle = "Re:ゼロから始める異世界生活";
        episode.CatalogSeriesReleaseYear = 2016;
        episode.ExternalIds = new Dictionary<string, string>
        {
            ["tmdb"] = "65942",
            ["anidb"] = "11370",
        };

        var result = new VideoSeriesViewModel(seriesId, [episode]);

        result.MetadataIdentity.Should().NotBeNull();
        result.MetadataIdentity!.ProviderId.Should().Be("anidb");
        result.MetadataIdentity.ProviderItemId.Should().Be("11370");
        result.MetadataIdentity.ExternalIds.Should().Contain("tmdb", "65942");
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
    public void RemoteSpecials_PreserveEveryAniDbTypeWithDuplicateNumericNumbers()
    {
        var seriesId = Guid.NewGuid();
        var seedEpisode = Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: null);
        var result = new VideoSeriesViewModel(seriesId, [seedEpisode]);

        var apply = () => result.ApplyRemoteSeasons([
            new VideoDiscoverySeason(
                0,
                "Specials",
                null,
                null,
                6,
                null,
                [
                    RemoteEpisode(1, "Other", "O1", 600),
                    RemoteEpisode(1, "Second special", "S1", 101),
                    RemoteEpisode(1, "Trailer", "T1", 400),
                    RemoteEpisode(1, "Parody", "P1", 500),
                    RemoteEpisode(1, "First special", "S1", 100),
                    RemoteEpisode(1, "Credits", "C1", 300),
                ]),
        ]);

        apply.Should().NotThrow();
        var specials = result.Seasons.Single(season => season.SeasonNumber == 0);
        specials.EpisodeSlots.Should().HaveCount(6);
        specials.EpisodeSlots.Select(slot => slot.NumberText)
            .Should().Equal("S1.", "S1.", "C1.", "T1.", "P1.", "O1.");
        specials.EpisodeSlots.Select(slot => slot.Title)
            .Should().Equal("First special", "Second special", "Credits", "Trailer", "Parody", "Other");
        specials.EpisodeSlots.Where(slot => slot.NumberText == "S1.")
            .Should().OnlyContain(slot => slot.CanDownload);
        specials.EpisodeSlots.Where(slot => slot.NumberText is "C1." or "T1." or "P1." or "O1.")
            .Should().OnlyContain(slot => slot.IsSupplemental && !slot.CanDownload);
    }

    [Fact]
    public void RemoteSpecials_WithStaleMissingDisplayNumbers_PreserveNumericDuplicates()
    {
        var seriesId = Guid.NewGuid();
        var result = new VideoSeriesViewModel(
            seriesId,
            [Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: null)]);

        var apply = () => result.ApplyRemoteSeasons([
            new VideoDiscoverySeason(
                0,
                "Specials",
                null,
                null,
                2,
                null,
                [
                    new VideoDiscoveryEpisode(
                        1, "Later stale row", null, null, null, null, null,
                        "https://anidb.net/episode/701"),
                    new VideoDiscoveryEpisode(
                        1, "Earlier stale row", null, null, null, null, null,
                        "https://anidb.net/episode/700"),
                ]),
        ]);

        apply.Should().NotThrow();
        var slots = result.Seasons.Single(season => season.SeasonNumber == 0).EpisodeSlots;
        slots.Should().HaveCount(2);
        slots.Select(slot => slot.NumberText).Should().Equal("1.", "1.");
        slots.Select(slot => slot.Title).Should().Equal("Earlier stale row", "Later stale row");
    }

    [Fact]
    public void RemoteSpecials_FromMultipleAidSnapshots_MergeByEpisodeIdentityWithoutLosingTypedRows()
    {
        var seriesId = Guid.NewGuid();
        var result = new VideoSeriesViewModel(
            seriesId,
            [Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: null)]);
        var firstSpecial = RemoteEpisode(1, "First special", "S1", 100);

        result.ApplyRemoteSeasons([
            new VideoDiscoverySeason(
                0, "Specials", null, null, 1, null, [firstSpecial]),
            new VideoDiscoverySeason(
                0, "Specials", null, null, 2, null,
                [firstSpecial, RemoteEpisode(1, "Second anime special", "S1", 200)]),
        ]);

        var slots = result.Seasons.Single(season => season.SeasonNumber == 0).EpisodeSlots;
        slots.Should().HaveCount(2, "the repeated EID is one row but the second AID's S1 must survive");
        slots.Select(slot => slot.RemoteEpisode!.SourceUrl)
            .Should().Equal("https://anidb.net/episode/100", "https://anidb.net/episode/200");
    }

    [Fact]
    public void RemoteSeasonZero_MatchesDownloadedSpecialFeaturesByAniDbEpisodeIdentity()
    {
        var seriesId = Guid.NewGuid();
        var localSpecial = Episode("local-special", seriesId, Guid.NewGuid(), 0, 1, absolute: null, special: true);
        localSpecial.ExternalIds = new Dictionary<string, string>
        {
            ["anidb-episode"] = "100",
        };
        var result = new VideoSeriesViewModel(
            seriesId,
            [Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: null), localSpecial]);

        result.ApplyRemoteSeasons([
            new VideoDiscoverySeason(
                0,
                "Specials",
                null,
                null,
                1,
                null,
                [RemoteEpisode(1, "Special", "S1", 100)]),
        ]);

        var slot = result.Seasons.Single(season => season.SeasonNumber == 0).EpisodeSlots.Single();
        slot.DownloadedEpisode!.Video.Id.Should().Be("local-special");
        slot.NumberText.Should().Be("S1.");
    }

    [Fact]
    public void RemoteRegularDuplicates_PreferLocalIdentityThenRichnessAndKeepLocalOnlyEpisodes()
    {
        var seriesId = Guid.NewGuid();
        var localEpisodeOne = Episode("local-1", seriesId, Guid.NewGuid(), 1, 1, absolute: null);
        localEpisodeOne.ExternalIds = new Dictionary<string, string>
        {
            ["anidb-episode"] = "1001",
        };
        var localEpisodeThree = Episode("local-3", seriesId, Guid.NewGuid(), 1, 3, absolute: null);
        var result = new VideoSeriesViewModel(seriesId, [localEpisodeOne, localEpisodeThree]);

        result.ApplyRemoteSeasons([
            new VideoDiscoverySeason(
                1,
                "Season 1",
                null,
                null,
                2,
                null,
                [
                    new VideoDiscoveryEpisode(
                        1, "Wrong rich episode", "原題", "Rich but belongs to another EID",
                        "2026-01-01", 24, "https://example.invalid/wrong.jpg",
                        "https://anidb.net/episode/9999"),
                    new VideoDiscoveryEpisode(
                        1, "Episode 1", null, null, null, null, null,
                        "https://anidb.net/episode/1001"),
                    new VideoDiscoveryEpisode(
                        2, "Episode 2", null, null, null, null, null,
                        "https://anidb.net/episode/2001"),
                    new VideoDiscoveryEpisode(
                        2, "Rich second", "第二話", "Full overview", "2026-01-08", 24,
                        "https://example.invalid/second.jpg", "https://anidb.net/episode/2002"),
                    new VideoDiscoveryEpisode(
                        4, "Stable candidate", null, null, null, null, null,
                        "https://anidb.net/episode/4002"),
                    new VideoDiscoveryEpisode(
                        4, "Stable candidate", null, null, null, null, null,
                        "https://anidb.net/episode/4001"),
                ]),
        ]);

        var season = result.Seasons.Single();
        season.EpisodeSlots.Select(slot => slot.EpisodeNumber).Should().Equal(1, 2, 3, 4);
        season.EpisodeSlots[0].RemoteEpisode!.SourceUrl.Should().EndWith("/1001");
        season.EpisodeSlots[0].DownloadedEpisode!.Video.Id.Should().Be("local-1");
        season.EpisodeSlots[1].Title.Should().Be("Rich second");
        season.EpisodeSlots[2].DownloadedEpisode!.Video.Id.Should().Be("local-3");
        season.EpisodeSlots[3].RemoteEpisode!.SourceUrl.Should().EndWith("/4001");
        season.EpisodeCount.Should().Be(4);
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

    [Fact]
    public void PersistedCatalogSeasons_AreAppliedDuringDetailsRebuild()
    {
        var seriesId = Guid.NewGuid();
        var seedEpisode = Episode("seed", seriesId, Guid.NewGuid(), 1, 1, absolute: 1);
        seedEpisode.CatalogSeriesSeasons = Enumerable.Range(1, 4)
            .Select(season => new VideoDiscoverySeason(
                season,
                $"第 {season} 季",
                $"Season {season} overview",
                null,
                2,
                null,
                [
                    new VideoDiscoveryEpisode(1, $"S{season}E1", null, null, null, null, null, null),
                    new VideoDiscoveryEpisode(2, $"S{season}E2", null, null, null, null, null, null),
                ]))
            .ToArray();

        var rebuilt = new VideoSeriesViewModel(seriesId, [seedEpisode]);

        rebuilt.HasRemoteSeasonMetadata.Should().BeTrue();
        rebuilt.Seasons.Select(season => season.SeasonNumber).Should().Equal(1, 2, 3, 4);
        rebuilt.Seasons[3].EpisodeSlots.Should().HaveCount(2);
        rebuilt.Seasons[3].Title.Should().Be("第 4 季");
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

    private static VideoDiscoveryEpisode RemoteEpisode(
        int number,
        string title,
        string displayNumber,
        int episodeId) => new(
        number,
        title,
        null,
        null,
        null,
        null,
        null,
        $"https://anidb.net/episode/{episodeId}",
        DisplayNumber: displayNumber);
}
