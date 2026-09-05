using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public sealed class GoogleDriveTokenRequestException : InvalidOperationException
{
    public GoogleDriveTokenRequestException(
        HttpStatusCode statusCode,
        string? errorCode,
        string? errorDescription)
        : base(BuildMessage(statusCode, errorCode))
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ErrorCode { get; }

    public string? ErrorDescription { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? errorCode) =>
        string.IsNullOrWhiteSpace(errorCode)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Google token request failed ({(int)statusCode}).")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Google token request failed ({(int)statusCode}): {errorCode}.");
}

public sealed class GoogleDriveReauthenticationRequiredException : InvalidOperationException
{
    public GoogleDriveReauthenticationRequiredException(Exception innerException)
        : base("Google Drive authorization expired or was revoked. Connect Google Drive again.", innerException)
    {
    }
}

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
        string clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        var response = await PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
            },
            ct);

        if (string.IsNullOrWhiteSpace(response.RefreshToken))
            throw new InvalidOperationException("Google did not return a refresh token.");

        return ToCredentials(response, clientId, clientSecret, response.RefreshToken);
    }

    public async Task<GoogleDriveCredentials> RefreshAsync(
        GoogleDriveCredentials credentials,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var clientSecret = credentials.ClientSecret ?? "";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;

        var response = await PostTokenAsync(form, ct);
        return ToCredentials(
            response,
            credentials.ClientId,
            clientSecret,
            response.RefreshToken ?? credentials.RefreshToken);
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
            TokenErrorResponse? error = null;
            try
            {
                error = JsonSerializer.Deserialize<TokenErrorResponse>(body, JsonOptions);
            }
            catch (JsonException)
            {
            }

            throw new GoogleDriveTokenRequestException(
                response.StatusCode,
                error?.Error,
                error?.ErrorDescription);
        }

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            throw new InvalidOperationException("Google token response did not include an access token.");

        return tokenResponse;
    }

    private static GoogleDriveCredentials ToCredentials(
        TokenResponse response,
        string clientId,
        string clientSecret,
        string refreshToken)
    {
        var expiresIn = response.ExpiresIn > 0 ? response.ExpiresIn : 3600;
        return new GoogleDriveCredentials(
            response.AccessToken!,
            refreshToken,
            clientId,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            string.IsNullOrWhiteSpace(response.Scope) ? DriveFileScope : response.Scope,
            clientSecret);
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

    private sealed class TokenErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
