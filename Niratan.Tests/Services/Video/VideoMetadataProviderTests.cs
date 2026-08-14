using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Video;
using Niratan.Services.Video;
using Niratan.Services.Storage;
using Niratan.Services.Novels;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoMetadataProviderTests
{
    [Fact]
    public async Task TmdbSearch_UsesCredentialAndParsesFixtureWithoutLiveNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"results":[{"id":123,"name":"アンナチュラル","original_name":"アンナチュラル","first_air_date":"2018-01-12"}]}
            """);
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var query = new VideoMetadataSearchQuery(
            "アンナチュラル", VideoMetadataMediaKind.Series, 2018, null, null, null,
            "ja-JP", "JP", ImmutableDictionary<string, string>.Empty);

        var candidates = await provider.SearchAsync(query, ct);

        candidates.Should().ContainSingle().Which.ProviderItemId.Should().Be("123");
        transport.LastRequest!.Headers!["Authorization"].Should().Be("Bearer secret");
        transport.LastRequest.Uri.Host.Should().Be("api.themoviedb.org");
    }

    [Fact]
    public async Task TmdbDetails_ProjectsComprehensiveSeriesMetadataFromFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {
              "id":123,"name":"作品","original_name":"作品 原題","overview":"概要",
              "first_air_date":"2020-01-01","last_air_date":"2024-03-01","status":"Returning Series",
              "tagline":"物語は続く","vote_average":8.25,
              "genres":[{"name":"Animation"}],
              "production_companies":[{"name":"Studio A"}],
              "external_ids":{"imdb_id":"tt123","tvdb_id":456},
              "credits":{"cast":[{"id":7,"name":"声優 A","character":"主人公","profile_path":"/person.jpg"}]},
              "keywords":{"results":[{"name":"time travel"}]},
              "content_ratings":{"results":[{"iso_3166_1":"JP","rating":"PG12"}]},
              "recommendations":{"results":[{"id":99,"name":"関連作品","original_name":"Related","first_air_date":"2021-01-01","poster_path":"/p.jpg","backdrop_path":"/b.jpg"}]}
            }
            """);
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var candidate = new VideoMetadataCandidate(
            "tmdb", "123", VideoMetadataMediaKind.Series, "作品", null, 2020,
            null, null, null, ["作品"], ImmutableDictionary<string, string>.Empty,
            "https://www.themoviedb.org/tv/123");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.Tagline.Should().Be("物語は続く");
        details.OfficialRating.Should().Be("PG12");
        details.CommunityRating.Should().Be(8.25);
        details.EndYear.Should().Be(2024);
        details.Status.Should().Be("Returning Series");
        details.Tags.Should().Contain("time travel");
        details.Studios.Should().Contain("Studio A");
        details.People.Should().ContainSingle(person => person.Name == "声優 A" && person.Role == "主人公");
        details.RelatedItems.Should().ContainSingle(item => item.ProviderItemId == "99");
        transport.LastRequest!.Uri.Query.Should().Contain("recommendations");
    }

    [Fact]
    public async Task AniListDetails_ProjectsRichSeriesTextFromFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Media":{"id":20987,"idMal":28825,
              "title":{"romaji":"Himouto! Umaru-chan","english":"Himouto! Umaru-chan","native":"干物妹！うまるちゃん"},
              "synonyms":[],"description":"概要","seasonYear":2015,"endDate":{"year":2015},
              "status":"FINISHED","averageScore":71,"genres":["Comedy"],
              "tags":[{"name":"School"}],"studios":{"nodes":[{"name":"Doga Kobo","isAnimationStudio":true}]},
              "characters":{"edges":[{"role":"MAIN","node":{"name":{"full":"Umaru Doma","native":"土間うまる"}},
                "voiceActors":[{"id":100,"name":{"full":"Aimi Tanaka","native":"田中あいみ"},"image":{"large":"https://img.test/person.jpg"},"siteUrl":"https://anilist.co/staff/100"}]}]},
              "recommendations":{"nodes":[{"mediaRecommendation":{"id":21268,"title":{"romaji":"Related","english":null,"native":"関連作品"},"seasonYear":2016,"coverImage":{"large":"https://img.test/poster.jpg"},"bannerImage":"https://img.test/backdrop.jpg","siteUrl":"https://anilist.co/anime/21268"}}]},
              "siteUrl":"https://anilist.co/anime/20987","externalLinks":[]}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var candidate = new VideoMetadataCandidate(
            "anilist", "20987", VideoMetadataMediaKind.Anime, "干物妹！うまるちゃん", null, 2015,
            null, 8, 8, ["Himouto! Umaru-chan"],
            ImmutableDictionary<string, string>.Empty.Add("anilist", "20987"),
            "https://anilist.co/anime/20987");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.OriginalTitle.Should().Be("干物妹！うまるちゃん");
        details.CommunityRating.Should().Be(7.1);
        details.Status.Should().Be("FINISHED");
        details.Tags.Should().Contain("School");
        details.Studios.Should().Contain("Doga Kobo");
        details.People.Should().ContainSingle(person => person.Name == "田中あいみ" && person.Role == "土間うまる");
        details.RelatedItems.Should().ContainSingle(item => item.ProviderItemId == "21268");
    }

    [Fact]
    public async Task AniListTitleSearch_OmitsUnusedNullIdFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Page":{"media":[{"id":20987,"idMal":28825,
              "title":{"romaji":"Himouto! Umaru-chan","english":"Himouto! Umaru-chan","native":"干物妹！うまるちゃん"},
              "synonyms":[],"seasonYear":2015,"siteUrl":"https://anilist.co/anime/20987"}]}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var query = new VideoMetadataSearchQuery(
            "Himouto! Umaru-chan", VideoMetadataMediaKind.Anime, null, null, 8, 8,
            "ja-JP", "JP", ImmutableDictionary<string, string>.Empty.Add("anidb", "10972"));

        var candidates = await provider.SearchAsync(query, ct);

        candidates.Should().ContainSingle().Which.ProviderItemId.Should().Be("20987");
        using var body = JsonDocument.Parse(transport.LastRequest!.Body!);
        var variables = body.RootElement.GetProperty("variables");
        variables.GetProperty("search").GetString().Should().Be("Himouto! Umaru-chan");
        variables.TryGetProperty("id", out _).Should().BeFalse();
        variables.TryGetProperty("idMal", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AniListArtwork_ProvidesPortraitPosterAndLandscapeBackdrop()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Media":{
              "coverImage":{"extraLarge":"https://img.test/poster-xl.jpg","large":"https://img.test/poster.jpg","medium":"https://img.test/poster-small.jpg"},
              "bannerImage":"https://img.test/backdrop.jpg",
              "siteUrl":"https://anilist.co/anime/20987"}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var candidate = new VideoMetadataCandidate(
            "anilist", "20987", VideoMetadataMediaKind.Anime, "干物妹！うまるちゃん", null,
            2015, null, null, null, ["Himouto! Umaru-chan"],
            ImmutableDictionary<string, string>.Empty.Add("anilist", "20987"),
            "https://anilist.co/anime/20987");

        var artwork = await provider.GetArtworkAsync(candidate, ct);

        artwork.Should().Contain(item => item.Kind == "poster"
                                         && item.Url == "https://img.test/poster-xl.jpg");
        artwork.Should().Contain(item => item.Kind == "backdrop"
                                         && item.Url == "https://img.test/backdrop.jpg");
        provider.ArtworkEnabledByDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Transport_DoesNotRetryAuthenticationFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new SequenceHandler(HttpStatusCode.Unauthorized);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance);

        var response = await transport.SendAsync(new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/movie/1")), ct);

        response.StatusCode.Should().Be(401);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Transport_RespectsRetryAfterFor429()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance);

        var response = await transport.SendAsync(new VideoMetadataRequest(
            "tvmaze", HttpMethod.Get, new Uri("https://api.tvmaze.com/search/shows?q=test")), ct);

        response.StatusCode.Should().Be(200);
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task LocalNfo_DisablesExternalEntitiesAndNeverChangesSidecars()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var media = Path.Combine(temp.Path, "Episode 01.mkv");
        var nfo = Path.Combine(temp.Path, "Episode 01.nfo");
        await File.WriteAllBytesAsync(media, [1, 2, 3], ct);
        await File.WriteAllTextAsync(nfo, "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///c:/windows/win.ini'>]><episodedetails><title>&leak;</title></episodedetails>", ct);
        var before = await File.ReadAllBytesAsync(nfo, ct);
        var provider = new LocalVideoMetadataProvider();

        var action = () => provider.ReadAsync(media, temp.Path, ct);

        await action.Should().ThrowAsync<XmlException>();
        (await File.ReadAllBytesAsync(nfo, ct)).Should().Equal(before);
    }

    [Fact]
    public async Task LocalArtwork_EnumeratesControlledNamesInStablePriorityOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var media = Path.Combine(temp.Path, "Episode 01.mkv");
        await File.WriteAllBytesAsync(media, [1], ct);
        foreach (var name in new[]
                 {
                     "season01-poster.jpg", "backdrop.png", "folder.webp",
                     "Episode 01.jpeg", "unrelated.jpg",
                 })
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, name), [1], ct);

        var metadata = await new LocalVideoMetadataProvider().ReadAsync(media, temp.Path, ct);

        metadata.ArtworkPaths.Select(Path.GetFileName).Should().Equal(
            "Episode 01.jpeg", "folder.webp", "backdrop.png", "season01-poster.jpg");
        metadata.ArtworkPaths.Should().NotContain(path => Path.GetFileName(path) == "unrelated.jpg");
    }

    [Fact]
    public async Task Transport_ReusesFreshCatalogCacheWithoutSecondNetworkRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            new NiratanJsonFileStore(),
            NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        var handler = new CacheHandler(HttpStatusCode.OK);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance,
            repository);
        var request = new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/movie/1"));

        var first = await transport.SendAsync(request, ct);
        var second = await transport.SendAsync(request, ct);

        first.Content.Should().Equal(Encoding.UTF8.GetBytes("{\"title\":\"cached\"}"));
        second.Content.Should().Equal(first.Content);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Transport_CoalescesConcurrentIdenticalQueriesIntoOneNetworkRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            new NiratanJsonFileStore(),
            NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        var handler = new DelayedCacheHandler();
        var transport = new VideoMetadataTransport(
            new HttpClient(handler), TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance, repository);
        var request = new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/search/tv?query=same"));

        var responses = await Task.WhenAll(
            transport.SendAsync(request, ct),
            transport.SendAsync(request, ct),
            transport.SendAsync(request, ct));

        handler.RequestCount.Should().Be(1);
        responses.Should().OnlyContain(response => response.StatusCode == 200);
        responses.Select(response => response.Content)
            .Should().OnlyContain(content => content.SequenceEqual(responses[0].Content));
    }

    [Fact]
    public async Task ArtworkCache_ValidatesImageAndAtomicallyReusesStoredEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var cache = new VideoArtworkCache(temp.Path);
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];

        var stored = await cache.StoreAsync(
            "https://image.tmdb.org/t/p/original/poster.png",
            new MemoryStream(png), "image/png", "\"v1\"", null, ct);
        var loaded = await cache.GetAsync(stored.Url, ct);
        var invalid = () => cache.StoreAsync(
            "https://image.tmdb.org/t/p/original/not-image",
            new MemoryStream([1, 2, 3, 4]), "text/plain", null, null, ct);

        loaded.Should().NotBeNull();
        loaded!.LocalPath.Should().Be(stored.LocalPath);
        File.Exists(stored.LocalPath).Should().BeTrue();
        await invalid.Should().ThrowAsync<InvalidDataException>();
        Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();
    }

    private sealed class FixtureTransport(string json) : IVideoMetadataTransport
    {
        public VideoMetadataRequest? LastRequest { get; private set; }
        public Task<VideoMetadataResponse> SendAsync(VideoMetadataRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new VideoMetadataResponse(
                200, Encoding.UTF8.GetBytes(json), "application/json", null, null, DateTimeOffset.UtcNow, false));
        }
    }

    private sealed class FixtureCredentialStore(string token) : IVideoMetadataCredentialStore
    {
        public Task<string?> ReadAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.FromResult<string?>(token);
        public Task WriteAsync(string providerId, string secretName, string value, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    private sealed class CacheHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"title\":\"cached\"}")),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedCacheHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(80, ct);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"title\":\"shared\"}")),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"shared-v1\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }
}
