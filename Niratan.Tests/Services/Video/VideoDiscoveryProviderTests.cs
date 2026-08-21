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
    public async Task BangumiCalendar_EmptyResponseIsAnEmptyPage()
    {
        var provider = new BangumiVideoDiscoveryProvider(
            new FixtureTransport("[]"),
            new FixtureCredentialStore());

        var page = await provider.GetPageAsync(new VideoDiscoveryRequest(
            "calendar", VideoMetadataMediaKind.Anime));

        page.Items.Should().BeEmpty();
        page.TotalPages.Should().BeNull();
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
        page.Items[0].CommunityRating.Should().Be(8.2);
        Encoding.UTF8.GetString(transport.LastRequest!.Body!).Should().Contain("seasonYear");
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
    public async Task DiscoveryService_SearchCachesArtworkForSearchCards()
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
            "https://example.test/movie-1");
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
            It.IsAny<CancellationToken>()), Times.Once);
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
    }
}
