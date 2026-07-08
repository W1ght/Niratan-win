using System.Net;
using FluentAssertions;
using Hoshi.Models.Sync;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class GoogleDriveTokenClientTests
{
    [Fact]
    public async Task ExchangeCodeAsync_PostsAuthorizationCodeWithPkceVerifier()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "access_token": "access-1",
                  "refresh_token": "refresh-1",
                  "expires_in": 3600,
                  "scope": "https://www.googleapis.com/auth/drive.file",
                  "token_type": "Bearer"
                }
                """),
        });
        var client = new GoogleDriveTokenClient(new HttpClient(handler));

        var credentials = await client.ExchangeCodeAsync(
            "1234567890-abcdef.apps.googleusercontent.com",
            "authorization-code",
            "http://127.0.0.1:49152/",
            "code-verifier",
            ct);

        handler.LastRequest!.RequestUri.Should().Be(GoogleDriveTokenClient.TokenEndpoint);
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastBody.Should().Contain("grant_type=authorization_code");
        handler.LastBody.Should().Contain("code=authorization-code");
        handler.LastBody.Should().Contain("redirect_uri=http%3A%2F%2F127.0.0.1%3A49152%2F");
        handler.LastBody.Should().Contain("code_verifier=code-verifier");
        credentials.Should().BeEquivalentTo(new GoogleDriveCredentials(
            AccessToken: "access-1",
            RefreshToken: "refresh-1",
            ClientId: "1234567890-abcdef.apps.googleusercontent.com",
            ExpiresAtUtc: credentials.ExpiresAtUtc,
            Scope: "https://www.googleapis.com/auth/drive.file"));
        credentials.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task RefreshAsync_PreservesRefreshTokenWhenResponseOnlyReturnsAccessToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var existing = new GoogleDriveCredentials(
            AccessToken: "old-access",
            RefreshToken: "refresh-1",
            ClientId: "1234567890-abcdef.apps.googleusercontent.com",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            Scope: GoogleDriveTokenClient.DriveFileScope);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "access_token": "access-2",
                  "expires_in": 1800,
                  "scope": "https://www.googleapis.com/auth/drive.file",
                  "token_type": "Bearer"
                }
                """),
        });
        var client = new GoogleDriveTokenClient(new HttpClient(handler));

        var refreshed = await client.RefreshAsync(existing, ct);

        handler.LastRequest!.RequestUri.Should().Be(GoogleDriveTokenClient.TokenEndpoint);
        handler.LastBody.Should().Contain("grant_type=refresh_token");
        handler.LastBody.Should().Contain("refresh_token=refresh-1");
        refreshed.AccessToken.Should().Be("access-2");
        refreshed.RefreshToken.Should().Be("refresh-1");
        refreshed.ClientId.Should().Be(existing.ClientId);
        refreshed.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(20));
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
