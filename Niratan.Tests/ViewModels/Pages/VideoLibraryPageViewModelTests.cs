using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoLibraryPageViewModelTests
{
    [Fact]
    public void ContinueWatching_ShowsOnlyMostRecentlyPlayedEpisodePerSeries()
    {
        var seriesId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var episode1 = new VideoItem
        {
            Id = "episode-1", CatalogSeriesNodeId = seriesId, IsAvailable = true,
            DurationSeconds = 1_200, LastPositionSeconds = 120, LastOpenedAt = now.AddMinutes(-10),
        };
        var episode3 = new VideoItem
        {
            Id = "episode-3", CatalogSeriesNodeId = seriesId, IsAvailable = true,
            DurationSeconds = 1_200, LastPositionSeconds = 300, LastOpenedAt = now,
        };
        var movie = new VideoItem
        {
            Id = "movie", IsAvailable = true, DurationSeconds = 7_200,
            LastPositionSeconds = 900, LastOpenedAt = now.AddMinutes(-5),
        };

        var result = VideoLibraryPageViewModel.BuildContinueWatchingItems(
            [episode1, movie, episode3], 6);

        result.Select(video => video.Id).Should().Equal("episode-3", "movie");
    }

    [Fact]
    public async Task InitializeAsync_LoadsVideos()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "video-1",
                    Title = "Episode 1",
                    FilePath = "D:\\Anime\\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();

        sut.Videos.Should().ContainSingle();
        sut.Videos[0].Video.Title.Should().Be("Episode 1");
    }

    [Fact]
    public async Task ImportCommand_PicksVideoAndImports()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(d => d.OpenFilePickerAsync(".mkv", ".mp4", ".webm", ".avi", ".mov"))
            .ReturnsAsync("D:\\Anime\\episode1.mkv");
        var service = new RecordingVideoLibraryService();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            videoService: service,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ImportVideoCommand.ExecuteAsync(null);

        service.ImportedPaths.Should().Equal("D:\\Anime\\episode1.mkv");
        notification.Verify(
            n => n.ShowSuccess(
                It.Is<string>(message => !string.IsNullOrWhiteSpace(message)),
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
    }

    [Fact]
    public async Task OpenVideoCommand_UsesDedicatedPlayerService()
    {
        var service = new RecordingVideoLibraryService();
        var player = new RecordingVideoPlayerWindowService();
        var video = new VideoItem
        {
            Id = "video-1",
            Title = "Episode 1",
            FilePath = "D:\\Anime\\episode1.mkv",
        };
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.OpenVideoCommand.ExecuteAsync(new VideoItemViewModel(video));

        player.OpenedVideos.Should().ContainSingle().Which.Id.Should().Be("video-1");
        service.MarkedOpenedIds.Should().Equal("video-1");
    }

    [Fact]
    public async Task OpenVideoCommand_PassesVisibleVideosAsEpisodePlaylist()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode-1", Title = "Episode 1", FilePath = @"D:\Anime\episode1.mkv" },
                new VideoItem { Id = "episode-2", Title = "Episode 2", FilePath = @"D:\Anime\episode2.mkv" },
                new VideoItem { Id = "episode-3", Title = "Episode 3", FilePath = @"D:\Anime\episode3.mkv" },
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        await sut.OpenVideoCommand.ExecuteAsync(sut.Videos[1]);

        var visibleOrder = sut.Videos.Select(video => video.Video.Id);
        player.OpenedVideos.Should().ContainSingle().Which.Id.Should().Be("episode-2");
        player.OpenedPlaylists.Should().ContainSingle()
            .Which.Select(video => video.Id)
            .Should().Equal(visibleOrder);
    }

    [Fact]
    public async Task SeriesDetails_QueuesOnlyCollapsedRegularEpisodesAndKeepsSpecialSingle()
    {
        var seriesId = Guid.NewGuid();
        var otherSeriesId = Guid.NewGuid();
        var episodeOneNode = Guid.NewGuid();
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                SeriesEpisode("episode-2", seriesId, Guid.NewGuid(), 1, 2),
                SeriesEpisode("episode-1-unavailable", seriesId, episodeOneNode, 1, 1, available: false),
                SeriesEpisode("episode-1", seriesId, episodeOneNode, 1, 1),
                SeriesEpisode("special", seriesId, Guid.NewGuid(), 0, 1, special: true),
                SeriesEpisode("other-series", otherSeriesId, Guid.NewGuid(), 1, 1),
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);
        await sut.InitializeAsync();
        var series = sut.SeriesCards.Single(card => card.Id == seriesId);
        sut.SelectSeriesCommand.Execute(series);

        await sut.OpenVideoCommand.ExecuteAsync(series.RegularEpisodes[1]);
        await sut.OpenVideoCommand.ExecuteAsync(series.SpecialFeatures.Single());

        player.OpenedPlaylists.Should().HaveCount(2);
        player.OpenedPlaylists[0].Select(video => video.Id)
            .Should().Equal("episode-1", "episode-2");
        player.OpenedPlaylists[1].Select(video => video.Id)
            .Should().Equal("special");
    }

    [Fact]
    public async Task SeriesCards_DoNotMergeSeasonNodesByLegacyBangumiIdentity()
    {
        var mainSeriesId = Guid.NewGuid();
        var seasonOnlySeriesId = Guid.NewGuid();
        var mainEpisode = SeriesEpisode("season-2-episode-1", mainSeriesId, Guid.NewGuid(), 2, 1);
        mainEpisode.CatalogSeriesTitle = "Mushoku Tensei Isekai Ittara Honki Dasu";
        mainEpisode.MatchCandidates =
        [
            new VideoMatchCandidateSnapshot(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "bangumi",
                "501963",
                "無職転生Ⅲ ～異世界行ったら本気だす～",
                2026,
                0.166,
                0.088,
                "scraped title",
                false,
                DateTimeOffset.UtcNow),
        ];
        var seasonOnlyEpisode = SeriesEpisode(
            "season-3-episode-8",
            seasonOnlySeriesId,
            Guid.NewGuid(),
            3,
            8);
        seasonOnlyEpisode.CatalogSeriesTitle = "無職転生Ⅲ ～異世界行ったら本気だす～";
        seasonOnlyEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["bangumi"] = "501963",
        };

        var sut = CreateSut(videoService: new RecordingVideoLibraryService
        {
            Videos = [mainEpisode, seasonOnlyEpisode],
        });

        await sut.InitializeAsync();

        sut.SeriesCards.Should().HaveCount(2);
        sut.SeriesCards.SelectMany(card => card.RegularEpisodes)
            .Select(item => item.Video.Id)
            .Should().BeEquivalentTo("season-2-episode-1", "season-3-episode-8");
    }

    [Fact]
    public async Task SeriesCards_MergeDifferentAniDbAnimeEntriesOnlyWhenPersistentGroupMatches()
    {
        var firstSeriesId = Guid.NewGuid();
        var secondSeriesId = Guid.NewGuid();
        var firstEpisode = SeriesEpisode("anidb-100-episode", firstSeriesId, Guid.NewGuid(), 1, 1);
        firstEpisode.CatalogSeriesTitle = "Shared franchise first entry";
        firstEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "100",
            ["anidb-group"] = "stable-franchise",
        };
        var secondEpisode = SeriesEpisode("anidb-200-episode", secondSeriesId, Guid.NewGuid(), 2, 1);
        secondEpisode.CatalogSeriesTitle = "Shared franchise second entry";
        secondEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "200",
            ["anidb-group"] = "stable-franchise",
        };
        var sut = CreateSut(videoService: new RecordingVideoLibraryService
        {
            Videos = [firstEpisode, secondEpisode],
        });

        await sut.InitializeAsync();

        sut.SeriesCards.Should().ContainSingle();
        sut.SeriesCards[0].RegularEpisodes.Select(item => item.Video.Id)
            .Should().BeEquivalentTo("anidb-100-episode", "anidb-200-episode");
    }

    [Fact]
    public async Task SeriesCards_KeepDifferentAniDbGroupsSeparateDespiteSharedTmdbTitleAndCandidates()
    {
        var firstSeriesId = Guid.NewGuid();
        var secondSeriesId = Guid.NewGuid();
        var firstEpisode = SeriesEpisode("anidb-100-episode", firstSeriesId, Guid.NewGuid(), 1, 1);
        firstEpisode.CatalogSeriesTitle = "Example Anime 1st Season";
        firstEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "100",
            ["anidb-group"] = "first-group",
            ["tmdb"] = "999",
        };
        firstEpisode.MatchCandidates =
        [
            new VideoMatchCandidateSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), "bangumi", "shared-candidate", "Example Anime",
                2020, 0.99, 0.99, "shared candidate", false, DateTimeOffset.UtcNow),
        ];
        var secondEpisode = SeriesEpisode("anidb-200-episode", secondSeriesId, Guid.NewGuid(), 2, 1);
        secondEpisode.CatalogSeriesTitle = "Example Anime 2nd Season";
        secondEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "200",
            ["anidb-group"] = "second-group",
            ["tmdb"] = "999",
        };
        secondEpisode.MatchCandidates =
        [
            new VideoMatchCandidateSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), "bangumi", "shared-candidate", "Example Anime",
                2020, 0.99, 0.99, "shared candidate", false, DateTimeOffset.UtcNow),
        ];
        var sut = CreateSut(videoService: new RecordingVideoLibraryService
        {
            Videos = [firstEpisode, secondEpisode],
        });

        await sut.InitializeAsync();

        sut.SeriesCards.Should().HaveCount(2);
        sut.SeriesCards.Select(card => card.Id).Should()
            .BeEquivalentTo(new[] { firstSeriesId, secondSeriesId });
    }

    [Fact]
    public async Task SeriesCards_DoNotFallbackToSharedMetadataWhileAniDbGroupIsMissing()
    {
        var firstSeriesId = Guid.NewGuid();
        var secondSeriesId = Guid.NewGuid();
        var firstEpisode = SeriesEpisode("anidb-100-episode", firstSeriesId, Guid.NewGuid(), 1, 1);
        firstEpisode.CatalogSeriesTitle = "Example Anime 1st Season";
        firstEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "100",
            ["tmdb"] = "999",
        };
        var secondEpisode = SeriesEpisode("anidb-200-episode", secondSeriesId, Guid.NewGuid(), 2, 1);
        secondEpisode.CatalogSeriesTitle = "Example Anime 2nd Season";
        secondEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "200",
            ["tmdb"] = "999",
        };
        var sut = CreateSut(videoService: new RecordingVideoLibraryService
        {
            Videos = [firstEpisode, secondEpisode],
        });

        await sut.InitializeAsync();

        sut.SeriesCards.Should().HaveCount(2);
        sut.SeriesCards.Select(card => card.Id).Should()
            .BeEquivalentTo(new[] { firstSeriesId, secondSeriesId });
    }

    [Fact]
    public async Task SeriesCards_MergedSeasonNodesUseTheEarliestRootSeries()
    {
        var rootSeriesId = Guid.NewGuid();
        var seasonFourSeriesId = Guid.NewGuid();
        var rootEpisode = SeriesEpisode("root-episode", rootSeriesId, Guid.NewGuid(), 1, 1);
        rootEpisode.CatalogSeriesTitle = "Re:ゼロから始める異世界生活";
        rootEpisode.CatalogSeriesReleaseYear = 2016;
        rootEpisode.ExternalIds = new Dictionary<string, string>
        {
            ["anidb"] = "11370",
            ["anidb-group"] = "re-zero",
        };
        var seasonFourEpisodes = Enumerable.Range(1, 3).Select(episodeNumber =>
        {
            var episode = SeriesEpisode(
                $"season-4-episode-{episodeNumber}",
                seasonFourSeriesId,
                Guid.NewGuid(),
                4,
                episodeNumber);
            episode.CatalogSeriesTitle = "Re:ゼロから始める異世界生活 4th season";
            episode.CatalogSeriesReleaseYear = 2026;
            episode.ExternalIds = new Dictionary<string, string>
            {
                ["anidb"] = "20000",
                ["anidb-group"] = "re-zero",
            };
            return episode;
        }).ToArray();
        var sut = CreateSut(videoService: new RecordingVideoLibraryService
        {
            Videos = [.. seasonFourEpisodes, rootEpisode],
        });

        await sut.InitializeAsync();

        sut.SeriesCards.Should().ContainSingle();
        var series = sut.SeriesCards[0];
        series.Id.Should().Be(rootSeriesId);
        series.Title.Should().Be("Re:ゼロから始める異世界生活");
        series.MetadataYear.Should().Be(2016);
        series.MetadataIdentity!.ProviderId.Should().Be("anidb");
        series.MetadataIdentity.ProviderItemId.Should().Be("11370");
        series.RegularEpisodes.Should().HaveCount(4);
    }

    [Fact]
    public void NextUp_ExcludesSpecialFeaturesAndCollapsesAssetVersions()
    {
        var seriesId = Guid.NewGuid();
        var episodeOneNode = Guid.NewGuid();
        var watched = SeriesEpisode("episode-1", seriesId, episodeOneNode, 1, 1);
        watched.IsWatched = true;
        var alternateVersion = SeriesEpisode("episode-1-alt", seriesId, episodeOneNode, 1, 1);
        var special = SeriesEpisode("special", seriesId, Guid.NewGuid(), 0, 2, special: true);
        special.AbsoluteEpisodeNumber = 2;
        var next = SeriesEpisode("episode-2", seriesId, Guid.NewGuid(), 1, 2);

        var result = VideoLibraryPageViewModel.BuildNextEpisodeItems(
            [watched, alternateVersion, special, next]);

        result.Select(video => video.Id).Should().Equal("episode-2");
    }

    [Fact]
    public async Task SearchText_FiltersVisibleVideosByTitleFolderCollectionAndTags()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\Show\Episode 1.mkv",
                    SourceFolderPath = @"D:\Anime\Show",
                    CollectionName = "Show",
                    Tags = "anime, japanese",
                },
                new VideoItem
                {
                    Id = "movie-1",
                    Title = "Movie",
                    FilePath = @"D:\Movies\Movie.mkv",
                    SourceFolderPath = @"D:\Movies",
                    CollectionName = "Movies",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SearchText = "japanese";

        sut.Videos.Should().ContainSingle()
            .Which.Video.Id.Should().Be("episode-1");
    }

    [Fact]
    public async Task SelectedSortOption_ProgressSortsHighestProgressFirst()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "low", Title = "Low", FilePath = @"D:\Videos\low.mkv", LastPositionSeconds = 10, DurationSeconds = 100 },
                new VideoItem { Id = "high", Title = "High", FilePath = @"D:\Videos\high.mkv", LastPositionSeconds = 80, DurationSeconds = 100 },
                new VideoItem { Id = "none", Title = "None", FilePath = @"D:\Videos\none.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SelectedSortOption = VideoLibrarySortOption.Progress;

        sut.Videos.Select(video => video.Video.Id).Should().Equal("high", "low", "none");
    }

    [Fact]
    public async Task SelectedLayoutMode_TogglesListAndPosterFlags()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.SelectedLayoutMode = VideoLibraryLayoutMode.Posters;

        sut.IsPosterLayout.Should().BeTrue();
        sut.IsListLayout.Should().BeFalse();
    }

    [Fact]
    public async Task LibraryHeader_IsVisibleOnlyForSearchOrImportViews()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.IsHomeView.Should().BeTrue();
        sut.IsLibraryHeaderVisible.Should().BeFalse();

        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.All));
        sut.IsCatalogSearchVisible.Should().BeTrue();
        sut.IsLibraryHeaderVisible.Should().BeTrue();

        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.Sources));
        sut.IsCatalogSearchVisible.Should().BeFalse();
        sut.IsSourcesView.Should().BeTrue();
        sut.IsLibraryHeaderVisible.Should().BeTrue();

        sut.ToggleMetadataTasksCommand.Execute(null);
        sut.IsMetadataTaskPanelOpen.Should().BeTrue();
        sut.IsMetadataTaskPanelVisible.Should().BeTrue();

        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.Series));
        sut.IsMetadataTaskPanelOpen.Should().BeFalse();
        sut.IsMetadataTaskPanelVisible.Should().BeFalse();
    }

    [Fact]
    public async Task AllVideosFilterHub_TracksAllMovieAnimeFolderCollectionAndTagViews()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        foreach (var view in new[]
                 {
                     VideoLibraryView.All,
                     VideoLibraryView.Movies,
                     VideoLibraryView.Anime,
                     VideoLibraryView.Folders,
                     VideoLibraryView.Collections,
                     VideoLibraryView.Tags,
                 })
        {
            sut.SelectLibraryViewCommand.Execute(view.ToString());
            sut.IsAllVideosFilterView.Should().BeTrue();
        }

        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.Series));
        sut.IsAllVideosFilterView.Should().BeFalse();
    }

    [Fact]
    public async Task ContinueWatchingView_IgnoresNearStartProgress()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "start", Title = "Start", FilePath = @"D:\Videos\start.mkv", LastPositionSeconds = 1.9, DurationSeconds = 2406 },
                new VideoItem { Id = "continue", Title = "Continue", FilePath = @"D:\Videos\continue.mkv", LastPositionSeconds = 10, DurationSeconds = 100 },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.ContinueWatching));

        sut.Videos.Select(video => video.Video.Id).Should().Equal("continue");
    }

    [Fact]
    public async Task SmartCollectionPreview_UsesAllRules()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode", Title = "Umaru 01", FilePath = @"D:\Anime\Umaru 01.mkv", Tags = "anime" },
                new VideoItem { Id = "movie", Title = "Movie", FilePath = @"D:\Movies\Movie.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Umaru";
        sut.SelectedSmartRuleField = VideoSmartRuleField.FileName;
        sut.SmartRuleValueDraft = "umaru";

        sut.SmartCollectionPreviewRows.Select(row => row.Video.Id).Should().Equal("episode");
    }

    [Fact]
    public async Task CreateSmartCollectionCommand_CreatesCollectionAndReloadsFilters()
    {
        var service = new RecordingVideoLibraryService();
        var sut = CreateSut(videoService: service);
        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Anime";
        sut.SelectedSmartRuleField = VideoSmartRuleField.Tag;
        sut.SmartRuleValueDraft = "anime";

        await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

        service.CreatedSmartCollections.Should().ContainSingle()
            .Which.Name.Should().Be("Anime");
    }

    [Fact]
    public async Task CreateSmartCollectionCommand_UsesIsTrueRuleForBoundSubtitle()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "subbed",
                    Title = "Subbed",
                    FilePath = @"D:\Anime\subbed.mkv",
                    SubtitlePath = @"D:\Anime\subbed.ja.srt",
                },
                new VideoItem
                {
                    Id = "raw",
                    Title = "Raw",
                    FilePath = @"D:\Anime\raw.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Subbed";
        sut.SelectedSmartRuleField = VideoSmartRuleField.HasBoundSubtitle;

        sut.SmartCollectionPreviewRows.Select(row => row.Video.Id).Should().Equal("subbed");

        await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

        var rule = service.CreatedSmartCollections.Should().ContainSingle()
            .Which.SmartRules.Should().ContainSingle().Subject;
        rule.Field.Should().Be(VideoSmartRuleField.HasBoundSubtitle);
        rule.Match.Should().Be(VideoSmartRuleMatch.IsTrue);
    }

    [Fact]
    public async Task ToggleFavoriteCommand_UpdatesFavoriteAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.ToggleFavoriteCommand.ExecuteAsync(sut.Videos[0]);

        service.FavoriteUpdates.Should().Equal(("episode-1", true));
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task AddToNewCollectionCommand_PromptsForNameAndCreatesManualCollection()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.PromptTextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync("Watch Later");
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service, dialogService: dialog.Object);

        await sut.InitializeAsync();
        await sut.AddToNewCollectionCommand.ExecuteAsync(sut.Videos[0]);

        service.CreatedManualCollections.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(("Watch Later", new[] { "episode-1" }));
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task RevealFileCommand_UsesFileRevealService()
    {
        var reveal = new RecordingFileRevealService();
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service, fileRevealService: reveal);

        await sut.InitializeAsync();
        await sut.RevealFileCommand.ExecuteAsync(sut.Videos[0]);

        reveal.RevealedPaths.Should().Equal(@"D:\Anime\episode1.mkv");
    }

    [Fact]
    public async Task OpenVideoFromBeginningCommand_OpensTransientZeroProgressVideo()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                    LastPositionSeconds = 120,
                    DurationSeconds = 240,
                },
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        await sut.OpenVideoFromBeginningCommand.ExecuteAsync(sut.Videos[0]);

        player.OpenedVideos.Should().ContainSingle()
            .Which.LastPositionSeconds.Should().Be(0);
        service.MarkedOpenedIds.Should().Equal("episode-1");
        service.ClearedProgressIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateMissingThumbnails_IgnoresPosterArtworkForReloadDecision()
    {
        var posterPath = Path.GetTempFileName();
        try
        {
            var service = new RecordingVideoLibraryService
            {
                Videos =
                [
                    new VideoItem
                    {
                        Id = "poster-backed",
                        Title = "Poster Backed",
                        FilePath = @"D:\Anime\poster-backed.mkv",
                        PosterPath = posterPath,
                    },
                ],
            };
            var thumbnail = new RecordingVideoThumbnailService
            {
                EnsureThumbnail = video => Task.FromResult<string?>(video.PosterPath),
            };
            var sut = CreateSut(videoService: service, thumbnailService: thumbnail);

            await sut.InitializeAsync();
            await thumbnail.WaitForCallsAsync(1, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            service.LoadCount.Should().Be(1);
        }
        finally
        {
            File.Delete(posterPath);
        }
    }

    [Fact]
    public async Task GenerateMissingThumbnails_UpdatesCardWithoutReloadingLibrary()
    {
        var thumbnailPath = Path.GetTempFileName();
        try
        {
            var service = new RecordingVideoLibraryService
            {
                Videos =
                [
                    new VideoItem
                    {
                        Id = "generated",
                        Title = "Generated",
                        FilePath = @"D:\Anime\generated.mkv",
                    },
                ],
            };
            var thumbnail = new RecordingVideoThumbnailService
            {
                EnsureThumbnail = _ => Task.FromResult<string?>(thumbnailPath),
            };
            var sut = CreateSut(videoService: service, thumbnailService: thumbnail);

            await sut.InitializeAsync();
            await thumbnail.WaitForCallsAsync(1, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            service.LoadCount.Should().Be(1);
            sut.Videos.Should().ContainSingle()
                .Which.ArtworkPath.Should().Be(thumbnailPath);
        }
        finally
        {
            File.Delete(thumbnailPath);
        }
    }

    [Fact]
    public void AvailableSmartRuleFields_ExposesSelectableSmartRuleFields()
    {
        var sut = CreateSut();

        sut.AvailableSmartRuleFields.Select(field => field.Value).Should().Equal(
            VideoSmartRuleField.FileName,
            VideoSmartRuleField.ParentFolder,
            VideoSmartRuleField.Path,
            VideoSmartRuleField.Tag,
            VideoSmartRuleField.HasBoundSubtitle,
            VideoSmartRuleField.PlaybackState);
        sut.AvailableSmartRuleFields.Should().OnlyContain(field => !string.IsNullOrWhiteSpace(field.DisplayName));
    }

    [Fact]
    public async Task ScanFolderCommand_PicksFolderAndScans()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.OpenFolderPickerAsync())
            .ReturnsAsync(@"D:\Anime");
        var service = new RecordingVideoLibraryService();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            videoService: service,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ScanFolderCommand.ExecuteAsync(null);

        service.ScannedFolders.Should().Equal(@"D:\Anime");
        notification.Verify(
            service => service.ShowSuccess(
                It.Is<string>(message => message.Contains('0')),
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
    }

    [Fact]
    public async Task MarkWatchedCommand_MarksVideoAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode-1", Title = "Episode 1", FilePath = @"D:\Anime\episode1.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.MarkWatchedCommand.ExecuteAsync(sut.Videos[0]);

        service.MarkedWatchedIds.Should().Equal("episode-1");
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task ClearProgressCommand_ClearsVideoProgressAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                    LastPositionSeconds = 20,
                    DurationSeconds = 100,
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.ClearProgressCommand.ExecuteAsync(sut.Videos[0]);

        service.ClearedProgressIds.Should().Equal("episode-1");
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task PlayerLibraryChanged_ReloadsVisibleVideos()
    {
        var service = new RecordingVideoLibraryService();
        service.VideoResponses.Enqueue(
        [
            new VideoItem
            {
                Id = "episode-1",
                Title = "Episode 1",
                FilePath = @"D:\Anime\episode1.mkv",
                LastPositionSeconds = 2,
                DurationSeconds = 100,
            },
        ]);
        service.VideoResponses.Enqueue(
        [
            new VideoItem
            {
                Id = "episode-1",
                Title = "Episode 1",
                FilePath = @"D:\Anime\episode1.mkv",
                LastPositionSeconds = 76,
                DurationSeconds = 100,
            },
        ]);
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        player.RaiseLibraryChanged();

        service.LoadCount.Should().Be(2);
        sut.Videos.Should().ContainSingle()
            .Which.Video.LastPositionSeconds.Should().Be(76);
    }

    [Fact]
    public async Task PlayerLibraryChanged_PreservesEnrichedSeriesDetailsAndSelectedSeason()
    {
        var seriesId = Guid.NewGuid();
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                SeriesEpisode("season-1-episode-1", seriesId, Guid.NewGuid(), 1, 1),
                SeriesEpisode("season-2-episode-8", seriesId, Guid.NewGuid(), 2, 8),
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);
        await sut.InitializeAsync();
        var originalSeries = sut.SeriesCards.Single();
        sut.SelectSeriesCommand.Execute(originalSeries);
        originalSeries.ApplyRemoteSeasons(
        [
            new VideoDiscoverySeason(0, "Special Edition", null, null, 3, null, []),
            new VideoDiscoverySeason(1, "First Cour", null, null, 11, null, []),
            new VideoDiscoverySeason(2, "Second Cour", null, null, 24, null, []),
        ]);
        originalSeries.SelectSeason(2);

        player.RaiseLibraryChanged();

        sut.SelectedSeries.Should().NotBeNull().And.NotBeSameAs(originalSeries);
        sut.SelectedSeries!.Seasons.Select(season => season.Title)
            .Should().Equal("Special Edition", "First Cour", "Second Cour");
        sut.SelectedSeries.Seasons.Select(season => season.EpisodeCount)
            .Should().Equal(3, 11, 24);
        sut.SelectedSeries.SelectedSeason!.SeasonNumber.Should().Be(2);
        sut.SelectedSeries.SelectedEpisodeSlots.Should().HaveCount(24);
    }

    [Fact]
    public async Task SelectionCommands_ApplyBatchPlaybackChanges()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "one", Title = "One", FilePath = @"D:\Videos\one.mkv" },
                new VideoItem { Id = "two", Title = "Two", FilePath = @"D:\Videos\two.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);
        await sut.InitializeAsync();

        sut.ToggleVideoSelectionCommand.Execute(sut.Videos[0]);
        sut.ToggleVideoSelectionCommand.Execute(sut.Videos[1]);
        await sut.MarkSelectedWatchedCommand.ExecuteAsync(null);
        await sut.ClearSelectedProgressCommand.ExecuteAsync(null);

        sut.SelectedVideoCount.Should().Be(2);
        service.MarkedWatchedIds.Should().BeEquivalentTo("one", "two");
        service.ClearedProgressIds.Should().BeEquivalentTo("one", "two");
    }

    [Fact]
    public async Task SaveVideoDetailsCommand_PersistsTitleTagsAndBoundSubtitle()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "one", Title = "One", FilePath = @"D:\Videos\one.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);
        await sut.InitializeAsync();
        sut.SelectVideoDetailsCommand.Execute(sut.Videos[0]);
        sut.SelectedVideoTitleDraft = "Display One";
        sut.SelectedVideoTagsDraft = "anime, japanese\nstudy";
        sut.SelectedVideoSubtitlePath = @"D:\Subs\one.srt";

        await sut.SaveVideoDetailsCommand.ExecuteAsync(null);

        var update = service.DetailUpdates.Should().ContainSingle().Subject;
        update.VideoId.Should().Be("one");
        update.Title.Should().Be("Display One");
        update.Tags.Should().Equal("anime", "japanese", "study");
        update.SubtitlePath.Should().Be(@"D:\Subs\one.srt");
    }

    [Fact]
    public async Task SmartCollectionEditor_PersistsMultipleRulesWhenEditing()
    {
        var existing = new VideoCollection
        {
            Id = "smart",
            Name = "Anime",
            Kind = VideoCollectionKind.Smart,
            SmartRules = [new VideoSmartRule(VideoSmartRuleField.Tag, "anime")],
        };
        var service = new RecordingVideoLibraryService { Collections = [existing] };
        var sut = CreateSut(videoService: service);
        await sut.InitializeAsync();
        var row = sut.CollectionFilters.Single(filter => filter.Key == "smart");

        sut.BeginEditSmartCollection(row).Should().BeTrue();
        sut.AddSmartRuleCommand.Execute(null);
        sut.SmartRuleDrafts[1].Field = VideoSmartRuleField.HasBoundSubtitle;
        sut.SmartRuleDrafts[1].Match = VideoSmartRuleMatch.IsTrue;
        sut.SmartCollectionNameDraft = "Subbed anime";
        await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

        service.UpdatedSmartCollections.Should().ContainSingle();
        service.UpdatedSmartCollections[0].Rules.Should().HaveCount(2);
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_WhenConfirmationIsRejected_DoesNotClear()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(false);
        var metadata = new Mock<IVideoMetadataCoordinator>();
        var videoService = new RecordingVideoLibraryService();
        var sut = CreateSut(
            videoService: videoService,
            dialogService: dialog.Object,
            metadataCoordinator: metadata.Object);

        await sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);

        metadata.Verify(
            service => service.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        videoService.LoadCount.Should().Be(0);
        sut.IsClearingScrapeRecords.Should().BeFalse();
    }

    [Fact]
    public void ScanAndRefreshCommands_AreDisabledWhileScrapeRecordsAreClearing()
    {
        var sut = CreateSut(
            metadataCoordinator: Mock.Of<IVideoMetadataCoordinator>(),
            scanCoordinator: Mock.Of<IVideoLibraryScanCoordinator>());
        var task = new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Failed,
                1, 0, 1, 1, "failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            "Anime");
        var source = new VideoLibrarySourceSummary(new VideoLibrarySource
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Anime",
            FolderPath = @"C:\Anime",
        }, 0, 0, 0);

        sut.RetryMetadataTaskCommand.CanExecute(task).Should().BeTrue();
        sut.RetryFailedMetadataTasksCommand.CanExecute(null).Should().BeTrue();
        sut.ScanFolderCommand.CanExecute(null).Should().BeTrue();
        sut.RefreshAllSourcesCommand.CanExecute(null).Should().BeTrue();
        sut.RefreshSourceCommand.CanExecute(source).Should().BeTrue();
        sut.RefreshSourceMetadataCommand.CanExecute(source).Should().BeTrue();
        sut.RefreshSelectedMetadataCommand.CanExecute(null).Should().BeTrue();
        sut.FullScanSourceCommand.CanExecute(source).Should().BeTrue();
        sut.ResumeSourceScanCommand.CanExecute(source).Should().BeTrue();
        sut.RemoveMissingVideosCommand.CanExecute(null).Should().BeTrue();

        sut.IsClearingScrapeRecords = true;

        sut.RetryMetadataTaskCommand.CanExecute(task).Should().BeFalse();
        sut.RetryFailedMetadataTasksCommand.CanExecute(null).Should().BeFalse();
        sut.ScanFolderCommand.CanExecute(null).Should().BeFalse();
        sut.RefreshAllSourcesCommand.CanExecute(null).Should().BeFalse();
        sut.RefreshSourceCommand.CanExecute(source).Should().BeFalse();
        sut.RefreshSourceMetadataCommand.CanExecute(source).Should().BeFalse();
        sut.RefreshSelectedMetadataCommand.CanExecute(null).Should().BeFalse();
        sut.FullScanSourceCommand.CanExecute(source).Should().BeFalse();
        sut.ResumeSourceScanCommand.CanExecute(source).Should().BeFalse();
        sut.RemoveMissingVideosCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ScanFolderCommand_ClearBeginsWhilePickerIsOpen_DoesNotStartScan()
    {
        var ct = TestContext.Current.CancellationToken;
        var pickerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePicker = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(item => item.OpenFolderPickerAsync())
            .Returns(async () =>
            {
                pickerStarted.TrySetResult();
                return await releasePicker.Task;
            });
        var video = new Mock<IVideoLibraryService>();
        var sut = CreateSut(videoService: video.Object, dialogService: dialog.Object);

        var scan = sut.ScanFolderCommand.ExecuteAsync(null);
        await pickerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.IsClearingScrapeRecords = true;
        releasePicker.TrySetResult(@"C:\Anime");
        await scan.WaitAsync(TimeSpan.FromSeconds(2), ct);

        video.Verify(item => item.ScanFolderAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScrapeAllMetadataCommand_ClearBeginsWhileConsentIsOpen_DoesNotQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var consentStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsent = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(item => item.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async () =>
            {
                consentStarted.TrySetResult();
                return await releaseConsent.Task;
            });
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(item => item.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = false },
            },
        });
        settings.Setup(item => item.SaveAsync()).Returns(Task.CompletedTask);
        var metadata = new Mock<IVideoMetadataCoordinator>();
        var sut = CreateSut(
            dialogService: dialog.Object,
            metadataCoordinator: metadata.Object,
            settingsService: settings.Object);

        var scrape = sut.ScrapeAllMetadataCommand.ExecuteAsync(null);
        await consentStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.IsClearingScrapeRecords = true;
        releaseConsent.TrySetResult(true);
        await scrape.WaitAsync(TimeSpan.FromSeconds(2), ct);

        metadata.Verify(item => item.QueueAllSourcesAsync(
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_ClearsStaleTaskUiAndReloadsLibrary()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata
            .Setup(service => service.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var notification = new Mock<INotificationService>();
        var videoService = new RecordingVideoLibraryService();
        var sut = CreateSut(
            videoService: videoService,
            dialogService: dialog.Object,
            notificationService: notification.Object,
            metadataCoordinator: metadata.Object);
        sut.MetadataTasks.Add(new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Completed,
                1, 1, 1, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            "Anime"));
        sut.IsMetadataTaskPanelOpen = true;
        sut.HasBackgroundMetadataTask = true;
        sut.BackgroundMetadataProgress = 0.5;
        sut.BackgroundMetadataText = "old background task";
        sut.HasActiveMetadataRefresh = true;
        sut.IsMetadataRefreshIndeterminate = true;
        sut.MetadataRefreshProgress = 0.5;
        sut.MetadataRefreshText = "old direct task";

        await sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);

        metadata.Verify(
            service => service.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        sut.MetadataTasks.Should().BeEmpty();
        sut.IsMetadataTaskPanelOpen.Should().BeFalse();
        sut.HasBackgroundMetadataTask.Should().BeFalse();
        sut.BackgroundMetadataProgress.Should().Be(0);
        sut.BackgroundMetadataText.Should().BeEmpty();
        sut.HasActiveMetadataRefresh.Should().BeFalse();
        sut.IsMetadataRefreshIndeterminate.Should().BeFalse();
        sut.MetadataRefreshProgress.Should().Be(0);
        sut.MetadataRefreshText.Should().BeEmpty();
        sut.IsClearingScrapeRecords.Should().BeFalse();
        videoService.LoadCount.Should().Be(1);
        notification.Verify(
            service => service.ShowSuccess(
                It.Is<string>(message => !string.IsNullOrWhiteSpace(message)),
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_WhenBackendFails_ReportsErrorAndLeavesCommandUsable()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        var metadata = new Mock<IVideoMetadataCoordinator>();
        var survivingJob = new VideoMetadataTaskSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            VideoCatalogJobState.Interrupted,
            3,
            10,
            1,
            2,
            "surviving repository state",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        metadata
            .Setup(service => service.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("clear failed"));
        metadata.Setup(service => service.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([survivingJob]);
        var notification = new Mock<INotificationService>();
        var videoService = new RecordingVideoLibraryService();
        var sut = CreateSut(
            videoService: videoService,
            dialogService: dialog.Object,
            notificationService: notification.Object,
            metadataCoordinator: metadata.Object);
        sut.MetadataTasks.Add(new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Running,
                1, 10, 0, 1, "stale ui", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            "Old"));
        sut.HasBackgroundMetadataTask = true;
        sut.BackgroundMetadataText = "old progress";

        await sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);

        sut.IsClearingScrapeRecords.Should().BeFalse();
        sut.ClearAllScrapeRecordsCommand.CanExecute(null).Should().BeTrue();
        videoService.LoadCount.Should().Be(1);
        sut.MetadataTasks.Should().ContainSingle(item => item.JobId == survivingJob.JobId);
        sut.HasBackgroundMetadataTask.Should().BeFalse();
        sut.BackgroundMetadataText.Should().BeEmpty();
        metadata.Verify(service => service.GetTaskHistoryAsync(
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        notification.Verify(
            service => service.ShowError(
                "clear failed",
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_WhenReconciliationAlsoFails_ReportsOriginalClearError()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata.Setup(service => service.ClearAllScrapeRecordsAsync(
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("clear failed"));
        metadata.Setup(service => service.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var video = new Mock<IVideoLibraryService>();
        video.Setup(service => service.GetVideosAsync(
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("reload failed"));
        video.Setup(service => service.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoCollection>>.Success([]));
        video.Setup(service => service.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoLibrarySource>>.Success([]));
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            videoService: video.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object,
            metadataCoordinator: metadata.Object);

        await sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);

        notification.Verify(service => service.ShowError(
            "clear failed", It.IsAny<string>()), Times.Once);
        notification.Verify(service => service.ShowError(
            "reload failed", It.IsAny<string>()), Times.Never);
        notification.Verify(service => service.ShowSuccess(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        sut.IsClearingScrapeRecords.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_IgnoresQueuedAndInFlightOldGenerationProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        var clearStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClear = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyCollection<VideoMetadataBatchProgress> activeProgress = [];
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata.SetupGet(service => service.ActiveBatchProgress)
            .Returns(() => activeProgress);
        metadata.Setup(service => service.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        metadata.Setup(service => service.ClearAllScrapeRecordsAsync(
                It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                clearStarted.TrySetResult(true);
                await releaseClear.Task;
            });
        var sut = CreateSut(
            videoService: new RecordingVideoLibraryService(),
            dialogService: dialog.Object,
            metadataCoordinator: metadata.Object);
        var uiContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            await sut.InitializeAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var batch = new VideoMetadataBatchProgress(
            Guid.NewGuid(), sourceId, VideoCatalogJobState.Running,
            1, 10, 0, 1, assetId);
        activeProgress = [batch];
        metadata.Raise(service => service.BatchProgressChanged += null, metadata.Object, batch);

        var clear = sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);
        await clearStarted.Task.WaitAsync(ct);
        metadata.Raise(service => service.ProgressChanged += null, metadata.Object,
            new VideoMetadataRefreshProgress(
                assetId, VideoMetadataRefreshStage.Artwork, 0, 1, "tmdb"));
        metadata.Raise(service => service.BatchProgressChanged += null, metadata.Object, batch);
        releaseClear.TrySetResult(true);
        await clear;
        uiContext.Drain();

        sut.MetadataTasks.Should().BeEmpty();
        sut.HasBackgroundMetadataTask.Should().BeFalse();
        sut.BackgroundMetadataText.Should().BeEmpty();
        sut.HasActiveMetadataRefresh.Should().BeFalse();
        sut.MetadataRefreshText.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAllScrapeRecordsCommand_InvalidatesPendingAutomaticQueuesFromAllRefreshPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new VideoLibrarySource
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Anime",
            FolderPath = @"C:\Anime",
            MediaType = VideoLibraryMediaType.Anime,
        };
        var videoService = new Mock<IVideoLibraryService>();
        videoService.Setup(item => item.GetVideosAsync(
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoItem>>.Success([]));
        videoService.Setup(item => item.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoCollection>>.Success([]));
        videoService.Setup(item => item.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoLibrarySource>>.Success([source]));
        var allStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allRelease = new TaskCompletionSource<Result<IReadOnlyList<VideoSourceRefreshResult>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        videoService.Setup(item => item.RefreshAllSourcesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => allStarted.TrySetResult(true))
            .Returns(allRelease.Task);
        var sourceStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceRelease = new TaskCompletionSource<Result<VideoSourceRefreshResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        videoService.Setup(item => item.RefreshSourceAsync(source.Id, It.IsAny<CancellationToken>()))
            .Callback(() => sourceStarted.TrySetResult(true))
            .Returns(sourceRelease.Task);
        var scanStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = new Mock<IVideoLibraryScanCoordinator>();
        scan.Setup(item => item.ScanAllAsync(false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                scanStarted.TrySetResult(true);
                await scanRelease.Task;
            });
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata.SetupGet(item => item.ActiveBatchProgress).Returns([]);
        metadata.Setup(item => item.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        metadata.Setup(item => item.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(item => item.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var dialog = new Mock<IDialogService>();
        dialog.Setup(item => item.ConfirmAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var sut = CreateSut(
            videoService: videoService.Object,
            dialogService: dialog.Object,
            metadataCoordinator: metadata.Object,
            scanCoordinator: scan.Object,
            settingsService: settings.Object);
        await sut.InitializeAsync();
        await scanStarted.Task.WaitAsync(ct);
        var refreshAll = sut.RefreshAllSourcesCommand.ExecuteAsync(null);
        var summary = new VideoLibrarySourceSummary(source, 0, 0, 0);
        var refreshSource = sut.RefreshSourceCommand.ExecuteAsync(summary);
        await Task.WhenAll(allStarted.Task, sourceStarted.Task).WaitAsync(ct);

        await sut.ClearAllScrapeRecordsCommand.ExecuteAsync(null);
        allRelease.TrySetResult(Result<IReadOnlyList<VideoSourceRefreshResult>>.Success([]));
        sourceRelease.TrySetResult(Result<VideoSourceRefreshResult>.Success(
            new VideoSourceRefreshResult(source, 0, [])));
        scanRelease.TrySetResult(true);
        await Task.WhenAll(refreshAll, refreshSource).WaitAsync(ct);
        await Task.Delay(50, ct);

        metadata.Verify(item => item.QueueAllSourcesAsync(
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        metadata.Verify(item => item.QueueSourceRefreshAsync(
            It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompletedSingleAssetMetadataRefresh_ReloadsAnOpenVideoDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new RecordingVideoLibraryService();
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata.SetupGet(item => item.ActiveBatchProgress).Returns([]);
        metadata.Setup(item => item.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut(
            videoService: service,
            metadataCoordinator: metadata.Object);
        await sut.InitializeAsync();
        sut.SelectedVideo = new VideoItemViewModel(new VideoItem
        {
            Id = "selected",
            Title = "Selected",
            FilePath = @"C:\Anime\selected.mkv",
        });

        metadata.Raise(item => item.ProgressChanged += null, metadata.Object,
            new VideoMetadataRefreshProgress(
                Guid.NewGuid(), VideoMetadataRefreshStage.Completed, 1, 1, "anidb"));

        for (var attempt = 0; attempt < 50 && service.LoadCount < 2; attempt++)
            await Task.Delay(10, ct);
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task CompletedSingleAssetMetadataRefresh_DoesNotRebuildHomeSections()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new RecordingVideoLibraryService();
        var metadata = new Mock<IVideoMetadataCoordinator>();
        metadata.SetupGet(item => item.ActiveBatchProgress).Returns([]);
        metadata.Setup(item => item.GetTaskHistoryAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = CreateSut(
            videoService: service,
            metadataCoordinator: metadata.Object);
        await sut.InitializeAsync();

        metadata.Raise(item => item.ProgressChanged += null, metadata.Object,
            new VideoMetadataRefreshProgress(
                Guid.NewGuid(), VideoMetadataRefreshStage.Completed, 1, 1, "anidb"));

        await Task.Delay(50, ct);
        service.LoadCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRefreshLoads_LateCanceledLoadDoesNotOverwriteNewestSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new VideoLibrarySource
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "Anime",
            FolderPath = @"C:\Anime",
            MediaType = VideoLibraryMediaType.Anime,
        };
        var staleLoadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleLoad = new TaskCompletionSource<Result<IReadOnlyList<VideoItem>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var getVideosCall = 0;
        var videoService = new Mock<IVideoLibraryService>();
        videoService.Setup(item => item.GetVideosAsync(
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string? _, CancellationToken _) =>
            {
                return Interlocked.Increment(ref getVideosCall) switch
                {
                    1 => Task.FromResult(Result<IReadOnlyList<VideoItem>>.Success([])),
                    2 => WaitForStaleLoadAsync(),
                    _ => Task.FromResult(Result<IReadOnlyList<VideoItem>>.Success(
                    [
                        new VideoItem
                        {
                            Id = "newest",
                            Title = "Newest",
                            FilePath = @"C:\Anime\newest.mkv",
                        },
                    ])),
                };

                Task<Result<IReadOnlyList<VideoItem>>> WaitForStaleLoadAsync()
                {
                    staleLoadStarted.TrySetResult();
                    return releaseStaleLoad.Task;
                }
            });
        videoService.Setup(item => item.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoCollection>>.Success([]));
        videoService.Setup(item => item.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoLibrarySource>>.Success([source]));
        videoService.Setup(item => item.RefreshAllSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoSourceRefreshResult>>.Success([]));
        videoService.Setup(item => item.RefreshSourceAsync(
                source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoSourceRefreshResult>.Success(
                new VideoSourceRefreshResult(source, 0, [])));
        var sut = CreateSut(videoService: videoService.Object);
        await sut.InitializeAsync();

        var staleRefresh = sut.RefreshAllSourcesCommand.ExecuteAsync(null);
        await staleLoadStarted.Task.WaitAsync(ct);
        var newestRefresh = sut.RefreshSourceCommand.ExecuteAsync(
            new VideoLibrarySourceSummary(source, 0, 0, 0));
        await newestRefresh.WaitAsync(ct);

        sut.Videos.Select(item => item.Video.Id).Should().Equal("newest");
        sut.IsContentLoading.Should().BeFalse();

        releaseStaleLoad.TrySetResult(Result<IReadOnlyList<VideoItem>>.Success(
        [
            new VideoItem
            {
                Id = "stale",
                Title = "Stale",
                FilePath = @"C:\Anime\stale.mkv",
            },
        ]));
        await staleRefresh.WaitAsync(ct);

        sut.Videos.Select(item => item.Video.Id).Should().Equal("newest");
        sut.IsContentLoading.Should().BeFalse();
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Enqueue((callback, state));

        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_callbacks.TryDequeue(out var work))
                    work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private static VideoLibraryPageViewModel CreateSut(
        IVideoLibraryService? videoService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        IVideoPlayerWindowService? playerService = null,
        IVideoThumbnailService? thumbnailService = null,
        IFileRevealService? fileRevealService = null,
        IVideoMetadataCoordinator? metadataCoordinator = null,
        IVideoLibraryScanCoordinator? scanCoordinator = null,
        ISettingsService? settingsService = null)
    {
        return new VideoLibraryPageViewModel(
            videoService ?? new RecordingVideoLibraryService(),
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            playerService ?? new RecordingVideoPlayerWindowService(),
            thumbnailService ?? new RecordingVideoThumbnailService(),
            fileRevealService ?? new RecordingFileRevealService(),
            scanCoordinator,
            metadataCoordinator,
            settingsService);
    }

    private static VideoItem SeriesEpisode(
        string id,
        Guid seriesId,
        Guid nodeId,
        int season,
        int episode,
        bool available = true,
        bool special = false) => new()
    {
        Id = id,
        Title = id,
        FilePath = $@"D:\Anime\{id}.mkv",
        CatalogSeriesNodeId = seriesId,
        CatalogSeriesTitle = seriesId.ToString("D"),
        CatalogNodeId = nodeId,
        CatalogNodeKind = VideoCatalogNodeKind.Episode,
        SeasonNumber = season,
        EpisodeNumber = episode,
        IsSpecialEpisode = special,
        IsAvailable = available,
        LibraryMediaType = VideoLibraryMediaType.Auto,
    };

    private sealed class RecordingVideoLibraryService : IVideoLibraryService
    {
        public IReadOnlyList<VideoItem> Videos { get; init; } = [];
        public IReadOnlyList<VideoCollection> Collections { get; init; } = [];
        public Queue<IReadOnlyList<VideoItem>> VideoResponses { get; } = [];
        public List<string> ImportedPaths { get; } = [];
        public List<string> ScannedFolders { get; } = [];
        public List<string> MarkedOpenedIds { get; } = [];
        public List<string> MarkedWatchedIds { get; } = [];
        public List<string> ClearedProgressIds { get; } = [];
        public List<(string VideoId, bool IsFavorite)> FavoriteUpdates { get; } = [];
        public List<VideoCollection> CreatedSmartCollections { get; } = [];
        public List<(string Name, IReadOnlyList<string> VideoIds)> CreatedManualCollections { get; } = [];
        public List<(string VideoId, string Title, IReadOnlyList<string> Tags, string? SubtitlePath)> DetailUpdates { get; } = [];
        public List<(VideoCollection Collection, string Name, IReadOnlyList<VideoSmartRule> Rules)> UpdatedSmartCollections { get; } = [];
        public int LoadCount { get; private set; }

        public Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
            string? queryText = null,
            CancellationToken ct = default)
        {
            LoadCount++;
            var videos = VideoResponses.Count > 0 ? VideoResponses.Dequeue() : Videos;
            return Task.FromResult(Result<IReadOnlyList<VideoItem>>.Success(videos));
        }

        public Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<VideoCollection>>.Success(Collections));

        public Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default)
        {
            ImportedPaths.Add(filePath);
            return Task.FromResult(Result<VideoItem>.Success(new VideoItem
            {
                Id = Path.GetFileNameWithoutExtension(filePath),
                Title = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
            }));
        }

        public Task<Result<VideoFolderScanResult>> ScanFolderAsync(
            string folderPath,
            CancellationToken ct = default)
        {
            ScannedFolders.Add(folderPath);
            return Task.FromResult(Result<VideoFolderScanResult>.Success(
                new VideoFolderScanResult(0, [])));
        }

        public Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(Result<VideoItem?>.Success(Videos.FirstOrDefault(video => video.Id == videoId)));

        public Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default)
        {
            MarkedOpenedIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateVideoDetailsAsync(
            string videoId,
            string title,
            IReadOnlyList<string> tags,
            string? subtitlePath,
            CancellationToken ct = default)
        {
            DetailUpdates.Add((videoId, title, tags, subtitlePath));
            return Task.FromResult(Result.Success());
        }

        public Task<Result<VideoCollection>> CreateSmartCollectionAsync(
            string name,
            IReadOnlyList<VideoSmartRule> rules,
            CancellationToken ct = default)
        {
            var collection = new VideoCollection
            {
                Name = name,
                Kind = VideoCollectionKind.Smart,
                SmartRules = rules,
            };
            CreatedSmartCollections.Add(collection);
            return Task.FromResult(Result<VideoCollection>.Success(collection));
        }

        public Task<Result<VideoCollection>> CreateManualCollectionAsync(
            string name,
            IReadOnlyList<string> videoIds,
            CancellationToken ct = default)
        {
            CreatedManualCollections.Add((name, videoIds));
            return Task.FromResult(Result<VideoCollection>.Success(new VideoCollection
            {
                Name = name,
                Kind = VideoCollectionKind.Manual,
                ItemIds = videoIds.ToList(),
            }));
        }

        public Task<Result> DeleteCollectionAsync(string collectionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<VideoCollection>> UpdateSmartCollectionAsync(
            VideoCollection collection,
            string name,
            IReadOnlyList<VideoSmartRule> rules,
            CancellationToken ct = default)
        {
            UpdatedSmartCollections.Add((collection, name, rules));
            collection.Name = name;
            collection.SmartRules = rules;
            return Task.FromResult(Result<VideoCollection>.Success(collection));
        }

        public Task<Result> SetFavoriteAsync(
            string videoId,
            bool isFavorite,
            CancellationToken ct = default)
        {
            FavoriteUpdates.Add((videoId, isFavorite));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SaveProgressAsync(
            string videoId,
            double positionSeconds,
            double durationSeconds,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SavePlaybackStateAsync(
            string videoId,
            VideoPlaybackState state,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> MarkWatchedAsync(
            string videoId,
            CancellationToken ct = default)
        {
            MarkedWatchedIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearProgressAsync(
            string videoId,
            CancellationToken ct = default)
        {
            ClearedProgressIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetVideoProfileAsync(
            string videoId,
            string? profileId,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class RecordingVideoThumbnailService : IVideoThumbnailService
    {
        private readonly TaskCompletionSource<int> _calls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Func<VideoItem, Task<string?>>? EnsureThumbnail { get; init; }

        public Task<string?> EnsureThumbnailAsync(
            VideoItem video,
            bool generateIfMissing = true,
            CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            _calls.TrySetResult(count);
            return EnsureThumbnail?.Invoke(video) ?? Task.FromResult(video.ThumbnailPath);
        }

        public async Task WaitForCallsAsync(int expectedCount, CancellationToken ct)
        {
            while (Volatile.Read(ref _callCount) < expectedCount)
            {
                await _calls.Task.WaitAsync(ct);
            }
        }

        public void Suspend()
        {
        }

        public void Resume()
        {
        }
    }

    private sealed class RecordingVideoPlayerWindowService : IVideoPlayerWindowService
    {
        public event EventHandler? LibraryChanged;

        public List<VideoItem> OpenedVideos { get; } = [];
        public List<IReadOnlyList<VideoItem>> OpenedPlaylists { get; } = [];

        public void RaiseLibraryChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);

        public Task OpenAsync(VideoItem video, CancellationToken ct = default)
        {
            OpenedVideos.Add(video);
            OpenedPlaylists.Add([video]);
            return Task.CompletedTask;
        }

        public Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
        {
            OpenedVideos.Add(video);
            OpenedPlaylists.Add(playlist);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFileRevealService : IFileRevealService
    {
        public List<string> RevealedPaths { get; } = [];

        public Task<Result> RevealInFileExplorerAsync(string filePath, CancellationToken ct = default)
        {
            RevealedPaths.Add(filePath);
            return Task.FromResult(Result.Success());
        }
    }
}
