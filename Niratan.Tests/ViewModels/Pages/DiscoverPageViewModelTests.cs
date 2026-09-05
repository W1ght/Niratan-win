using System.Collections.Immutable;
using FluentAssertions;
using Moq;
using Niratan.Enums;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;
using Niratan.Views.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class DiscoverPageViewModelTests
{
    [Fact]
    public void Navigation_target_preserves_feed_artwork_urls_when_identity_omits_them()
    {
        var item = new VideoDiscoveryItem(
            CreateIdentity() with { PosterUrl = null, BackdropUrl = null },
            null,
            null,
            null,
            "https://image.example/poster.jpg",
            "https://image.example/backdrop.jpg",
            "C:\\cache\\poster.jpg",
            "C:\\cache\\backdrop.jpg");

        var target = VideoDiscoveryNavigationTarget.FromItem(item);

        target.Identity.PosterUrl.Should().Be(item.PosterUrl);
        target.Identity.BackdropUrl.Should().Be(item.BackdropUrl);
        target.Artwork.PosterPath.Should().Be(item.LocalPosterPath);
        target.Artwork.BackdropPath.Should().Be(item.LocalBackdropPath);
    }

    [Fact]
    public async Task DefaultRecommendationsUseAggregateSourcesAndPreserveCanonicalTitles()
    {
        const string romaji = "Kimi no Na wa.";
        const string english = "Your Name.";
        const string native = "君の名は。";
        var identity = CreateIdentity() with
        {
            ProviderId = "tmdb",
            ProviderItemId = "372058",
            MediaKind = VideoMetadataMediaKind.Movie,
            Title = romaji,
            OriginalTitle = native,
            Aliases = [english, native],
            ExternalIds = ImmutableDictionary<string, string>.Empty
                .Add("tmdb", "372058")
                .Add("anilist", "21519"),
        };
        var discovery = new Mock<IVideoDiscoveryService>();
        discovery.Setup(service => service.GetAggregatedRecommendationsAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoDiscoveryPage>>.Success([
                new VideoDiscoveryPage(
                    "aggregate",
                    "trending",
                    1,
                    1,
                    [new VideoDiscoveryItem(identity, null, null, null, null, null)]),
            ]));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                ExploreProviderOrder = ["tmdb"],
            },
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings
                {
                    AniListEnabled = true,
                    TmdbEnabled = true,
                },
            },
        });
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());

        await viewModel.InitializeAsync();

        discovery.Verify(service => service.GetAggregatedRecommendationsAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist", "tmdb" })),
            It.IsAny<CancellationToken>()), Times.Once);
        discovery.Verify(service => service.GetPageAsync(
            It.IsAny<string>(),
            It.IsAny<VideoDiscoveryRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        discovery.Verify(service => service.GetFeeds(
            It.IsAny<string>(),
            It.IsAny<VideoDiscoveryFeedKind>()), Times.Never);
        var card = viewModel.RecommendationSections.Should().ContainSingle()
            .Which.Items.Should().ContainSingle().Subject;
        card.Identity.ProviderId.Should().Be("tmdb");
        card.Identity.MediaKind.Should().Be(VideoMetadataMediaKind.Movie);
        card.Title.Should().Be(romaji);
        card.Identity.OriginalTitle.Should().Be(native);
        card.Identity.Aliases.Should().Contain(english);
        card.Identity.ExternalIds.Should().Contain("anilist", "21519");
    }

    [Fact]
    public async Task AggregateOperationsUseMetadataEnabledSourcesInsteadOfLegacyProviderOrder()
    {
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.SearchAggregatedAsync(
                It.IsAny<IReadOnlyList<string>>(),
                "frieren",
                VideoDiscoverySearchCategory.All,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoDiscoveryPage>.Success(CreateSearchPage("frieren")));
        discovery.Setup(service => service.GetAggregatedPageAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<VideoDiscoveryAggregateRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoDiscoveryPage>.Success(CreateBrowsePage("browse")));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                ExploreProviderOrder = ["bangumi", "tmdb", "anilist"],
            },
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings
                {
                    AniListEnabled = true,
                    TmdbEnabled = false,
                },
            },
        });
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());

        await viewModel.InitializeAsync();
        viewModel.SearchText = "frieren";
        await viewModel.SearchVideosCommand.ExecuteAsync(null);
        await viewModel.ApplyFiltersCommand.ExecuteAsync(null);

        discovery.Verify(service => service.SearchAggregatedAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist" })),
            "frieren",
            VideoDiscoverySearchCategory.All,
            It.IsAny<CancellationToken>()), Times.Once);
        discovery.Verify(service => service.GetAggregatedPageAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist" })),
            It.Is<VideoDiscoveryAggregateRequest>(request => request.Page == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        discovery.Verify(service => service.GetFeeds(
            "bangumi",
            It.IsAny<VideoDiscoveryFeedKind>()), Times.Never);
    }

    [Fact]
    public async Task AggregatedSearchAlwaysUsesAllKindsAndProjectsPartialWarning()
    {
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.SearchAggregatedAsync(
                It.IsAny<IReadOnlyList<string>>(),
                "frieren",
                VideoDiscoverySearchCategory.All,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoDiscoveryPage>.Success(new VideoDiscoveryPage(
                "aggregate",
                "search",
                1,
                1,
                [new VideoDiscoveryItem(
                    CreateIdentity() with
                    {
                        ProviderId = "anilist",
                        ProviderItemId = "100",
                        MediaKind = VideoMetadataMediaKind.Anime,
                        Title = "Frieren",
                    },
                    null,
                    null,
                    null,
                    null,
                    null)],
                "TMDB unavailable")));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        viewModel.SearchText = "frieren";
        await viewModel.SearchVideosCommand.ExecuteAsync(null);

        discovery.Verify(service => service.SearchAggregatedAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist", "tmdb" })),
            "frieren",
            VideoDiscoverySearchCategory.All,
            It.IsAny<CancellationToken>()), Times.Once);
        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.ProviderId.Should().Be("anilist");
        viewModel.ProviderWarning.Should().Be("TMDB unavailable");
        viewModel.HasProviderWarning.Should().BeTrue();

        viewModel.HasMoreExplorePages.Should().BeFalse();
    }

    [Fact]
    public async Task AggregatedSearch_LaterRequestWinsWhenEarlierProviderIgnoresCancellation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.SearchAggregatedAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string>(),
                It.IsAny<VideoDiscoverySearchCategory>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, string, VideoDiscoverySearchCategory, CancellationToken>(
                async (_, query, _, _) =>
                {
                    if (query == "old")
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task;
                    }
                    return Result<VideoDiscoveryPage>.Success(CreateSearchPage(query));
                });
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        viewModel.SearchText = "old";
        var oldSearch = viewModel.SearchVideosCommand.ExecuteAsync(null);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.SearchText = "new";
        await viewModel.SearchVideosCommand.ExecuteAsync(null);

        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("new");
        releaseFirst.TrySetResult();
        await oldSearch;

        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("new");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task AggregatedSearch_CannotBeOverwrittenByAnOlderBrowseRequest()
    {
        var browseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBrowse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.GetAggregatedPageAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<VideoDiscoveryAggregateRequest>(request => request.Page == 1),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, VideoDiscoveryAggregateRequest, CancellationToken>(async (_, _, _) =>
            {
                browseStarted.TrySetResult();
                await releaseBrowse.Task;
                return Result<VideoDiscoveryPage>.Success(CreateSearchPage("old browse"));
            });
        discovery.Setup(service => service.SearchAggregatedAsync(
                It.IsAny<IReadOnlyList<string>>(),
                "new search",
                It.IsAny<VideoDiscoverySearchCategory>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoDiscoveryPage>.Success(CreateSearchPage("new search")));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        var browse = viewModel.ApplyFiltersCommand.ExecuteAsync(null);
        await browseStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.SearchText = "new search";
        await viewModel.SearchVideosCommand.ExecuteAsync(null);
        releaseBrowse.TrySetResult();
        await browse;

        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("new search");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task AggregatedBrowseLaterFilterRequestWinsWhenEarlierRequestIgnoresCancellation()
    {
        var browseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBrowse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.GetAggregatedPageAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<VideoDiscoveryAggregateRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, VideoDiscoveryAggregateRequest, CancellationToken>(async (_, request, _) =>
            {
                if (request.Year == 2020)
                {
                    browseStarted.TrySetResult();
                    await releaseBrowse.Task;
                    return Result<VideoDiscoveryPage>.Success(CreateBrowsePage("old browse"));
                }

                return Result<VideoDiscoveryPage>.Success(CreateBrowsePage("new browse"));
            });
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        viewModel.YearText = "2020";
        var oldBrowse = viewModel.ApplyFiltersCommand.ExecuteAsync(null);
        await browseStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.YearText = "2021";
        await viewModel.ApplyFiltersCommand.ExecuteAsync(null);

        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("new browse");
        releaseBrowse.TrySetResult();
        await oldBrowse;

        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("new browse");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshingBrowseResultsReloadsBrowseInsteadOfTitleSearch()
    {
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.GetAggregatedPageAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.Is<VideoDiscoveryAggregateRequest>(request => request.Page == 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoDiscoveryPage>.Success(CreateBrowsePage("browse")));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        await viewModel.ApplyFiltersCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        discovery.Verify(service => service.GetAggregatedPageAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist", "tmdb" })),
            It.Is<VideoDiscoveryAggregateRequest>(request => request.Page == 1),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        discovery.Verify(service => service.SearchAggregatedAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(),
            It.IsAny<VideoDiscoverySearchCategory>(),
            It.IsAny<CancellationToken>()), Times.Never);
        viewModel.ExploreItems.Should().ContainSingle()
            .Which.Identity.Title.Should().Be("browse");
    }

    [Fact]
    public async Task LoadingMoreBrowseResultsUsesTheNextAggregatedPage()
    {
        var discovery = CreateDiscoveryWithoutRecommendations();
        discovery.Setup(service => service.GetAggregatedPageAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<VideoDiscoveryAggregateRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, VideoDiscoveryAggregateRequest, CancellationToken>(
                (_, request, _) => Task.FromResult(Result<VideoDiscoveryPage>.Success(
                    CreateBrowsePage($"page {request.Page}", request.Page, 2))));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new DiscoverPageViewModel(
            discovery.Object,
            settings.Object,
            Mock.Of<INavigationService>());
        await viewModel.InitializeAsync();

        await viewModel.ApplyFiltersCommand.ExecuteAsync(null);
        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        viewModel.ExploreItems.Select(item => item.Identity.Title)
            .Should().Equal("page 1", "page 2");
        viewModel.HasMoreExplorePages.Should().BeFalse();
        discovery.Verify(service => service.GetAggregatedPageAsync(
            It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "anilist", "tmdb" })),
            It.Is<VideoDiscoveryAggregateRequest>(request => request.Page == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Header_actions_open_download_tasks_and_subscription_management_routes()
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var navigation = new Mock<INavigationService>();
        using var viewModel = new DiscoverPageViewModel(
            Mock.Of<IVideoDiscoveryService>(),
            settings.Object,
            navigation.Object);

        viewModel.OpenDownloadTasksCommand.Execute(null);
        viewModel.OpenSubscriptionsCommand.Execute(null);

        navigation.Verify(service => service.Navigate(
            typeof(DownloadsPage),
            DownloadsPageSection.Tasks), Times.Once);
        navigation.Verify(service => service.Navigate(
            typeof(DownloadsPage),
            DownloadsPageSection.Subscriptions), Times.Once);
    }

    [Fact]
    public async Task Subscription_route_requires_one_strict_release_and_forwards_artwork()
    {
        var identity = CreateIdentity() with
        {
            MediaKind = VideoMetadataMediaKind.Anime,
            PosterUrl = "https://image.example/poster.jpg",
        };
        var item = CreateItem() with { Title = "[Group] Test title - 03 [1080p]" };
        var resources = new Mock<IVideoResourceSearchService>();
        resources.Setup(service => service.BuildDefaultQuery(identity)).Returns("Test title");
        resources.Setup(service => service.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NyaaTorrentItem>>.Success([item]));
        var subscriptions = new Mock<INyaaSubscriptionService>();
        subscriptions.Setup(service => service.SubscribeAsync(
                identity,
                "Test title",
                "1_0",
                item,
                3,
                It.IsAny<NyaaSubscriptionArtwork>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(0));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var viewModel = new VideoDiscoveryResourceSearchPageViewModel(
            resources.Object,
            subscriptions.Object,
            new Lazy<INyaaDownloadManager>(() => Mock.Of<INyaaDownloadManager>()),
            Mock.Of<IQbittorrentCredentialStore>(),
            Mock.Of<IQbittorrentDownloadCoordinator>(),
            settings.Object);
        var route = new VideoDiscoveryResourceSearchTarget(
            new VideoDiscoveryNavigationTarget(
                identity,
                new VideoDiscoveryArtwork("C:\\cache\\poster.jpg", null, null)),
            VideoDiscoveryResourceRouteMode.Subscription);

        await viewModel.InitializeAsync(route);
        viewModel.SelectedResult = viewModel.Results.Single();

        viewModel.StrictReleaseGroup.Should().Be("Group");
        viewModel.StrictResolution.Should().Be("1080p");
        viewModel.StrictStartAfterEpisode.Should().Be(3);
        viewModel.HasStrictSelection.Should().BeTrue();

        await viewModel.SubmitSelectionCommand.ExecuteAsync(null);

        subscriptions.Verify(service => service.SubscribeAsync(
            identity,
            "Test title",
            "1_0",
            item,
            3,
            It.Is<NyaaSubscriptionArtwork>(artwork =>
                artwork.PosterUrl == identity.PosterUrl
                && artwork.PosterPath == "C:\\cache\\poster.jpg"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Download_route_uses_only_the_configured_builtin_backend()
    {
        var identity = CreateIdentity();
        var item = CreateItem();
        var resources = new Mock<IVideoResourceSearchService>();
        resources.Setup(service => service.BuildDefaultQuery(identity)).Returns("Test title");
        resources.Setup(service => service.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NyaaTorrentItem>>.Success([item]));
        var manager = new Mock<INyaaDownloadManager>();
        var qb = new Mock<IQbittorrentDownloadCoordinator>();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            DownloadBackend = DownloadBackendKind.MonoTorrent,
        });
        using var viewModel = new VideoDiscoveryResourceSearchPageViewModel(
            resources.Object,
            Mock.Of<INyaaSubscriptionService>(),
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            Mock.Of<IQbittorrentCredentialStore>(),
            qb.Object,
            settings.Object);

        await viewModel.InitializeAsync(new VideoDiscoveryResourceSearchTarget(
            VideoDiscoveryNavigationTarget.FromItem(new VideoDiscoveryItem(
                identity, null, null, null, null, null)),
            VideoDiscoveryResourceRouteMode.Download));
        viewModel.SelectedResult = viewModel.Results.Single();
        await viewModel.SubmitSelectionCommand.ExecuteAsync(null);

        manager.Verify(value => value.Enqueue(item), Times.Once);
        qb.Verify(value => value.AddAsync(
            It.IsAny<NyaaTorrentItem>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Subtitle_destination_never_reuses_an_existing_file_name()
    {
        var directory = Directory.CreateTempSubdirectory("niratan-subtitle-route-");
        try
        {
            var requested = Path.Combine(directory.FullName, "episode.ja.ass");
            File.WriteAllText(requested, "existing");

            var unique = VideoDiscoverySubtitleSearchPageViewModel.FindUniqueDestination(requested);

            unique.Should().Be(Path.Combine(directory.FullName, "episode.ja (2).ass"));
            File.ReadAllText(requested).Should().Be("existing");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Mock<IVideoDiscoveryService> CreateDiscoveryWithoutRecommendations()
    {
        var discovery = new Mock<IVideoDiscoveryService>();
        discovery.Setup(service => service.GetAggregatedRecommendationsAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoDiscoveryPage>>.Success(
                Array.Empty<VideoDiscoveryPage>()));
        return discovery;
    }

    private static VideoDiscoveryPage CreateSearchPage(string title) => new(
        "aggregate",
        "search",
        1,
        1,
        [new VideoDiscoveryItem(
            CreateIdentity() with { ProviderItemId = title, Title = title },
            null,
            null,
            null,
            null,
            null)]);

    private static VideoDiscoveryPage CreateBrowsePage(
        string title,
        int page = 1,
        int totalPages = 1) => new(
        "aggregate",
        "popular",
        page,
        totalPages,
        [new VideoDiscoveryItem(
            CreateIdentity() with { ProviderItemId = title, Title = title },
            null,
            null,
            null,
            null,
            null)]);

    private static NyaaTorrentItem CreateItem() => new(
        "test-resource",
        "[Test] Test title 2026",
        new Uri("https://nyaa.si/download/test-resource.torrent"),
        new Uri("https://nyaa.si/view/test-resource"),
        "Live action",
        1024,
        12,
        1,
        0,
        DateTimeOffset.UtcNow,
        true,
        false);

    private static VideoMetadataCandidate CreateIdentity() => new(
        "tmdb",
        "123",
        VideoMetadataMediaKind.Movie,
        "Test title",
        null,
        2026,
        null,
        null,
        null,
        ["Test title"],
        ImmutableDictionary<string, string>.Empty,
        null);
}
