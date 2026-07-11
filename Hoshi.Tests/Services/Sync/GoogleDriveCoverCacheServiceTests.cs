using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Hoshi.Models.Sync;
using Hoshi.Services.Sync;
using Hoshi.Tests.TestUtils;

namespace Hoshi.Tests.Services.Sync;

public sealed class GoogleDriveCoverCacheServiceTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task GetCoverPathAsync_DownloadsAuthenticatedS768ThumbnailAtomically()
    {
        using var temp = new TempDirectory();
        var handler = new RecordingHandler(ImageResponse(PngBytes));
        var service = new GoogleDriveCoverCacheService(
            new HttpClient(handler),
            new FakeGoogleDriveAuthService(),
            temp.Path);
        var cover = new TtuRemoteFile(
            "cover-id",
            "cover_1_6.png",
            ThumbnailLink: "https://thumb.test/image=s220");

        var path = await service.GetCoverPathAsync(
            cover,
            TestContext.Current.CancellationToken);

        path.Should().NotBeNull();
        File.ReadAllBytes(path!).Should().Equal(PngBytes);
        handler.Requests.Single().RequestUri!.ToString().Should().EndWith("=s768");
        handler.Requests.Single().Headers.Authorization.Should().Be(
            new AuthenticationHeaderValue("Bearer", "drive-token"));
        Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task GetCoverPathAsync_ReusesValidCacheWithoutNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var cover = new TtuRemoteFile(
            "cover-id",
            "cover_1_6.png",
            ThumbnailLink: "https://thumb.test/image=s220");
        var first = new GoogleDriveCoverCacheService(
            new HttpClient(new RecordingHandler(ImageResponse(PngBytes))),
            new FakeGoogleDriveAuthService(),
            temp.Path);
        var expected = await first.GetCoverPathAsync(cover, ct);
        var second = new GoogleDriveCoverCacheService(
            new HttpClient(new ThrowingHandler()),
            new FakeGoogleDriveAuthService(),
            temp.Path);

        var actual = await second.GetCoverPathAsync(cover, ct);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetCoverPathAsync_InvalidImageLeavesNoCacheAndReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var service = new GoogleDriveCoverCacheService(
            new HttpClient(new RecordingHandler(ImageResponse([1, 2, 3]))),
            new FakeGoogleDriveAuthService(),
            temp.Path);

        var path = await service.GetCoverPathAsync(
            new TtuRemoteFile(
                "bad",
                "cover_1_6.png",
                ThumbnailLink: "https://thumb.test/bad"),
            ct);

        path.Should().BeNull();
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task GetCoverPathAsync_CancellationPropagates()
    {
        using var temp = new TempDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new GoogleDriveCoverCacheService(
            new HttpClient(new ThrowingHandler()),
            new FakeGoogleDriveAuthService(),
            temp.Path);
        Func<Task> action = () => service.GetCoverPathAsync(
            new TtuRemoteFile(
                "cover-id",
                "cover_1_6.png",
                ThumbnailLink: "https://thumb.test/cover"),
            cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") },
            },
        };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network should not be used.");
    }

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials => true;

        public Task AuthenticateAsync(
            string clientId,
            string clientSecret,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("drive-token");

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
