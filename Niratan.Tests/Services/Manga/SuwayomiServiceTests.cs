using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class SuwayomiServiceTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4567/api/v1/", "http://127.0.0.1:4567/")]
    [InlineData("https://example.test/base/api/graphql", "https://example.test/base")]
    public void NormalizeServerUri_StripsApiSuffix(string input, string expected)
    {
        SuwayomiService.NormalizeServerUri(input).AbsoluteUri
            .TrimEnd('/')
            .Should().Be(expected.TrimEnd('/'));
    }

    [Fact]
    public async Task ConnectAsync_UsesInstalledSourceEndpointAndBasicAuthentication()
    {
        using var temp = new TempDirectory();
        var handler = new RecordingHandler(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/source/list");
            request.Headers.Authorization!.Scheme.Should().Be("Basic");
            Encoding.UTF8.GetString(Convert.FromBase64String(
                    request.Headers.Authorization.Parameter!))
                .Should().Be("reader:secret");
            return Json("""[{"id":"42","name":"MangaDex","lang":"en","displayName":"MangaDex"}]""");
        });
        using var service = CreateService(temp, handler);
        var configuration = new SuwayomiServerConfiguration
        {
            ServerUrl = "https://example.test",
            AuthMode = SuwayomiAuthMode.Basic,
            Username = "reader",
        };

        var sources = await service.ConnectAsync(
            configuration,
            "secret",
            TestContext.Current.CancellationToken);

        sources.Should().ContainSingle()
            .Which.Label.Should().Be("[en] MangaDex");
    }

    [Fact]
    public async Task GetPagePathAsync_DownloadsOnceAndUsesContentTypeExtension()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        var handler = new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.AbsolutePath.Should().Be(
                "/api/v1/manga/11/chapter/4/page/2");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = CreateService(temp, handler);
        var configuration = new SuwayomiServerConfiguration
        {
            ServerUrl = "https://example.test",
        };
        await service.SaveConfigurationAsync(
            configuration,
            null,
            TestContext.Current.CancellationToken);
        var serverId = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes("https://example.test")))
            .ToLowerInvariant();
        var book = new MangaBook
        {
            Id = "remote",
            SourcePath = "https://example.test",
            ContainerKind = MangaContainerKind.Suwayomi,
            SuwayomiServerId = serverId,
            SuwayomiMangaId = 11,
            SuwayomiChapterIndex = 4,
            Pages = Enumerable.Range(0, 3)
                .Select(index => new MangaPageDescriptor
                {
                    Index = index,
                    Path = index.ToString(),
                })
                .ToList(),
        };

        var first = await service.GetPagePathAsync(
            book,
            2,
            TestContext.Current.CancellationToken);
        var second = await service.GetPagePathAsync(
            book,
            2,
            TestContext.Current.CancellationToken);

        first.Should().Be(second);
        Path.GetExtension(first).Should().Be(".png");
        File.ReadAllBytes(first).Should().Equal(1, 2, 3, 4);
        requests.Should().Be(1);
    }

    [Fact]
    public async Task GetLibraryAsync_MergesSuwayomiCategoriesWithoutDuplicates()
    {
        using var temp = new TempDirectory();
        var paths = new List<string>();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path switch
            {
                "/api/v1/category" => Json(
                    """[{"id":1,"name":"Default"},{"id":2,"name":"Reading"}]"""),
                "/api/v1/category/1" => Json(
                    """[{"id":11,"title":"Zulu"},{"id":12,"title":"Alpha"}]"""),
                "/api/v1/category/2" => Json(
                    """[{"id":11,"title":"Zulu"}]"""),
                _ => throw new InvalidOperationException(path),
            };
        });
        using var service = CreateService(temp, handler);

        var library = await service.GetLibraryAsync(
            new SuwayomiServerConfiguration
            {
                ServerUrl = "https://example.test",
            },
            null,
            TestContext.Current.CancellationToken);

        library.Select(manga => manga.Title).Should().Equal("Alpha", "Zulu");
        paths.Should().Equal(
            "/api/v1/category",
            "/api/v1/category/1",
            "/api/v1/category/2");
    }

    [Fact]
    public async Task MangaDetailsAndLibraryActions_UseSuwayomiOwnedEndpoints()
    {
        using var temp = new TempDirectory();
        var requests = new List<(HttpMethod Method, string Path, string Query)>();
        var handler = new RecordingHandler(request =>
        {
            requests.Add((
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query));
            if (request.RequestUri.AbsolutePath == "/api/v1/manga/17/full")
            {
                return Json(
                    """
                    {
                      "id":17,
                      "title":"Detailed title",
                      "author":"Author",
                      "description":"Description",
                      "inLibrary":false
                    }
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var service = CreateService(temp, handler);
        var configuration = new SuwayomiServerConfiguration
        {
            ServerUrl = "https://example.test",
        };

        var details = await service.GetMangaDetailsAsync(
            configuration,
            null,
            17,
            TestContext.Current.CancellationToken);
        await service.SetLibraryAsync(
            configuration,
            null,
            17,
            isInLibrary: true,
            TestContext.Current.CancellationToken);
        await service.SetLibraryAsync(
            configuration,
            null,
            17,
            isInLibrary: false,
            TestContext.Current.CancellationToken);

        details.Title.Should().Be("Detailed title");
        requests.Should().Equal(
            (HttpMethod.Get, "/api/v1/manga/17/full", "?onlineFetch=true"),
            (HttpMethod.Get, "/api/v1/manga/17/library", string.Empty),
            (HttpMethod.Delete, "/api/v1/manga/17/library", string.Empty));
    }

    [Fact]
    public async Task GetThumbnailPathAsync_CachesCoverByServerAndManga()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        var handler = new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.AbsolutePath.Should().Be(
                "/api/v1/manga/17/thumbnail");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 3, 2, 1]),
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("image/webp");
            return response;
        });
        using var service = CreateService(temp, handler);
        var configuration = new SuwayomiServerConfiguration
        {
            ServerUrl = "https://example.test",
        };

        var first = await service.GetThumbnailPathAsync(
            configuration,
            null,
            17,
            TestContext.Current.CancellationToken);
        var second = await service.GetThumbnailPathAsync(
            configuration,
            null,
            17,
            TestContext.Current.CancellationToken);

        second.Should().Be(first);
        Path.GetExtension(first).Should().Be(".webp");
        File.ReadAllBytes(first).Should().Equal(4, 3, 2, 1);
        requests.Should().Be(1);
    }

    [Fact]
    public async Task GetSourceIconPathAsync_UsesReportedApiUrlAndCachesImage()
    {
        using var temp = new TempDirectory();
        var requests = 0;
        var handler = new RecordingHandler(request =>
        {
            requests++;
            request.RequestUri!.AbsolutePath.Should().Be(
                "/api/v1/extension/icon/tachiyomi-en.mangadex.png");
            request.Headers.Accept.Should().ContainSingle()
                .Which.MediaType.Should().Be("image/*");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([9, 8, 7, 6]),
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = CreateService(temp, handler);
        var configuration = new SuwayomiServerConfiguration
        {
            ServerUrl = "https://example.test",
        };
        var source = new SuwayomiSource
        {
            Id = "42",
            DisplayName = "MangaDex",
            IconUrl = "/api/v1/extension/icon/tachiyomi-en.mangadex.png",
        };

        var first = await service.GetSourceIconPathAsync(
            configuration,
            null,
            source,
            TestContext.Current.CancellationToken);
        var second = await service.GetSourceIconPathAsync(
            configuration,
            null,
            source,
            TestContext.Current.CancellationToken);

        second.Should().Be(first);
        first.Should().NotBeNull();
        Path.GetExtension(first!).Should().Be(".png");
        File.ReadAllBytes(first).Should().Equal(9, 8, 7, 6);
        requests.Should().Be(1);
    }

    [Fact]
    public void GetSourceIconApiPath_RejectsCrossOriginUrls()
    {
        var act = () => SuwayomiService.GetSourceIconApiPath(
            new Uri("https://example.test"),
            "https://untrusted.test/api/v1/extension/icon/source.png");

        act.Should().Throw<InvalidDataException>();
    }

    private static SuwayomiService CreateService(
        TempDirectory temp,
        HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new MemoryCredentialStore(),
            Path.Combine(temp.Path, "suwayomi.json"),
            Path.Combine(temp.Path, "cache"));

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class MemoryCredentialStore : ISuwayomiCredentialStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> ReadAsync(string credentialId) =>
            Task.FromResult(_values.GetValueOrDefault(credentialId));

        public Task WriteAsync(string credentialId, string secret)
        {
            _values[credentialId] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string credentialId)
        {
            _values.Remove(credentialId);
            return Task.CompletedTask;
        }
    }
}
