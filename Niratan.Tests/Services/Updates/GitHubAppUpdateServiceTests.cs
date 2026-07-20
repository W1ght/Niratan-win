using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Niratan.Services.Updates;

namespace Niratan.Tests.Services.Updates;

public sealed class GitHubAppUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNewStableRelease()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "tag_name": "v0.5.0",
              "html_url": "https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "Niratan.Setup.x64.exe",
                  "browser_download_url": "https://github.com/W1ght/Niratan-win/releases/download/v0.5.0/Niratan.Setup.x64.exe",
                  "size": 123,
                  "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                }
              ]
            }
            """
        );
        var sut = new GitHubAppUpdateService(new HttpClient(handler));

        var result = await sut.CheckForUpdateAsync(
            "0.4.0",
            TestContext.Current.CancellationToken);

        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("0.5.0");
        result.InstallerAsset.Should().NotBeNull();
        result.InstallerAsset!.Name.Should().Be("Niratan.Setup.x64.exe");
        result.InstallerAsset.Size.Should().Be(123);
        result
            .ReleasePageUri.AbsoluteUri.Should()
            .Be("https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri.Should().Be(GitHubAppUpdateService.LatestReleaseApiUri);
        handler.LastRequest.Headers.UserAgent.ToString().Should().Be("Niratan-Windows");
    }

    [Theory]
    [InlineData("v0.4.1", "0.4.0", true)]
    [InlineData("0.4.0", "0.4.0+abc", false)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.3.9", "0.4.0", false)]
    [InlineData("invalid", "0.4.0", false)]
    public void IsVersionNewer_NormalizesReleaseTags(
        string candidate,
        string current,
        bool expected
    )
    {
        GitHubAppUpdateService.IsVersionNewer(candidate, current).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CheckForUpdateAsync_RejectsNonStableRelease(bool draft, bool prerelease)
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            $$"""
            {
              "tag_name": "v0.5.0",
              "html_url": "https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0",
              "draft": {{draft.ToString().ToLowerInvariant()}},
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}}
            }
            """
        );
        var sut = new GitHubAppUpdateService(new HttpClient(handler));

        var action = () => sut.CheckForUpdateAsync("0.4.0");

        await action.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CheckForUpdateAsync_RejectsNewReleaseWithoutSetupAsset()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "tag_name": "v0.5.0",
              "html_url": "https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """
        );
        var sut = new GitHubAppUpdateService(new HttpClient(handler));

        var action = () => sut.CheckForUpdateAsync(
            "0.4.0",
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*missing the x64 setup asset*");
    }

    [Fact]
    public async Task CheckForUpdateAsync_DoesNotRequireInstallerForCurrentRelease()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "tag_name": "v0.5.0",
              "html_url": "https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """
        );
        var sut = new GitHubAppUpdateService(new HttpClient(handler));

        var result = await sut.CheckForUpdateAsync(
            "0.5.0",
            TestContext.Current.CancellationToken);

        result.IsUpdateAvailable.Should().BeFalse();
        result.InstallerAsset.Should().BeNull();
    }

    [Fact]
    public async Task DownloadUpdateAsync_DownloadsAndVerifiesInstaller()
    {
        var payload = Encoding.UTF8.GetBytes("signed installer bytes");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var handler = new BinaryHttpMessageHandler(payload);
        var sut = new GitHubAppUpdateService(new HttpClient(handler));
        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "niratan-update-tests",
            Guid.NewGuid().ToString("N"));
        var progressValues = new List<AppUpdateDownloadProgress>();
        var update = CreateUpdate(payload.LongLength, sha256);

        try
        {
            var package = await sut.DownloadUpdateAsync(
                update,
                downloadDirectory,
                new InlineProgress<AppUpdateDownloadProgress>(progressValues.Add),
                TestContext.Current.CancellationToken);

            package.Version.Should().Be("0.5.0");
            package.InstallerPath.Should().Be(
                Path.Combine(downloadDirectory, "Niratan.Setup.x64.exe"));
            var downloadedBytes = await File.ReadAllBytesAsync(
                package.InstallerPath,
                TestContext.Current.CancellationToken);
            downloadedBytes.Should().BeEquivalentTo(payload);
            progressValues.Should().NotBeEmpty();
            progressValues[^1].Percentage.Should().Be(100);
            handler.LastRequest!.Headers.UserAgent.ToString().Should().Be("Niratan-Windows");
        }
        finally
        {
            if (Directory.Exists(downloadDirectory))
                Directory.Delete(downloadDirectory, true);
        }
    }

    [Fact]
    public async Task DownloadUpdateAsync_DeletesPartialFileWhenDigestDoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("tampered installer bytes");
        var sut = new GitHubAppUpdateService(
            new HttpClient(new BinaryHttpMessageHandler(payload)));
        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "niratan-update-tests",
            Guid.NewGuid().ToString("N"));
        var update = CreateUpdate(payload.LongLength, new string('0', 64));

        try
        {
            var action = () => sut.DownloadUpdateAsync(
                update,
                downloadDirectory,
                ct: TestContext.Current.CancellationToken);

            await action.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*SHA-256 mismatch*");
            File.Exists(Path.Combine(downloadDirectory, "Niratan.Setup.x64.exe"))
                .Should().BeFalse();
            File.Exists(Path.Combine(downloadDirectory, "Niratan.Setup.x64.exe.partial"))
                .Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(downloadDirectory))
                Directory.Delete(downloadDirectory, true);
        }
    }

    private static AppUpdateCheckResult CreateUpdate(long size, string sha256) =>
        new(
            "0.4.0",
            "0.5.0",
            new Uri("https://github.com/W1ght/Niratan-win/releases/tag/v0.5.0"),
            new AppUpdateAsset(
                "Niratan.Setup.x64.exe",
                new Uri("https://github.com/W1ght/Niratan-win/releases/download/v0.5.0/Niratan.Setup.x64.exe"),
                size,
                sha256),
            true);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _json;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string json)
        {
            _statusCode = statusCode;
            _json = json;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            return Task.FromResult(
                new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                }
            );
        }
    }

    private sealed class BinaryHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request,
            });
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
