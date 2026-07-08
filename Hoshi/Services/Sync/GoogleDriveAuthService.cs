using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Sync;

public sealed class GoogleDriveAuthService : IGoogleDriveAuthService
{
    private static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");

    private readonly IGoogleDriveCredentialStore _credentialStore;
    private readonly GoogleDriveTokenClient _tokenClient;
    private readonly IGoogleOAuthLoopbackReceiver _loopbackReceiver;
    private readonly IBrowserLauncher _browserLauncher;

    public GoogleDriveAuthService(
        IGoogleDriveCredentialStore credentialStore,
        GoogleDriveTokenClient tokenClient,
        IGoogleOAuthLoopbackReceiver loopbackReceiver,
        IBrowserLauncher browserLauncher)
    {
        _credentialStore = credentialStore;
        _tokenClient = tokenClient;
        _loopbackReceiver = loopbackReceiver;
        _browserLauncher = browserLauncher;
    }

    public bool HasCredentials => _credentialStore.HasCredentials;

    public async Task AuthenticateAsync(string clientId, CancellationToken ct = default)
    {
        clientId = clientId.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var state = GoogleOAuthPkce.CreateCodeVerifier();
        var codeVerifier = GoogleOAuthPkce.CreateCodeVerifier();
        var codeChallenge = GoogleOAuthPkce.CreateCodeChallenge(codeVerifier);

        await using var session = await _loopbackReceiver.StartAsync(state, ct);
        var authorizationUri = BuildAuthorizationUri(
            clientId,
            session.RedirectUri.ToString(),
            codeChallenge,
            state);

        await _browserLauncher.LaunchAsync(authorizationUri, ct);

        var callback = await session.CallbackTask.WaitAsync(TimeSpan.FromMinutes(5), ct);
        if (!string.IsNullOrWhiteSpace(callback.Error))
            throw new InvalidOperationException($"Google authorization failed: {callback.Error}");
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            throw new InvalidOperationException("Google authorization returned an unexpected state.");
        if (string.IsNullOrWhiteSpace(callback.Code))
            throw new InvalidOperationException("Google authorization did not return an authorization code.");

        var credentials = await _tokenClient.ExchangeCodeAsync(
            clientId,
            callback.Code,
            session.RedirectUri.ToString(),
            codeVerifier,
            ct);
        await _credentialStore.SaveAsync(credentials, ct);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var credentials = await _credentialStore.LoadAsync(ct)
            ?? throw new InvalidOperationException("Google Drive is not connected.");

        if (credentials.ShouldRefresh(DateTimeOffset.UtcNow))
        {
            credentials = await _tokenClient.RefreshAsync(credentials, ct);
            await _credentialStore.SaveAsync(credentials, ct);
        }

        return credentials.AccessToken;
    }

    public Task SignOutAsync(CancellationToken ct = default) =>
        _credentialStore.DeleteAsync(ct);

    private static Uri BuildAuthorizationUri(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string state)
    {
        var query = BuildQuery(new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["client_id"] = clientId,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "consent",
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = GoogleDriveTokenClient.DriveFileScope,
            ["state"] = state,
        });

        return new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"{AuthorizationEndpoint}?{query}"));
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values)
    {
        var parts = new List<string>();
        foreach (var pair in values)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return string.Join("&", parts);
    }
}
