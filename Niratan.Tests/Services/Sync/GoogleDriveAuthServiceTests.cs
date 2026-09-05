using System.Net;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Niratan.Messages;
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
            new RecordingBrowserLauncher(),
            new WeakReferenceMessenger());

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

    [Fact]
    public async Task GetAccessTokenAsync_WhenGrantIsInvalid_ClearsCredentialsAndPublishesReconnectState()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""
                {
                  "error": "invalid_grant",
                  "error_description": "Token has been expired or revoked."
                }
                """, System.Text.Encoding.UTF8, "application/json"),
        });
        var store = new RecordingCredentialStore(new GoogleDriveCredentials(
            "expired-access",
            "revoked-refresh",
            "client-id",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            GoogleDriveTokenClient.DriveFileScope,
            "client-secret"));
        var messenger = new WeakReferenceMessenger();
        GoogleDriveConnectionStateChangedMessage? state = null;
        var recipient = new object();
        messenger.Register<object, GoogleDriveConnectionStateChangedMessage>(
            recipient,
            (_, message) => state = message);
        var service = new GoogleDriveAuthService(
            store,
            new GoogleDriveTokenClient(new HttpClient(handler)),
            new SuccessfulLoopbackReceiver(),
            new RecordingBrowserLauncher(),
            messenger);

        var action = () => service.GetAccessTokenAsync(ct);

        await action.Should().ThrowAsync<GoogleDriveReauthenticationRequiredException>();
        store.Saved.Should().BeNull();
        store.DeleteCount.Should().Be(1);
        service.HasCredentials.Should().BeFalse();
        state.Should().Be(new GoogleDriveConnectionStateChangedMessage(
            IsConnected: false,
            RequiresReconnect: true));
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenRefreshFailsTransiently_KeepsCredentialsAndConnectionState()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("temporarily unavailable"),
        });
        var original = new GoogleDriveCredentials(
            "expired-access",
            "still-valid-refresh",
            "client-id",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            GoogleDriveTokenClient.DriveFileScope,
            "client-secret");
        var store = new RecordingCredentialStore(original);
        var messenger = new WeakReferenceMessenger();
        var receivedStates = 0;
        var recipient = new object();
        messenger.Register<object, GoogleDriveConnectionStateChangedMessage>(
            recipient,
            (_, _) => receivedStates++);
        var service = new GoogleDriveAuthService(
            store,
            new GoogleDriveTokenClient(new HttpClient(handler)),
            new SuccessfulLoopbackReceiver(),
            new RecordingBrowserLauncher(),
            messenger);

        var action = () => service.GetAccessTokenAsync(ct);

        await action.Should().ThrowAsync<GoogleDriveTokenRequestException>();
        store.Saved.Should().BeSameAs(original);
        store.DeleteCount.Should().Be(0);
        service.HasCredentials.Should().BeTrue();
        receivedStates.Should().Be(0);
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
        public RecordingCredentialStore(GoogleDriveCredentials? credentials = null) =>
            Saved = credentials;

        public bool HasCredentials => Saved != null;
        public GoogleDriveCredentials? Saved { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Saved);

        public Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)
        {
            Saved = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            DeleteCount++;
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
