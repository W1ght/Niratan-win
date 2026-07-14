using FluentAssertions;
using Niratan.Services.Sync;

namespace Niratan.Tests.Services.Sync;

public sealed class GoogleOAuthLoopbackReceiverTests
{
    [Fact]
    public async Task SuccessfulCallback_ReportsAuthorizationReceivedWithoutClaimingConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var receiver = new GoogleOAuthLoopbackReceiver();
        await using var session = await receiver.StartAsync("expected-state", ct);
        using var httpClient = new HttpClient();
        var callbackUri = new Uri(
            session.RedirectUri,
            "?code=authorization-code&state=expected-state");

        var responseTask = httpClient.GetStringAsync(callbackUri, ct);
        var callback = await session.CallbackTask;
        var html = await responseTask;

        callback.Should().Be(new GoogleOAuthCallback(
            "authorization-code",
            "expected-state",
            null));
        html.Should().Contain("Google Drive authorization received");
        html.Should().Contain("Return to Niratan to finish connecting");
        html.Should().NotContain("Google Drive connected");
    }
}
