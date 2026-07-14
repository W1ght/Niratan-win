using System.Net;
using FluentAssertions;
using Niratan.Models.Sync;
using Niratan.Services.Sync;

namespace Niratan.Tests.Services.Sync;

public sealed class GoogleDriveAuthServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_UsesTrimmedClientCredentialsAndStoresSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "access_token": "access-1",
                  "refresh_token": "refresh-1",
                  "expires_in": 3600,
                  "scope": "https://www.googleapis.com/auth/drive.file"
                }
                """, System.Text.Encoding.UTF8, "application/json"),
        });
        var store = new RecordingCredentialStore();
        var service = new GoogleDriveAuthService(
            store,
            new GoogleDriveTokenClient(new HttpClient(handler)),
            new SuccessfulLoopbackReceiver(),
            new RecordingBrowserLauncher());

        await service.AuthenticateAsync(
            " 1234567890-abcdef.apps.googleusercontent.com ",
            " desktop-client-secret ",
            ct);

        handler.LastBody.Should().Contain("client_id=1234567890-abcdef.apps.googleusercontent.com");
        handler.LastBody.Should().Contain("client_secret=desktop-client-secret");
        store.Saved.Should().NotBeNull();
        store.Saved!.ClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        store.Saved.ClientSecret.Should().Be("desktop-client-secret");
    }

    private sealed class SuccessfulLoopbackReceiver : IGoogleOAuthLoopbackReceiver
    {
        public Task<GoogleOAuthLoopbackSession> StartAsync(
            string state,
            CancellationToken ct = default)
        {
            var callback = Task.FromResult(new GoogleOAuthCallback(
                "authorization-code",
                state,
                null));
            return Task.FromResult(new GoogleOAuthLoopbackSession(
                new Uri("http://127.0.0.1:49152/"),
                callback,
                () => ValueTask.CompletedTask));
        }
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        public Uri? LaunchedUri { get; private set; }

        public Task LaunchAsync(Uri uri, CancellationToken ct = default)
        {
            LaunchedUri = uri;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : IGoogleDriveCredentialStore
    {
        public bool HasCredentials => Saved != null;
        public GoogleDriveCredentials? Saved { get; private set; }

        public Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Saved);

        public Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)
        {
            Saved = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            Saved = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
            _responseFactory = responseFactory;

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
