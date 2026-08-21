using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaDiscoveryServiceTests
{
    [Fact]
    public void ProvidersExposeBangumiAndAniListRecommendationFeeds()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(_ => Json("{}")));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        service.Providers.Select(provider => provider.Id)
            .Should().BeEquivalentTo(["bangumi", "anilist"], options => options.WithStrictOrdering());
        service.GetFeeds("bangumi", MangaDiscoveryFeedKind.Recommendation)
            .Select(feed => feed.Id)
            .Should().BeEquivalentTo(["rank", "heat", "date"], options => options.WithStrictOrdering());
        service.GetFeeds("anilist", MangaDiscoveryFeedKind.Recommendation)
            .Select(feed => feed.Id)
            .Should().BeEquivalentTo(["trending", "popular", "updated"], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task BangumiPageMapsMangaMetadataAndPagination()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.Host.Should().Be("api.bgm.tv");
            request.RequestUri.Query.Should().Contain("type=1");
            request.RequestUri.Query.Should().Contain("cat=1001");
            request.RequestUri.Query.Should().Contain("series=true");
            request.RequestUri.Query.Should().Contain("sort=rank");
            request.RequestUri.Query.Should().Contain("limit=12");
            return Json(
                """
                {
                  "data": [
                    {
                      "id": 1,
                      "name": "One Piece",
                      "name_cn": "海贼王",
                      "date": "1997-07-22",
                      "summary": "A pirate adventure.",
                      "images": {"large": "https://lain.bgm.tv/pic/cover/l/1.jpg"},
                      "rating": {"score": 9.1, "rank": 2}
                    }
                  ],
                  "total": 25
                }
                """);
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var page = await service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("rank"),
            TestContext.Current.CancellationToken);

        page.HasMore.Should().BeTrue();
        page.Items.Should().ContainSingle();
        var item = page.Items[0];
        item.Title.Should().Be("海贼王");
        item.OriginalTitle.Should().Be("One Piece");
        item.Year.Should().Be(1997);
        item.Score.Should().Be(9.1);
        item.Rank.Should().Be(2);
        item.PosterUrl.Should().Be("https://lain.bgm.tv/pic/cover/l/1.jpg");
    }

    [Fact]
    public async Task BangumiHeatUsesSearchEndpointWithMangaFilter()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/v0/search/subjects");
            request.RequestUri.Query.Should().Contain("limit=12");
            request.RequestUri.Query.Should().Contain("offset=12");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("keyword").GetString().Should().BeEmpty();
            body.RootElement.GetProperty("sort").GetString().Should().Be("heat");
            var filter = body.RootElement.GetProperty("filter");
            filter.GetProperty("type")[0].GetInt32().Should().Be(1);
            filter.GetProperty("meta_tags")[0].GetString().Should().Be("漫画");
            request.RequestUri.Query.Should().NotContain("sort=heat");
            return Json("""{"data":[],"total":25}""");
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var page = await service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("heat", 2),
            TestContext.Current.CancellationToken);

        page.FeedId.Should().Be("heat");
        page.Page.Should().Be(2);
        page.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task BangumiLatestUsesSupportedDateSort()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.Should().Be("/v0/subjects");
            request.RequestUri.Query.Should().Contain("sort=date");
            request.RequestUri.Query.Should().Contain("cat=1001");
            request.RequestUri.Query.Should().NotContain("sort=heat");
            request.RequestUri.Query.Should().NotContain("sort=score");
            return Json("""{"data":[],"total":0}""");
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var page = await service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("date"),
            TestContext.Current.CancellationToken);

        page.FeedId.Should().Be("date");
    }

    [Fact]
    public async Task AniListSearchPostsMangaGraphqlQueryAndMapsScores()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.Host.Should().Be("graphql.anilist.co");
            var body = await request.Content!.ReadAsStringAsync();
            body.Should().Contain("type:MANGA");
            body.Should().Contain("search:$search");
            body.Should().Contain("SEARCH_MATCH");
            using var payload = JsonDocument.Parse(body);
            payload.RootElement.GetProperty("variables").GetProperty("perPage")
                .GetInt32().Should().Be(24);
            return Json(
                """
                {
                  "data": {
                    "Page": {
                      "pageInfo": {"lastPage": 2},
                      "media": [
                        {
                          "id": 42,
                          "title": {"romaji": "One Piece", "english": "One Piece", "native": "ワンピース"},
                          "synonyms": ["OP"],
                          "description": "Pirates.<br>&amp; rivals.",
                          "startDate": {"year": 1997},
                          "averageScore": 91,
                          "coverImage": {"extraLarge": "https://s4.anilist.co/file/anilistcdn/media/manga/cover/large/bx42-a.jpg"},
                          "siteUrl": "https://anilist.co/manga/42"
                        }
                      ]
                    }
                  }
                }
                """);
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var page = await service.SearchAsync(
            "anilist",
            "One Piece",
            1,
            TestContext.Current.CancellationToken);

        page.HasMore.Should().BeTrue();
        var item = page.Items.Should().ContainSingle().Subject;
        item.Title.Should().Be("ワンピース");
        item.OriginalTitle.Should().Be("One Piece");
        item.Score.Should().Be(9.1);
        item.Overview.Should().Be($"Pirates.{Environment.NewLine}& rivals.");
        item.Aliases.Should().Contain("OP");
    }

    [Fact]
    public async Task AniListRecommendationQueryDoesNotDeclareUnusedSearchVariable()
    {
        using var temp = new TempDirectory();
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            body.Should().Contain("TRENDING_DESC");
            body.Should().NotContain("$search:String");
            body.Should().NotContain("search:$search");
            body.Should().NotContain("idMal");
            body.Should().NotContain("popularity");
            using var payload = JsonDocument.Parse(body);
            payload.RootElement.GetProperty("variables").GetProperty("perPage")
                .GetInt32().Should().Be(12);
            return Json(
                """
                {
                  "data": {
                    "Page": {
                      "pageInfo": {"lastPage": 1},
                      "media": []
                    }
                  }
                }
                """);
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var page = await service.GetPageAsync(
            "anilist",
            new MangaDiscoveryRequest("trending"),
            TestContext.Current.CancellationToken);

        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AniListRecommendationsBatchThreeFeedsIntoOneRequest()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(async request =>
        {
            requests++;
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.Host.Should().Be("graphql.anilist.co");
            var body = await request.Content!.ReadAsStringAsync();
            body.Should().Contain("feed0:Page");
            body.Should().Contain("feed1:Page");
            body.Should().Contain("feed2:Page");
            body.Should().Contain("TRENDING_DESC");
            body.Should().Contain("POPULARITY_DESC");
            body.Should().Contain("UPDATED_AT_DESC");
            body.Should().Contain("perPage:12");
            body.Should().NotContain("$search:String");
            return Json(
                """
                {
                  "data": {
                    "feed0": {"pageInfo":{"lastPage":1},"media":[]},
                    "feed1": {"pageInfo":{"lastPage":2},"media":[]},
                    "feed2": {"pageInfo":{"lastPage":3},"media":[]}
                  }
                }
                """);
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var batchService = (IMangaDiscoveryBatchService)service;
        var pages = await batchService.GetPagesAsync(
            "anilist",
            [
                new MangaDiscoveryRequest("trending"),
                new MangaDiscoveryRequest("popular"),
                new MangaDiscoveryRequest("updated"),
            ],
            TestContext.Current.CancellationToken);

        var cachedPages = await batchService.GetPagesAsync(
            "anilist",
            [
                new MangaDiscoveryRequest("trending"),
                new MangaDiscoveryRequest("popular"),
                new MangaDiscoveryRequest("updated"),
            ],
            TestContext.Current.CancellationToken);

        requests.Should().Be(1);
        pages.Select(page => page.FeedId).Should().Equal("trending", "popular", "updated");
        pages.Select(page => page.TotalPages).Should().Equal(1, 2, 3);
        cachedPages.Should().Equal(pages);

        service.ClearCache();
        await batchService.GetPagesAsync(
            "anilist",
            [
                new MangaDiscoveryRequest("trending"),
                new MangaDiscoveryRequest("popular"),
                new MangaDiscoveryRequest("updated"),
            ],
            TestContext.Current.CancellationToken);
        requests.Should().Be(2);
    }

    [Fact]
    public async Task DiscoveryPageCacheAvoidsDuplicateRequestsUntilCleared()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(_ =>
        {
            requests++;
            return Json("""{"data":[],"total":0}""");
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var first = await service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("rank"),
            TestContext.Current.CancellationToken);
        var second = await service.GetPageAsync(
            "BANGUMI",
            new MangaDiscoveryRequest("RANK"),
            TestContext.Current.CancellationToken);

        requests.Should().Be(1);
        second.Should().BeSameAs(first);

        service.ClearCache();
        await service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("rank"),
            TestContext.Current.CancellationToken);
        requests.Should().Be(2);
    }

    [Fact]
    public async Task PosterDownloadUsesAllowlistedCacheAndReusesIt()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        var validPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZlZsAAAAASUVORK5CYII=");
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.Host.Should().Be("lain.bgm.tv");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(validPng),
            };
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));
        var item = new MangaDiscoveryItem(
            "bangumi",
            "1",
            "海贼王",
            "One Piece",
            null,
            null,
            null,
            null,
            "https://lain.bgm.tv/pic/cover/l/1.png",
            null);

        var paths = await Task.WhenAll(
            service.GetPosterPathAsync(item, TestContext.Current.CancellationToken),
            service.GetPosterPathAsync(item, TestContext.Current.CancellationToken));
        var first = paths[0];
        var second = paths[1];

        first.Should().NotBeNull();
        first.Should().Be(second);
        File.Exists(first!).Should().BeTrue();
        requests.Should().Be(1);

        await File.WriteAllBytesAsync(
            first!,
            validPng.AsMemory(0, 8),
            TestContext.Current.CancellationToken);
        var recovered = await service.GetPosterPathAsync(
            item,
            TestContext.Current.CancellationToken);

        recovered.Should().Be(first);
        requests.Should().Be(2);
        (await File.ReadAllBytesAsync(
            recovered!,
            TestContext.Current.CancellationToken)).Should().Equal(validPng);
    }

    [Theory]
    [InlineData("https://example.invalid/stolen.json")]
    [InlineData("http://api.bgm.tv/v0/subjects")]
    public async Task DiscoveryJsonRejectsUnsafeRedirectWithoutFollowingIt(
        string redirectTarget)
    {
        using var temp = new TempDirectory();
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.Host.Should().Be("api.bgm.tv");
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri(redirectTarget),
                },
            };
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));

        var action = () => service.GetPageAsync(
            "bangumi",
            new MangaDiscoveryRequest("rank"),
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*redirect*");
        requests.Should().Be(1);
    }

    [Theory]
    [InlineData("https://example.invalid/stolen.png")]
    [InlineData("http://lain.bgm.tv/pic/cover/l/1.png")]
    public async Task PosterDownloadRejectsUnsafeRedirectWithoutFollowingIt(
        string redirectTarget)
    {
        using var temp = new TempDirectory();
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.Host.Should().Be("lain.bgm.tv");
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri(redirectTarget),
                },
            };
        }));
        var service = new MangaDiscoveryService(
            http,
            Path.Combine(temp.Path, "posters"));
        var item = new MangaDiscoveryItem(
            "bangumi",
            "1",
            "海贼王",
            "One Piece",
            null,
            null,
            null,
            null,
            "https://lain.bgm.tv/pic/cover/l/1.png",
            null);

        var action = () => service.GetPosterPathAsync(
            item,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*redirect*");
        requests.Should().Be(1);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(request => Task.FromResult(responder(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
