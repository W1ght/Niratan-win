using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public sealed class GoogleDriveTokenClient
{
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public GoogleDriveTokenClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GoogleDriveCredentials> ExchangeCodeAsync(
        string clientId,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var response = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
            },
            ct);

        if (string.IsNullOrWhiteSpace(response.RefreshToken))
            throw new InvalidOperationException("Google did not return a refresh token.");

        return ToCredentials(response, clientId, response.RefreshToken);
    }

    public async Task<GoogleDriveCredentials> RefreshAsync(
        GoogleDriveCredentials credentials,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var response = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken,
            },
            ct);

        return ToCredentials(response, credentials.ClientId, response.RefreshToken ?? credentials.RefreshToken);
    }

    private async Task<TokenResponse> PostTokenAsync(
        Dictionary<string, string> form,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Google token request failed ({(int)response.StatusCode}): {body}"));
        }

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            throw new InvalidOperationException("Google token response did not include an access token.");

        return tokenResponse;
    }

    private static GoogleDriveCredentials ToCredentials(
        TokenResponse response,
        string clientId,
        string refreshToken)
    {
        var expiresIn = response.ExpiresIn > 0 ? response.ExpiresIn : 3600;
        return new GoogleDriveCredentials(
            response.AccessToken!,
            refreshToken,
            clientId,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            string.IsNullOrWhiteSpace(response.Scope) ? DriveFileScope : response.Scope);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "";
    }
}
