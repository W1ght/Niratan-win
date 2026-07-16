using System.Net;
using System.Net.Http;
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
              "prerelease": false
            }
            """
        );
        var sut = new GitHubAppUpdateService(new HttpClient(handler));

        var result = await sut.CheckForUpdateAsync("0.4.0");

        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("0.5.0");
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
}
