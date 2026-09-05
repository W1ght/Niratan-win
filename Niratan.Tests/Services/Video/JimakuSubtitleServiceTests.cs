using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Moq;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class JimakuSubtitleServiceTests
{
    [Fact]
    public async Task Search_uses_anilist_identity_and_returns_only_trusted_text_subtitles()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            requests.Add(CloneRequest(request));
            if (request.RequestUri!.AbsolutePath.EndsWith("/entries/search", StringComparison.Ordinal))
            {
                return Json("[{\"id\":42,\"name\":\"Test Anime\",\"anilist_id\":123}]");
            }
            return Json("["
                + "{\"name\":\"Test Anime - 02.ja.ass\",\"url\":\"https://cdn.jimaku.cc/test.ass\",\"size\":2048},"
                + "{\"name\":\"archive.zip\",\"url\":\"https://cdn.jimaku.cc/archive.zip\"},"
                + "{\"name\":\"evil.srt\",\"url\":\"https://example.com/evil.srt\"}]");
        });
        var credentials = new Mock<IVideoMetadataCredentialStore>();
        credentials.Setup(store => store.ReadAsync("jimaku", "token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("secret-key");
        using var service = new JimakuSubtitleService(
            credentials.Object,
            new HttpClient(handler));

        var result = await service.SearchAsync(new VideoSubtitleSearchRequest(CreateIdentity()));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value![0].FileName.Should().Be("Test Anime - 02.ja.ass");
        result.Value[0].Language.Should().Be("ja");
        result.Value[0].EpisodeNumber.Should().Be(2);
        requests.Should().HaveCount(2);
        requests[0].RequestUri!.Query.Should().Contain("anilist_id=123");
        requests.All(request =>
            request.Headers.TryGetValues("Authorization", out var values)
            && values.Single() == "secret-key").Should().BeTrue();
    }

    [Fact]
    public async Task Search_without_api_key_returns_actionable_failure_without_network_request()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("network should not run"));
        using var service = new JimakuSubtitleService(
            Mock.Of<IVideoMetadataCredentialStore>(),
            new HttpClient(handler));

        var result = await service.SearchAsync(new VideoSubtitleSearchRequest(CreateIdentity()));

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().NotBeNullOrWhiteSpace();
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("https://attacker@cdn.jimaku.cc/test.srt")]
    [InlineData("https://cdn.jimaku.cc:444/test.srt")]
    public async Task Download_rejects_userinfo_and_non_default_ports_without_network_request(
        string downloadUrl)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("network should not run"));
        using var service = new JimakuSubtitleService(
            Mock.Of<IVideoMetadataCredentialStore>(),
            new HttpClient(handler));

        var result = await service.DownloadAsync(
            new JimakuSubtitleItem(
                42,
                "Test Anime",
                "Test Anime - 02.ja.srt",
                new Uri(downloadUrl),
                32,
                "ja",
                2),
            Path.Combine(Path.GetTempPath(), $"niratan-jimaku-{Guid.NewGuid():N}.srt"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Download_refuses_to_overwrite_an_existing_subtitle()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nnew\n")),
        });
        var credentials = new Mock<IVideoMetadataCredentialStore>();
        credentials.Setup(store => store.ReadAsync("jimaku", "token", It.IsAny<CancellationToken>()))
            .ReturnsAsync("secret-key");
        using var service = new JimakuSubtitleService(credentials.Object, new HttpClient(handler));
        var directory = Path.Combine(Path.GetTempPath(), $"niratan-jimaku-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "existing.srt");
        await File.WriteAllTextAsync(destination, "original");
        try
        {
            var result = await service.DownloadAsync(
                new JimakuSubtitleItem(
                    42,
                    "Test Anime",
                    "Test Anime - 02.ja.srt",
                    new Uri("https://cdn.jimaku.cc/test.srt"),
                    32,
                    "ja",
                    2),
                destination);

            result.IsSuccess.Should().BeFalse();
            (await File.ReadAllTextAsync(destination)).Should().Be("original");
            Directory.EnumerateFiles(directory, ".*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VideoMetadataCandidate CreateIdentity() => new(
        "anilist",
        "123",
        VideoMetadataMediaKind.Anime,
        "Test Anime",
        "テストアニメ",
        2026,
        1,
        2,
        2,
        ["Test Anime"],
        ImmutableDictionary<string, string>.Empty.Add("anilist", "123"),
        null);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
