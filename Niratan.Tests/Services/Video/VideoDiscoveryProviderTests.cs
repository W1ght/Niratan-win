using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Moq;
using Niratan.Models.Common;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Settings;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoDiscoveryProviderTests
{
    [Fact]
    public async Task TmdbExplore_ParsesCardsAndSendsFilters()
    {
        var transport = new FixtureTransport(
            """{"page":2,"total_pages":7,"results":[{"id":12,"title":"Movie","original_title":"Original","release_date":"2026-01-02","overview":"Summary","vote_average":8.4,"vote_count":100,"poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}]}""");
        var provider = new TmdbVideoDiscoveryProvider(transport, new FixtureCredentialStore());

        var page = await provider.GetPageAsync(new VideoDiscoveryRequest(
            "discover-movie", VideoMetadataMediaKind.Movie, 2, 2026, "16", "vote_average.desc"));

        page.Page.Should().Be(2);
        page.TotalPages.Should().Be(7);
        page.Items.Should().ContainSingle();
        page.Items[0].Identity.Title.Should().Be("Movie");
        page.Items[0].PosterUrl.Should().Be("https://image.tmdb.org/t/p/w500/poster.jpg");
        transport.LastRequest!.Uri.Query.Should().Contain("primary_release_year=2026");
        transport.LastRequest.Uri.Query.Should().Contain("with_genres=16");
        transport.LastRequest.Uri.Query.Should().Contain("sort_by=vote_average.desc");
    }

    [Fact]
    public async Task TmdbV3ApiKey_UsesQueryAuthentication()
    {
        const string apiKey = "12345678901234567890123456789012";
        var transport = new FixtureTransport(
            """{"results":[]}""");
        var provider = new TmdbVideoDiscoveryProvider(
            transport,
            new FixtureCredentialStore(apiKey));

        await provider.GetPageAsync(new VideoDiscoveryRequest(
            "popular-movie", VideoMetadataMediaKind.Movie));

        transport.LastRequest!.Uri.Query.Should().Contain("api_key=" + apiKey);
        transport.LastRequest.Headers.Should().ContainKey("Accept");
        transport.LastRequest.Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task AniListSeasonal_ParsesPagingAndUsesGraphqlBody()
    {
        var transport = new FixtureTransport(
            """{"data":{"Page":{"pageInfo":{"lastPage":4},"media":[{"id":99,"idMal":88,"title":{"romaji":"Romaji","english":"English","native":"Native"},"description":"Summary","seasonYear":2026,"averageScore":82,"coverImage":{"extraLarge":"https://s4.anilist.co/file/poster.jpg"},"bannerImage":"https://s4.anilist.co/file/banner.jpg","siteUrl":"https://anilist.co/anime/99"}]}}}""");
        var provider = new AniListVideoDiscoveryProvider(transport);

        var page = await provider.GetPageAsync(new VideoDiscoveryRequest(
            "seasonal", VideoMetadataMediaKind.Anime, 2));

        page.Page.Should().Be(2);
        page.TotalPages.Should().Be(4);
        page.Items.Should().ContainSingle();
        page.Items[0].Identity.Title.Should().Be("Romaji");
        page.Items[0].Identity.OriginalTitle.Should().Be("Native");
        page.Items[0].Identity.Aliases.Should().Contain("English");
        page.Items[0].CommunityRating.Should().Be(8.2);
        Encoding.UTF8.GetString(transport.LastRequest!.Body!).Should().Contain("seasonYear");
    }

    [Fact]
    public async Task AniListPopular_MapsNeutralYearGenreAndSortFilters()
    {
        var transport = new FixtureTransport(
            """{"data":{"Page":{"pageInfo":{"lastPage":1},"media":[]}}}""");
        var provider = new AniListVideoDiscoveryProvider(transport);

        await provider.GetPageAsync(new VideoDiscoveryRequest(
            "popular",
            VideoMetadataMediaKind.Anime,
            Year: 2025,
            GenreId: "28",
            SortBy: "vote_average.desc"), TestContext.Current.CancellationToken);

        var body = Encoding.UTF8.GetString(transport.LastRequest!.Body!);
        body.Should().Contain("SCORE_DESC");
        body.Should().Contain("seasonYear:2025");
        body.Should().Contain("genre:\\u0022Action\\u0022");
    }

    [Fact]
    public async Task Provider_PropagatesCancellationAndRejectsHttpErrors()
    {
        var cancelled = new TmdbVideoDiscoveryProvider(
            new FixtureTransport(null, cancel: true),
            new FixtureCredentialStore());
        var ct = new CancellationTokenSource().Token;

        var action = () => cancelled.GetPageAsync(
            new VideoDiscoveryRequest("popular-movie", VideoMetadataMediaKind.Movie), ct);

        await action.Should().ThrowAsync<OperationCanceledException>();

        var failed = new TmdbVideoDiscoveryProvider(
            new FixtureTransport("{}", statusCode: 503),
            new FixtureCredentialStore());
        var failure = () => failed.GetPageAsync(
            new VideoDiscoveryRequest("popular-movie", VideoMetadataMediaKind.Movie));
        await failure.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DiscoveryService_RequiresOnlineConsentBeforeNetworkAccess()
    {
        var provider = new Mock<IVideoDiscoveryProvider>();
        provider.SetupGet(value => value.Id).Returns("fixture");
        provider.SetupGet(value => value.Feeds).Returns([]);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(value => value.Current).Returns(new AppSettings());
        var service = new VideoDiscoveryService(
            [provider.Object],
            [],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);

        var result = await service.GetPageAsync(
            "fixture",
            new VideoDiscoveryRequest("feed", VideoMetadataMediaKind.Movie));

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().NotBeNullOrWhiteSpace();
        provider.Verify(value => value.GetPageAsync(It.IsAny<VideoDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryService_CachesHomePageResults_until_explicit_refresh()
    {
        var provider = new Mock<IVideoDiscoveryProvider>();
        provider.SetupGet(value => value.Id).Returns("fixture");
        provider.SetupGet(value => value.Feeds).Returns([
            new VideoDiscoveryFeed(
                "fixture",
                "popular",
                "Popular",
                VideoDiscoveryFeedKind.Explore,
                [VideoMetadataMediaKind.Movie])]);
        provider.Setup(value => value.GetPageAsync(
                It.IsAny<VideoDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoDiscoveryPage(
                "fixture",
                "popular",
                1,
                1,
                [new VideoDiscoveryItem(
                    new VideoMetadataCandidate(
                        "fixture",
                        "movie-1",
                        VideoMetadataMediaKind.Movie,
                        "Movie",
                        null,
                        2026,
                        null,
                        null,
                        null,
                        ["Movie"],
                        ImmutableDictionary<string, string>.Empty,
                        null),
                    null,
                    null,
                    null,
                    null,
                    null)]));

        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [provider.Object],
            [],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);
        var request = new VideoDiscoveryRequest("popular", VideoMetadataMediaKind.Movie);

        var first = await service.GetPageAsync("fixture", request);
        var second = await service.GetPageAsync("fixture", request);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value!.Items.Should().ContainSingle();
        provider.Verify(value => value.GetPageAsync(request, It.IsAny<CancellationToken>()), Times.Once);

        service.ClearCache();
        await service.GetPageAsync("fixture", request);
        provider.Verify(value => value.GetPageAsync(request, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DiscoveryService_SearchUsesEmbeddedArtworkWithoutPerResultArtworkRequests()
    {
        var candidate = new VideoMetadataCandidate(
            "fixture",
            "movie-1",
            VideoMetadataMediaKind.Movie,
            "Movie",
            "Original",
            2026,
            null,
            null,
            null,
            ["Movie", "Original"],
            ImmutableDictionary<string, string>.Empty,
            "https://example.test/movie-1",
            "https://image.tmdb.org/poster.jpg",
            "https://image.tmdb.org/backdrop.jpg");
        var searchProvider = new Mock<IVideoMetadataSearchProvider>();
        ConfigureProvider(searchProvider, "fixture", VideoMetadataCapabilities.Search);
        searchProvider
            .Setup(provider => provider.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var artworkProvider = new Mock<IVideoArtworkProvider>();
        ConfigureProvider(artworkProvider, "fixture", VideoMetadataCapabilities.Artwork);
        artworkProvider
            .Setup(provider => provider.GetArtworkAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VideoArtworkCandidate(
                    "fixture", "https://image.tmdb.org/poster.jpg", "poster", null, 500, 750, null),
                new VideoArtworkCandidate(
                    "fixture", "https://image.tmdb.org/backdrop.jpg", "backdrop", null, 1280, 720, null),
            ]);

        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var cache = new FixtureArtworkCache();
        var service = new VideoDiscoveryService(
            [],
            [],
            [artworkProvider.Object],
            new FixtureTransport("image"),
            cache,
            settings.Object,
            [searchProvider.Object]);

        var result = await service.SearchAsync(
            "fixture",
            "Movie",
            VideoMetadataMediaKind.Movie);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value!.Items.Should().ContainSingle().Subject;
        item.PosterUrl.Should().Be("https://image.tmdb.org/poster.jpg");
        item.BackdropUrl.Should().Be("https://image.tmdb.org/backdrop.jpg");
        item.LocalPosterPath.Should().Be(cache.PathFor("https://image.tmdb.org/poster.jpg"));
        item.LocalBackdropPath.Should().Be(cache.PathFor("https://image.tmdb.org/backdrop.jpg"));
        searchProvider.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query =>
                query.Language == "en-US" && query.Region == "US"),
            It.IsAny<CancellationToken>()), Times.Once);
        artworkProvider.Verify(provider => provider.GetArtworkAsync(
            It.Is<VideoMetadataCandidate>(value => value.ProviderItemId == "movie-1"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedSearch_UsesOnlyFushiSourcesAndStableRoundRobinOrder()
    {
        var anilist = CreateSearchProvider("anilist", query =>
            query.MediaKind == VideoMetadataMediaKind.Anime
                ? [
                    CreateSearchCandidate("anilist", "ani-1", VideoMetadataMediaKind.Anime, "Anime One", 2024),
                    CreateSearchCandidate("anilist", "ani-2", VideoMetadataMediaKind.Anime, "Anime Two", 2023),
                ]
                : []);
        var tmdb = CreateSearchProvider("tmdb", query => query.MediaKind switch
        {
            VideoMetadataMediaKind.Movie =>
                [CreateSearchCandidate("tmdb", "movie-1", VideoMetadataMediaKind.Movie, "Movie One", 2025)],
            VideoMetadataMediaKind.Series =>
                [CreateSearchCandidate("tmdb", "series-1", VideoMetadataMediaKind.Series, "Series One", 2022)],
            _ => [],
        });
        var tvmaze = CreateSearchProvider("tvmaze", _ =>
            [CreateSearchCandidate("tvmaze", "ignored", VideoMetadataMediaKind.Series, "Ignored", 2020)]);
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object, tvmaze.Object);

        var result = await service.SearchAggregatedAsync(
            ["tmdb", "tvmaze", "anilist"],
            "work",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(item => item.Identity.ProviderItemId)
            .Should().Equal("ani-1", "movie-1", "ani-2", "series-1");
        anilist.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query => query.MediaKind == VideoMetadataMediaKind.Anime),
            It.IsAny<CancellationToken>()), Times.Once);
        tmdb.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query => query.MediaKind == VideoMetadataMediaKind.Movie),
            It.IsAny<CancellationToken>()), Times.Once);
        tmdb.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query => query.MediaKind == VideoMetadataMediaKind.Series),
            It.IsAny<CancellationToken>()), Times.Once);
        tvmaze.Verify(provider => provider.SearchAsync(
            It.IsAny<VideoMetadataSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedBrowse_UsesOnlyFushiSourcesAndStableNestedRoundRobinOrder()
    {
        var anilist = CreateDiscoveryProvider("anilist", request => new VideoDiscoveryPage(
            "anilist",
            request.FeedId,
            request.Page,
            1,
            [
                CreateDiscoveryItem("anilist", "ani-1", VideoMetadataMediaKind.Anime, "Anime One"),
                CreateDiscoveryItem("anilist", "ani-2", VideoMetadataMediaKind.Anime, "Anime Two"),
                CreateDiscoveryItem("anilist", "ani-3", VideoMetadataMediaKind.Anime, "Anime Three"),
            ]));
        var tmdb = CreateDiscoveryProvider("tmdb", request => new VideoDiscoveryPage(
            "tmdb",
            request.FeedId,
            request.Page,
            1,
            request.MediaKind == VideoMetadataMediaKind.Movie
                ? [
                    CreateDiscoveryItem("tmdb", "movie-1", VideoMetadataMediaKind.Movie, "Movie One"),
                    CreateDiscoveryItem("tmdb", "movie-2", VideoMetadataMediaKind.Movie, "Movie Two"),
                ]
                : [
                    CreateDiscoveryItem("tmdb", "series-1", VideoMetadataMediaKind.Series, "Series One"),
                    CreateDiscoveryItem("tmdb", "series-2", VideoMetadataMediaKind.Series, "Series Two"),
                ]));
        var tvmaze = CreateDiscoveryProvider("tvmaze", request => new VideoDiscoveryPage(
            "tvmaze",
            request.FeedId,
            request.Page,
            1,
            [CreateDiscoveryItem("tvmaze", "ignored", VideoMetadataMediaKind.Series, "Ignored")]));
        var service = CreateOnlineBrowseService(anilist.Object, tmdb.Object, tvmaze.Object);

        var result = await service.GetAggregatedPageAsync(
            ["tmdb", "tvmaze", "anilist"],
            new VideoDiscoveryAggregateRequest(PageSize: 20),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(item => item.Identity.ProviderItemId)
            .Should().Equal(
                "ani-1", "movie-1", "ani-2", "series-1",
                "ani-3", "movie-2", "series-2");
        anilist.Verify(provider => provider.GetPageAsync(
            It.Is<VideoDiscoveryRequest>(request =>
                request.FeedId == "popular"
                && request.MediaKind == VideoMetadataMediaKind.Anime
                && request.Page == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        tmdb.Verify(provider => provider.GetPageAsync(
            It.Is<VideoDiscoveryRequest>(request =>
                request.FeedId == "popular-movie"
                && request.MediaKind == VideoMetadataMediaKind.Movie
                && request.Page == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        tmdb.Verify(provider => provider.GetPageAsync(
            It.Is<VideoDiscoveryRequest>(request =>
                request.FeedId == "popular-tv"
                && request.MediaKind == VideoMetadataMediaKind.Series
                && request.Page == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        tvmaze.Verify(provider => provider.GetPageAsync(
            It.IsAny<VideoDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedRecommendationsUseFushiConceptShelves()
    {
        const string romaji = "Re:Zero kara Hajimeru Isekai Seikatsu";
        const string english = "Re:ZERO -Starting Life in Another World-";
        const string native = "Re:ゼロから始める異世界生活";
        var aniListReZero = new VideoDiscoveryItem(
            CreateSearchCandidate(
                "anilist",
                "21355",
                VideoMetadataMediaKind.Anime,
                romaji,
                2016,
                [english, native]) with
            {
                OriginalTitle = native,
            },
            null, null, null, null, null);
        var tmdbReZero = new VideoDiscoveryItem(
            CreateSearchCandidate(
                "tmdb",
                "65942",
                VideoMetadataMediaKind.Series,
                english,
                2016,
                [romaji, native]),
            null, null, null, null, null);
        var anilist = CreateDiscoveryProvider("anilist", request => new VideoDiscoveryPage(
            "anilist",
            request.FeedId,
            request.Page,
            1,
            request.FeedId == "popular"
                ? [aniListReZero]
                : [CreateDiscoveryItem(
                    "anilist",
                    $"{request.FeedId}-anime",
                    VideoMetadataMediaKind.Anime,
                    $"{request.FeedId} anime")]));
        var tmdb = CreateDiscoveryProvider("tmdb", request => new VideoDiscoveryPage(
            "tmdb",
            request.FeedId,
            request.Page,
            1,
            request.FeedId == "trending-tv"
                ? [tmdbReZero]
                : [CreateDiscoveryItem(
                    "tmdb",
                    request.FeedId,
                    request.MediaKind,
                    request.FeedId)]));
        var service = CreateOnlineBrowseService(anilist.Object, tmdb.Object);

        var result = await service.GetAggregatedRecommendationsAsync(
            ["anilist", "tmdb"],
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var pages = result.Value!;
        pages.Select(page => page.FeedId)
            .Should().Equal("trending", "seasonal", "popular");
        pages.Single(page => page.FeedId == "seasonal").Items
            .Should().OnlyContain(item => item.Identity.ProviderId == "anilist");
        var trendingReZero = pages.Single(page => page.FeedId == "trending").Items
            .Where(item =>
                item.Identity.ExternalIds.TryGetValue("tmdb", out var tmdbId)
                && tmdbId == "65942"
                && item.Identity.ExternalIds.ContainsKey("anilist"))
            .Should().ContainSingle().Subject;
        trendingReZero.Identity.Title.Should().Be(romaji);
        trendingReZero.Identity.OriginalTitle.Should().Be(native);
        trendingReZero.Identity.Aliases.Should().Contain(english);
        trendingReZero.Identity.ExternalIds.Should().Contain("tmdb", "65942");
        foreach (var feed in new[] { "trending", "seasonal", "popular" })
        {
            anilist.Verify(provider => provider.GetPageAsync(
                It.Is<VideoDiscoveryRequest>(request => request.FeedId == feed),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        foreach (var feed in new[]
                 {
                     "trending-movie", "trending-tv", "popular-movie", "popular-tv",
                 })
        {
            tmdb.Verify(provider => provider.GetPageAsync(
                It.Is<VideoDiscoveryRequest>(request => request.FeedId == feed),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        tmdb.Invocations.Count(invocation =>
                invocation.Method.Name == nameof(IVideoDiscoveryProvider.GetPageAsync))
            .Should().Be(4);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedBrowse_KeepsSuccessfulStreamsWhenOneTmdbKindFails()
    {
        var anilist = CreateDiscoveryProvider("anilist", request => new VideoDiscoveryPage(
            "anilist",
            request.FeedId,
            request.Page,
            1,
            [CreateDiscoveryItem("anilist", "ani-1", VideoMetadataMediaKind.Anime, "Anime")]));
        var tmdb = CreateDiscoveryProvider("tmdb", request =>
        {
            if (request.MediaKind == VideoMetadataMediaKind.Movie)
                throw new HttpRequestException("movie stream offline");
            return new VideoDiscoveryPage(
                "tmdb",
                request.FeedId,
                request.Page,
                1,
                [CreateDiscoveryItem("tmdb", "series-1", VideoMetadataMediaKind.Series, "Series")]);
        });
        var service = CreateOnlineBrowseService(anilist.Object, tmdb.Object);

        var result = await service.GetAggregatedPageAsync(
            ["anilist", "tmdb"],
            new VideoDiscoveryAggregateRequest(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(item => item.Identity.ProviderItemId)
            .Should().Equal("ani-1", "series-1");
        result.Value.Error.Should().ContainEquivalentOf("tmdb");
    }

    [Fact]
    public async Task DiscoveryService_AggregatedBrowse_FailsOnlyWhenEveryStreamFails()
    {
        var anilist = CreateDiscoveryProvider("anilist", _ =>
            throw new HttpRequestException("anilist offline"));
        var tmdb = CreateDiscoveryProvider("tmdb", _ =>
            throw new HttpRequestException("tmdb offline"));
        var service = CreateOnlineBrowseService(anilist.Object, tmdb.Object);

        var result = await service.GetAggregatedPageAsync(
            ["anilist", "tmdb"],
            new VideoDiscoveryAggregateRequest(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().ContainEquivalentOf("anilist");
        result.Error.Should().ContainEquivalentOf("tmdb");
        result.ErrorTitle.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DiscoveryService_AggregatedBrowse_RebuildsCumulativePrefixWithoutDroppingPriorPageTail()
    {
        var anilist = CreateDiscoveryProvider("anilist", request =>
        {
            var firstId = request.Page == 1 ? 1 : 5;
            return new VideoDiscoveryPage(
                "anilist",
                request.FeedId,
                request.Page,
                2,
                Enumerable.Range(firstId, 4)
                    .Select(id => CreateDiscoveryItem(
                        "anilist",
                        $"ani-{id}",
                        VideoMetadataMediaKind.Anime,
                        $"Anime {id}"))
                    .ToImmutableArray());
        });
        var service = CreateOnlineBrowseService(anilist.Object);
        var request = new VideoDiscoveryAggregateRequest(PageSize: 3);

        var first = await service.GetAggregatedPageAsync(
            ["anilist"],
            request,
            TestContext.Current.CancellationToken);
        var second = await service.GetAggregatedPageAsync(
            ["anilist"],
            request with { Page = 2 },
            TestContext.Current.CancellationToken);

        first.Value!.Items.Select(item => item.Identity.ProviderItemId)
            .Should().Equal("ani-1", "ani-2", "ani-3");
        second.Value!.Items.Select(item => item.Identity.ProviderItemId)
            .Should().Equal("ani-4", "ani-5", "ani-6");
        anilist.Verify(provider => provider.GetPageAsync(
            It.Is<VideoDiscoveryRequest>(page => page.Page == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        anilist.Verify(provider => provider.GetPageAsync(
            It.Is<VideoDiscoveryRequest>(page => page.Page == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(VideoDiscoverySearchCategory.Movie, 1, 0, VideoMetadataMediaKind.Movie)]
    [InlineData(VideoDiscoverySearchCategory.Series, 1, 0, VideoMetadataMediaKind.Series)]
    [InlineData(VideoDiscoverySearchCategory.Anime, 0, 1, VideoMetadataMediaKind.Anime)]
    public async Task DiscoveryService_AggregatedSearch_RoutesCategoriesToSupportedSourceOnly(
        VideoDiscoverySearchCategory category,
        int expectedTmdbCalls,
        int expectedAniListCalls,
        VideoMetadataMediaKind expectedKind)
    {
        var tmdb = CreateSearchProvider("tmdb", _ => []);
        var anilist = CreateSearchProvider("anilist", _ => []);
        var service = CreateOnlineDiscoveryService(tmdb.Object, anilist.Object);

        var result = await service.SearchAggregatedAsync(
            ["tmdb", "anilist"],
            "work",
            category,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        tmdb.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query => query.MediaKind == expectedKind),
            It.IsAny<CancellationToken>()), Times.Exactly(expectedTmdbCalls));
        anilist.Verify(provider => provider.SearchAsync(
            It.Is<VideoMetadataSearchQuery>(query => query.MediaKind == expectedKind),
            It.IsAny<CancellationToken>()), Times.Exactly(expectedAniListCalls));
        tmdb.Invocations.Count(invocation => invocation.Method.Name == nameof(IVideoMetadataSearchProvider.SearchAsync))
            .Should().Be(expectedTmdbCalls);
        anilist.Invocations.Count(invocation => invocation.Method.Name == nameof(IVideoMetadataSearchProvider.SearchAsync))
            .Should().Be(expectedAniListCalls);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedSearch_KeepsSuccessfulSourceWhenOtherSourceFails()
    {
        var anilist = CreateSearchProvider("anilist", _ =>
            [CreateSearchCandidate("anilist", "ani-1", VideoMetadataMediaKind.Anime, "Anime", 2024)]);
        var tmdb = CreateSearchProvider("tmdb", _ => throw new HttpRequestException("offline"));
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object);

        var result = await service.SearchAggregatedAsync(
            ["anilist", "tmdb"],
            "anime",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle()
            .Which.Identity.ProviderId.Should().Be("anilist");
        result.Value.Error.Should().ContainEquivalentOf("tmdb");
    }

    [Fact]
    public async Task DiscoveryService_AggregatedSearch_FailsOnlyWhenEverySourceFails()
    {
        var anilist = CreateSearchProvider("anilist", _ => throw new HttpRequestException("offline"));
        var tmdb = CreateSearchProvider("tmdb", _ => throw new HttpRequestException("offline"));
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object);

        var result = await service.SearchAggregatedAsync(
            ["anilist", "tmdb"],
            "work",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().ContainEquivalentOf("anilist");
        result.Error.Should().ContainEquivalentOf("tmdb");
        result.ErrorTitle.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DiscoveryService_AggregatedSearch_MergesExactTitleAndYearAndPreservesBothIdentities()
    {
        var aniIds = ImmutableDictionary<string, string>.Empty
            .Add("anilist", "100")
            .Add("mal", "200");
        var tmdbIds = ImmutableDictionary<string, string>.Empty.Add("tmdb", "300");
        var anilist = CreateSearchProvider("anilist", _ =>
            [CreateSearchCandidate(
                "anilist", "100", VideoMetadataMediaKind.Anime,
                "Sousou no Frieren", 2023, ["Frieren: Beyond Journey's End"], aniIds)]);
        var tmdb = CreateSearchProvider("tmdb", query => query.MediaKind == VideoMetadataMediaKind.Series
            ? [CreateSearchCandidate(
                "tmdb", "300", VideoMetadataMediaKind.Series,
                "Frieren: Beyond Journey's End", 2023, ["Sousou no Frieren"], tmdbIds)]
            : []);
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object);

        var result = await service.SearchAggregatedAsync(
            ["anilist", "tmdb"],
            "frieren",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        var item = result.Value!.Items.Should().ContainSingle().Subject;
        item.Identity.ProviderId.Should().Be("anilist");
        item.Identity.ExternalIds.Should().Contain("anilist", "100");
        item.Identity.ExternalIds.Should().Contain("tmdb", "300");
        item.Identity.Aliases.Should().Contain("Frieren: Beyond Journey's End");
    }

    [Fact]
    public async Task DiscoveryService_AggregatedAnimeMovieKeepsTmdbIdentityAndUsesAniListRomajiTitle()
    {
        const string romaji = "Kimi no Na wa.";
        const string english = "Your Name.";
        const string native = "君の名は。";
        var aniListCandidate = CreateSearchCandidate(
            "anilist",
            "21519",
            VideoMetadataMediaKind.Anime,
            romaji,
            2016,
            [english, native]) with
        {
            OriginalTitle = native,
        };
        var anilist = CreateSearchProvider("anilist", _ => [aniListCandidate]);
        var tmdb = CreateSearchProvider("tmdb", query => query.MediaKind == VideoMetadataMediaKind.Movie
            ? [CreateSearchCandidate(
                "tmdb",
                "372058",
                VideoMetadataMediaKind.Movie,
                english,
                2016,
                [romaji, native])]
            : []);
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object);

        var result = await service.SearchAggregatedAsync(
            ["anilist", "tmdb"],
            "your name",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        var item = result.Value!.Items.Should().ContainSingle().Subject;
        item.Identity.ProviderId.Should().Be("tmdb");
        item.Identity.ProviderItemId.Should().Be("372058");
        item.Identity.MediaKind.Should().Be(VideoMetadataMediaKind.Movie);
        item.Identity.Title.Should().Be(romaji);
        item.Identity.OriginalTitle.Should().Be(native);
        item.Identity.Aliases.Should().Contain(english);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedSearch_DoesNotMergeConflictsKindsOrMissingYears()
    {
        var anilist = CreateSearchProvider("anilist", _ =>
        [
            CreateSearchCandidate(
                "anilist", "ani-conflict", VideoMetadataMediaKind.Anime, "Conflict", 2020,
                externalIds: ImmutableDictionary<string, string>.Empty.Add("mal", "1")),
            CreateSearchCandidate("anilist", "ani-no-year", VideoMetadataMediaKind.Anime, "No Year", null),
        ]);
        var tmdb = CreateSearchProvider("tmdb", query => query.MediaKind switch
        {
            VideoMetadataMediaKind.Movie =>
                [CreateSearchCandidate("tmdb", "7", VideoMetadataMediaKind.Movie, "Same Numeric Id", 2021)],
            VideoMetadataMediaKind.Series =>
            [
                CreateSearchCandidate(
                    "tmdb", "series-conflict", VideoMetadataMediaKind.Series, "Conflict", 2020,
                    externalIds: ImmutableDictionary<string, string>.Empty.Add("mal", "2")),
                CreateSearchCandidate("tmdb", "series-no-year", VideoMetadataMediaKind.Series, "No Year", null),
                CreateSearchCandidate("tmdb", "7", VideoMetadataMediaKind.Series, "Same Numeric Id", 2021),
            ],
            _ => [],
        });
        var service = CreateOnlineDiscoveryService(anilist.Object, tmdb.Object);

        var result = await service.SearchAggregatedAsync(
            ["anilist", "tmdb"],
            "work",
            VideoDiscoverySearchCategory.All,
            TestContext.Current.CancellationToken);

        result.Value!.Items.Should().HaveCount(6);
        result.Value.Items.Count(item => item.Identity.ProviderItemId == "7").Should().Be(2);
        result.Value.Items.Count(item => item.Identity.Title == "Conflict").Should().Be(2);
        result.Value.Items.Count(item => item.Identity.Title == "No Year").Should().Be(2);
    }

    [Fact]
    public async Task DiscoveryService_CachesMainAndSecondaryDetailsArtwork()
    {
        var identity = new VideoMetadataCandidate(
            "fixture",
            "movie-1",
            VideoMetadataMediaKind.Movie,
            "Movie",
            "Original",
            2026,
            null,
            null,
            null,
            ["Movie"],
            ImmutableDictionary<string, string>.Empty,
            "https://example.test/movie-1");
        var metadata = new VideoMetadataDetails(
            "fixture",
            "movie-1",
            VideoMetadataMediaKind.Movie,
            "Movie",
            "Original",
            null,
            "Overview",
            2026,
            null,
            null,
            null,
            ["Movie"],
            [],
            ["Actor"],
            ImmutableDictionary<string, string>.Empty,
            identity.SourceUrl,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            People: [new("person-1", "Actor", "Role", "Actor", "https://image.tmdb.org/person.jpg")],
            RelatedItems: [new(
                "fixture",
                "related-1",
                "Related",
                null,
                2025,
                "https://image.tmdb.org/related-poster.jpg",
                "https://image.tmdb.org/related-backdrop.jpg",
                "https://example.test/related-1")]);

        var detailsProvider = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(detailsProvider, "fixture", VideoMetadataCapabilities.Details);
        detailsProvider
            .Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var artworkProvider = new Mock<IVideoArtworkProvider>();
        ConfigureProvider(artworkProvider, "fixture", VideoMetadataCapabilities.Artwork);
        artworkProvider
            .Setup(provider => provider.GetArtworkAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VideoArtworkCandidate("fixture", "https://image.tmdb.org/poster.jpg", "poster", null, null, null, null),
                new VideoArtworkCandidate("fixture", "https://image.tmdb.org/backdrop.jpg", "backdrop", null, null, null, null),
            ]);

        var cache = new FixtureArtworkCache();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            [detailsProvider.Object],
            [artworkProvider.Object],
            new FixtureTransport("image"),
            cache,
            settings.Object);

        var result = await service.GetDetailsAsync(identity);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Artwork.PosterPath.Should().Be(cache.PathFor("https://image.tmdb.org/poster.jpg"));
        result.Value.Artwork.BackdropPath.Should().Be(cache.PathFor("https://image.tmdb.org/backdrop.jpg"));
        result.Value.Metadata.People.Should().ContainSingle()
            .Which.LocalImagePath.Should().Be(cache.PathFor("https://image.tmdb.org/person.jpg"));
        result.Value.Metadata.RelatedItems.Should().ContainSingle()
            .Which.LocalPosterPath.Should().Be(cache.PathFor("https://image.tmdb.org/related-poster.jpg"));
        cache.StoredUrls.Should().Contain("https://image.tmdb.org/person.jpg");
        cache.StoredUrls.Should().Contain("https://image.tmdb.org/related-poster.jpg");

        var cachedResult = await service.GetDetailsAsync(identity);

        cachedResult.IsSuccess.Should().BeTrue();
        cachedResult.Value.Should().NotBeNull();
        detailsProvider.Verify(provider => provider.GetDetailsAsync(
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        artworkProvider.Verify(provider => provider.GetArtworkAsync(
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoveryService_DetailsCacheSeparatesTmdbMovieAndSeriesWithSameNumericId()
    {
        var detailsProvider = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(detailsProvider, "tmdb", VideoMetadataCapabilities.Details);
        detailsProvider.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoMetadataCandidate, string, string, CancellationToken>((identity, _, _, _) =>
                Task.FromResult<VideoMetadataDetails?>(new VideoMetadataDetails(
                    identity.ProviderId,
                    identity.ProviderItemId,
                    identity.MediaKind,
                    identity.Title,
                    null,
                    null,
                    null,
                    identity.Year,
                    null,
                    null,
                    null,
                    [identity.Title],
                    [],
                    [],
                    identity.ExternalIds,
                    identity.SourceUrl,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddDays(1))));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            [detailsProvider.Object],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);
        var movie = CreateSearchCandidate(
            "tmdb", "7", VideoMetadataMediaKind.Movie, "Movie", 2020);
        var series = CreateSearchCandidate(
            "tmdb", "7", VideoMetadataMediaKind.Series, "Series", 2021);

        var movieResult = await service.GetDetailsAsync(movie, TestContext.Current.CancellationToken);
        var seriesResult = await service.GetDetailsAsync(series, TestContext.Current.CancellationToken);

        movieResult.Value!.Metadata.MediaKind.Should().Be(VideoMetadataMediaKind.Movie);
        seriesResult.Value!.Metadata.MediaKind.Should().Be(VideoMetadataMediaKind.Series);
        detailsProvider.Verify(provider => provider.GetDetailsAsync(
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DiscoveryService_CachedDetailsAddAggregatedIdentityWithoutOverwritingProviderIds()
    {
        var detailsProvider = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(detailsProvider, "anilist", VideoMetadataCapabilities.Details);
        detailsProvider.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoMetadataCandidate, string, string, CancellationToken>((candidate, _, _, _) =>
                Task.FromResult<VideoMetadataDetails?>(new VideoMetadataDetails(
                    "anilist",
                    "100",
                    VideoMetadataMediaKind.Anime,
                    "Work",
                    null,
                    null,
                    null,
                    2024,
                    null,
                    null,
                    null,
                    ["Work"],
                    [],
                    [],
                    candidate.ExternalIds.SetItem("mal", "200"),
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddDays(1))));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            [detailsProvider.Object],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);
        var cachedIdentity = CreateSearchCandidate(
            "anilist",
            "100",
            VideoMetadataMediaKind.Anime,
            "Work",
            2024,
            externalIds: ImmutableDictionary<string, string>.Empty
                .Add("anilist", "100")
                .Add("tmdb", "stale-cross-reference"));
        var aggregatedIdentity = cachedIdentity with
        {
            ExternalIds = cachedIdentity.ExternalIds
                .SetItem("tmdb", "300")
                .Add("mal", "stale"),
        };

        var first = await service.GetDetailsAsync(
            cachedIdentity,
            TestContext.Current.CancellationToken);
        var second = await service.GetDetailsAsync(
            aggregatedIdentity,
            TestContext.Current.CancellationToken);

        first.IsSuccess.Should().BeTrue();
        first.Value!.Metadata.ExternalIds.Should().Contain("tmdb", "stale-cross-reference");
        second.Value!.Metadata.ExternalIds.Should().Contain("tmdb", "300");
        second.Value.Metadata.ExternalIds.Should().Contain("mal", "200");
        detailsProvider.Verify(provider => provider.GetDetailsAsync(
            It.Is<VideoMetadataCandidate>(candidate =>
                candidate.ExternalIds.Count == 1
                && candidate.ExternalIds["anilist"] == "100"),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoveryService_AggregatedIdentityHydratesDetailsFromAniListAndTmdb()
    {
        var identity = CreateSearchCandidate(
            "anilist",
            "100",
            VideoMetadataMediaKind.Anime,
            "Work",
            2024,
            externalIds: ImmutableDictionary<string, string>.Empty
                .Add("anilist", "100")
                .Add("tmdb", "300"));
        var aniListDetails = BuildSeriesDetails(identity, 0) with
        {
            Overview = null,
            ExternalIds = ImmutableDictionary<string, string>.Empty.Add("anilist", "100"),
        };
        var tmdbIdentity = identity with
        {
            ProviderId = "tmdb",
            ProviderItemId = "300",
            MediaKind = VideoMetadataMediaKind.Series,
        };
        var tmdbDetails = BuildSeriesDetails(tmdbIdentity, 1) with
        {
            Overview = "TMDB overview",
            OfficialRating = "TV-14",
            ExternalIds = ImmutableDictionary<string, string>.Empty
                .Add("tmdb", "300")
                .Add("imdb", "tt1234567"),
            People = [new VideoPersonCredit("9", "Actor", "Role", "Actor", null)],
        };
        var aniList = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(aniList, "anilist", VideoMetadataCapabilities.Details);
        aniList.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(aniListDetails);
        var tmdb = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdb, "tmdb", VideoMetadataCapabilities.Details);
        tmdb.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tmdbDetails);
        var service = CreateOnlineDetailsService(aniList.Object, tmdb.Object);

        var result = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);
        var cachedResult = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        cachedResult.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderId.Should().Be("anilist");
        result.Value.Metadata.ProviderItemId.Should().Be("100");
        result.Value.Metadata.Overview.Should().Be("TMDB overview");
        result.Value.Metadata.OfficialRating.Should().Be("TV-14");
        result.Value.Metadata.ExternalIds.Should().Contain("imdb", "tt1234567");
        result.Value.Metadata.People.Should().ContainSingle(person => person.Name == "Actor");
        result.Value.Seasons.Should().ContainSingle();
        tmdb.Verify(provider => provider.GetDetailsAsync(
            It.Is<VideoMetadataCandidate>(candidate =>
                candidate.ProviderItemId == "300"
                && candidate.MediaKind == VideoMetadataMediaKind.Series),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
        aniList.Verify(provider => provider.GetDetailsAsync(
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoveryService_TmdbPrimaryHydratesAniListWithoutReplacingMovieIdentity()
    {
        const string romaji = "Re:Zero kara Hajimeru Isekai Seikatsu";
        const string english = "Re:ZERO -Starting Life in Another World-";
        const string native = "Re:ゼロから始める異世界生活";
        var identity = CreateSearchCandidate(
            "tmdb",
            "7",
            VideoMetadataMediaKind.Movie,
            english,
            2024,
            externalIds: ImmutableDictionary<string, string>.Empty
                .Add("tmdb", "7")
                .Add("anilist", "100"));
        var tmdb = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdb, "tmdb", VideoMetadataCapabilities.Details);
        tmdb.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(identity, 0) with { OriginalTitle = null });
        var aniList = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(aniList, "anilist", VideoMetadataCapabilities.Details);
        aniList.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoMetadataCandidate, string, string, CancellationToken>((candidate, _, _, _) =>
                Task.FromResult<VideoMetadataDetails?>(BuildSeriesDetails(candidate, 0) with
                {
                    Title = romaji,
                    OriginalTitle = native,
                    Aliases = [english],
                }));
        var service = CreateOnlineDetailsService(tmdb.Object, aniList.Object);

        var result = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderId.Should().Be("tmdb");
        result.Value.Metadata.ProviderItemId.Should().Be("7");
        result.Value.Metadata.MediaKind.Should().Be(VideoMetadataMediaKind.Movie);
        result.Value.Metadata.Title.Should().Be(romaji);
        result.Value.Metadata.OriginalTitle.Should().Be(native);
        result.Value.Metadata.Aliases.Should().Contain(english);
        aniList.Verify(provider => provider.GetDetailsAsync(
            It.Is<VideoMetadataCandidate>(candidate =>
                candidate.ProviderItemId == "100"
                && candidate.MediaKind == VideoMetadataMediaKind.Anime),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoveryService_SupplementalDetailsFailureKeepsPrimaryDetails()
    {
        var identity = CreateSearchCandidate(
            "anilist",
            "100",
            VideoMetadataMediaKind.Anime,
            "Work",
            2024,
            externalIds: ImmutableDictionary<string, string>.Empty
                .Add("anilist", "100")
                .Add("tmdb", "300"));
        var aniList = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(aniList, "anilist", VideoMetadataCapabilities.Details);
        aniList.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(identity, 0) with { Overview = "AniList overview" });
        var tmdb = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdb, "tmdb", VideoMetadataCapabilities.Details);
        tmdb.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var service = CreateOnlineDetailsService(aniList.Object, tmdb.Object);

        var result = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderId.Should().Be("anilist");
        result.Value.Metadata.Overview.Should().Be("AniList overview");
    }

    [Fact]
    public async Task DiscoveryService_TmdbPrimaryKeepsCanonicalAniListTitleWhenSupplementFails()
    {
        const string romaji = "Kimi no Na wa.";
        const string english = "Your Name.";
        const string native = "君の名は。";
        var identity = CreateSearchCandidate(
            "tmdb",
            "372058",
            VideoMetadataMediaKind.Movie,
            romaji,
            2016,
            [english, native],
            ImmutableDictionary<string, string>.Empty
                .Add("tmdb", "372058")
                .Add("anilist", "21519")) with
        {
            OriginalTitle = native,
        };
        var tmdb = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdb, "tmdb", VideoMetadataCapabilities.Details);
        tmdb.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(identity, 0) with
            {
                Title = english,
                OriginalTitle = null,
                Aliases = [english],
            });
        var aniList = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(aniList, "anilist", VideoMetadataCapabilities.Details);
        aniList.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        var service = CreateOnlineDetailsService(tmdb.Object, aniList.Object);

        var result = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderId.Should().Be("tmdb");
        result.Value.Metadata.ProviderItemId.Should().Be("372058");
        result.Value.Metadata.MediaKind.Should().Be(VideoMetadataMediaKind.Movie);
        result.Value.Metadata.Title.Should().Be(romaji);
        result.Value.Metadata.OriginalTitle.Should().Be(native);
        result.Value.Metadata.Aliases.Should().Contain(english);
    }

    [Fact]
    public async Task DiscoveryService_DetailsWithoutExactSupplementIdDoNotSearchAnotherSource()
    {
        var identity = CreateSearchCandidate(
            "anilist",
            "100",
            VideoMetadataMediaKind.Anime,
            "Work",
            2024);
        var aniList = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(aniList, "anilist", VideoMetadataCapabilities.Details);
        aniList.Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(identity, 0));
        var tmdb = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdb, "tmdb", VideoMetadataCapabilities.Details);
        var service = CreateOnlineDetailsService(aniList.Object, tmdb.Object);

        var result = await service.GetDetailsAsync(
            identity,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        tmdb.Verify(provider => provider.GetDetailsAsync(
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryService_ResolvesLibraryTitleWhenSeriesHasNoExternalId()
    {
        var candidate = new VideoMetadataCandidate(
            "tmdb",
            "mushoku",
            VideoMetadataMediaKind.Series,
            "Mushoku Tensei: Jobless Reincarnation",
            "無職転生 ～異世界行ったら本気だす～",
            2021,
            null,
            null,
            null,
            ["Mushoku Tensei"],
            ImmutableDictionary<string, string>.Empty,
            "https://www.themoviedb.org/tv/mushoku");
        var searchProvider = new Mock<IVideoMetadataSearchProvider>();
        ConfigureProvider(searchProvider, "tmdb", VideoMetadataCapabilities.Search);
        searchProvider
            .Setup(provider => provider.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var metadata = new VideoMetadataDetails(
            "tmdb",
            "mushoku",
            VideoMetadataMediaKind.Series,
            candidate.Title,
            candidate.OriginalTitle,
            null,
            "Overview",
            2021,
            null,
            null,
            null,
            candidate.Aliases,
            [],
            [],
            candidate.ExternalIds,
            candidate.SourceUrl,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            Seasons: [new VideoMetadataSeason(
                1,
                "Season 1",
                null,
                "2021-04-11",
                1,
                null,
                [new VideoMetadataEpisode(1, "Episode 1", null, null, null, 24, null, null)])]);
        var detailsProvider = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(detailsProvider, "tmdb", VideoMetadataCapabilities.Details);
        detailsProvider
            .Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            [detailsProvider.Object],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object,
            [searchProvider.Object]);

        var result = await service.GetDetailsByTitleAsync(
            ["Mushoku Tensei Isekai Ittara Honki Dasu"],
            VideoMetadataMediaKind.Series,
            2021);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderItemId.Should().Be("mushoku");
        result.Value.Seasons.Should().ContainSingle();
        result.Value.Seasons[0].Episodes.Should().ContainSingle();
    }

    [Fact]
    public async Task DiscoveryService_PrefersProviderWithRichestSeasonInventory()
    {
        var tmdbCandidate = new VideoMetadataCandidate(
            "tmdb",
            "mushoku-tmdb",
            VideoMetadataMediaKind.Series,
            "Mushoku Tensei Isekai Ittara Honki Dasu",
            null,
            2021,
            null,
            null,
            null,
            [],
            ImmutableDictionary<string, string>.Empty,
            null);
        var tvMazeCandidate = tmdbCandidate with
        {
            ProviderId = "tvmaze",
            ProviderItemId = "mushoku-tvmaze",
            Title = "Mushoku Tensei: Jobless Reincarnation",
        };
        var tmdbSearch = new Mock<IVideoMetadataSearchProvider>();
        ConfigureProvider(tmdbSearch, "tmdb", VideoMetadataCapabilities.Search);
        tmdbSearch
            .Setup(provider => provider.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([tmdbCandidate]);
        var tvMazeSearch = new Mock<IVideoMetadataSearchProvider>();
        ConfigureProvider(tvMazeSearch, "tvmaze", VideoMetadataCapabilities.Search);
        tvMazeSearch
            .Setup(provider => provider.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([tvMazeCandidate]);

        var tmdbDetails = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tmdbDetails, "tmdb", VideoMetadataCapabilities.Details);
        tmdbDetails
            .Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(tmdbCandidate, 2));
        var tvMazeDetails = new Mock<IVideoMetadataDetailsProvider>();
        ConfigureProvider(tvMazeDetails, "tvmaze", VideoMetadataCapabilities.Details);
        tvMazeDetails
            .Setup(provider => provider.GetDetailsAsync(
                It.IsAny<VideoMetadataCandidate>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSeriesDetails(tvMazeCandidate, 3));

        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            [tmdbDetails.Object, tvMazeDetails.Object],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object,
            [tmdbSearch.Object, tvMazeSearch.Object]);

        var result = await service.GetDetailsByTitleAsync(
            ["Mushoku Tensei Isekai Ittara Honki Dasu"],
            VideoMetadataMediaKind.Series,
            2021);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Metadata.ProviderId.Should().Be("tvmaze");
        result.Value.Seasons.Should().HaveCount(3);
    }

    [Fact]
    public async Task DiscoveryService_AnimeLibraryLookup_UsesOnlyAniDbThenTmdb()
    {
        var searchProviders = new Dictionary<string, Mock<IVideoMetadataSearchProvider>>(
            StringComparer.OrdinalIgnoreCase);
        var detailsProviders = new List<IVideoMetadataDetailsProvider>();
        foreach (var providerId in new[] { "anidb", "tmdb", "anilist", "bangumi", "tvmaze" })
        {
            var search = new Mock<IVideoMetadataSearchProvider>();
            ConfigureProvider(search, providerId, VideoMetadataCapabilities.Search);
            search.Setup(provider => provider.SearchAsync(
                    It.IsAny<VideoMetadataSearchQuery>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            searchProviders[providerId] = search;

            var details = new Mock<IVideoMetadataDetailsProvider>();
            ConfigureProvider(details, providerId, VideoMetadataCapabilities.Details);
            detailsProviders.Add(details.Object);
        }
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        var service = new VideoDiscoveryService(
            [],
            detailsProviders,
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object,
            searchProviders.Values.Select(provider => provider.Object));

        await service.GetDetailsByTitleAsync(
            ["Re:Zero kara Hajimeru Isekai Seikatsu"],
            VideoMetadataMediaKind.Anime,
            2016);

        searchProviders["anidb"].Verify(provider => provider.SearchAsync(
            It.IsAny<VideoMetadataSearchQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        searchProviders["tmdb"].Verify(provider => provider.SearchAsync(
            It.IsAny<VideoMetadataSearchQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        foreach (var providerId in new[] { "anilist", "bangumi", "tvmaze" })
        {
            searchProviders[providerId].Verify(provider => provider.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private static Mock<IVideoMetadataSearchProvider> CreateSearchProvider(
        string providerId,
        Func<VideoMetadataSearchQuery, IReadOnlyList<VideoMetadataCandidate>> search)
    {
        var provider = new Mock<IVideoMetadataSearchProvider>();
        ConfigureProvider(provider, providerId, VideoMetadataCapabilities.Search);
        provider.Setup(value => value.SearchAsync(
                It.IsAny<VideoMetadataSearchQuery>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoMetadataSearchQuery, CancellationToken>((query, _) =>
                Task.FromResult(search(query)));
        return provider;
    }

    private static Mock<IVideoDiscoveryProvider> CreateDiscoveryProvider(
        string providerId,
        Func<VideoDiscoveryRequest, VideoDiscoveryPage> load)
    {
        var provider = new Mock<IVideoDiscoveryProvider>();
        provider.SetupGet(value => value.Id).Returns(providerId);
        provider.SetupGet(value => value.DisplayName).Returns(providerId);
        provider.SetupGet(value => value.Feeds).Returns([]);
        provider.Setup(value => value.GetPageAsync(
                It.IsAny<VideoDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoDiscoveryRequest, CancellationToken>((request, _) =>
                Task.FromResult(load(request)));
        return provider;
    }

    private static VideoDiscoveryService CreateOnlineDiscoveryService(
        params IVideoMetadataSearchProvider[] searchProviders)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        return new VideoDiscoveryService(
            [],
            [],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object,
            searchProviders);
    }

    private static VideoDiscoveryService CreateOnlineBrowseService(
        params IVideoDiscoveryProvider[] providers)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        return new VideoDiscoveryService(
            providers,
            [],
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);
    }

    private static VideoDiscoveryService CreateOnlineDetailsService(
        params IVideoMetadataDetailsProvider[] detailsProviders)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                Metadata = new VideoMetadataSettings { OnlineConsentAccepted = true },
            },
        });
        return new VideoDiscoveryService(
            [],
            detailsProviders,
            [],
            Mock.Of<IVideoMetadataTransport>(),
            Mock.Of<IVideoArtworkCache>(),
            settings.Object);
    }

    private static VideoMetadataCandidate CreateSearchCandidate(
        string providerId,
        string providerItemId,
        VideoMetadataMediaKind mediaKind,
        string title,
        int? year,
        ImmutableArray<string> aliases = default,
        ImmutableDictionary<string, string>? externalIds = null) => new(
        providerId,
        providerItemId,
        mediaKind,
        title,
        null,
        year,
        null,
        null,
        null,
        aliases.IsDefault ? [title] : aliases,
        externalIds ?? ImmutableDictionary<string, string>.Empty.Add(providerId, providerItemId),
        null);

    private static VideoDiscoveryItem CreateDiscoveryItem(
        string providerId,
        string providerItemId,
        VideoMetadataMediaKind mediaKind,
        string title) => new(
        CreateSearchCandidate(providerId, providerItemId, mediaKind, title, 2026),
        null,
        null,
        null,
        null,
        null);

    private static VideoMetadataDetails BuildSeriesDetails(
        VideoMetadataCandidate candidate,
        int seasonCount) =>
        new(
            candidate.ProviderId,
            candidate.ProviderItemId,
            candidate.MediaKind,
            candidate.Title,
            candidate.OriginalTitle,
            null,
            "Overview",
            candidate.Year,
            null,
            null,
            null,
            candidate.Aliases,
            [],
            [],
            candidate.ExternalIds,
            candidate.SourceUrl,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            Seasons: Enumerable.Range(1, seasonCount)
                .Select(season => new VideoMetadataSeason(
                    season,
                    $"Season {season}",
                    null,
                    null,
                    1,
                    null,
                    [new VideoMetadataEpisode(
                        1,
                        $"Episode {season}",
                        null,
                        null,
                        null,
                        24,
                        null,
                        null)]))
                .ToImmutableArray());

    private static void ConfigureProvider<T>(
        Mock<T> provider,
        string id,
        VideoMetadataCapabilities capabilities)
        where T : class, IVideoMetadataProvider
    {
        provider.SetupGet(value => value.Id).Returns(id);
        provider.SetupGet(value => value.DisplayName).Returns(id);
        provider.SetupGet(value => value.Capabilities).Returns(capabilities);
        provider.SetupGet(value => value.SupportedMediaKinds)
            .Returns(new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Movie });
        provider.SetupGet(value => value.ArtworkEnabledByDefault).Returns(true);
        provider.SetupGet(value => value.AttributionUrl).Returns((string?)null);
    }

    private sealed class FixtureTransport(
        string? json,
        int statusCode = 200,
        bool cancel = false) : IVideoMetadataTransport
    {
        public VideoMetadataRequest? LastRequest { get; private set; }

        public Task<VideoMetadataResponse> SendAsync(
            VideoMetadataRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (cancel)
                return Task.FromCanceled<VideoMetadataResponse>(new CancellationToken(true));
            return Task.FromResult(new VideoMetadataResponse(
                statusCode,
                Encoding.UTF8.GetBytes(json ?? "{}"),
                "application/json",
                null,
                null,
                DateTimeOffset.UtcNow,
                false));
        }
    }

    private sealed class FixtureCredentialStore(string token = "token") : IVideoMetadataCredentialStore
    {
        public Task<string?> ReadAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.FromResult<string?>(token);
        public Task WriteAsync(string providerId, string secretName, string value, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FixtureArtworkCache : IVideoArtworkCache
    {
        private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> StoredUrls => _paths.Keys;

        public string PathFor(string url) => _paths[url];

        public Task<VideoArtworkCacheEntry?> GetAsync(
            string url,
            CancellationToken ct = default) =>
            Task.FromResult<VideoArtworkCacheEntry?>(null);

        public Task<VideoArtworkCacheEntry> StoreAsync(
            string url,
            Stream content,
            string? contentType,
            string? etag,
            DateTimeOffset? lastModified,
            CancellationToken ct = default)
        {
            lock (_paths)
            {
                if (_paths.TryGetValue(url, out var existing))
                    return Task.FromResult(new VideoArtworkCacheEntry(
                        existing,
                        url,
                        etag,
                        lastModified,
                        5,
                        DateTimeOffset.UtcNow));

                var path = $"cache://{_paths.Count + 1}";
                _paths[url] = path;
                return Task.FromResult(new VideoArtworkCacheEntry(
                    path,
                    url,
                    etag,
                    lastModified,
                    5,
                    DateTimeOffset.UtcNow));
            }
        }

        public Task TrimAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
