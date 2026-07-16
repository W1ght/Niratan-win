using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Updates;

public sealed class GitHubAppUpdateService : IAppUpdateService
{
    internal static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/W1ght/Niratan-win/releases/latest"
    );

    private readonly HttpClient _httpClient;

    public GitHubAppUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken ct = default
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Niratan-Windows");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var release =
            await response.Content.ReadFromJsonAsync<GitHubRelease>(
                cancellationToken: timeoutCts.Token
            ) ?? throw new HttpRequestException("GitHub returned an empty release response.");

        if (release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
            throw new HttpRequestException("GitHub latest did not return a stable release.");

        if (
            !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releasePageUri)
            || releasePageUri.Scheme != Uri.UriSchemeHttps
        )
        {
            throw new HttpRequestException("GitHub returned an invalid release URL.");
        }

        var latestVersion = NormalizeVersion(release.TagName);
        return new AppUpdateCheckResult(
            currentVersion,
            latestVersion,
            releasePageUri,
            IsVersionNewer(latestVersion, currentVersion)
        );
    }

    internal static bool IsVersionNewer(string candidate, string current)
    {
        return TryParseVersion(candidate, out var candidateVersion)
            && TryParseVersion(current, out var currentVersion)
            && candidateVersion > currentVersion;
    }

    internal static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        return suffixIndex >= 0 ? normalized[..suffixIndex] : normalized;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var parts = NormalizeVersion(value).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
        {
            version = new Version();
            return false;
        }

        var normalizedParts = new int[Math.Max(parts.Length, 3)];
        for (var index = 0; index < parts.Length; index++)
        {
            if (
                !int.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out normalizedParts[index]
                )
                || normalizedParts[index] < 0
            )
            {
                version = new Version();
                return false;
            }
        }

        version =
            normalizedParts.Length == 4
                ? new Version(
                    normalizedParts[0],
                    normalizedParts[1],
                    normalizedParts[2],
                    normalizedParts[3]
                )
                : new Version(normalizedParts[0], normalizedParts[1], normalizedParts[2]);
        return true;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease
    );
}
