using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Updates;

public sealed class GitHubAppUpdateService : IAppUpdateService
{
    private const string InstallerAssetPrefix = "Niratan.Setup.x64";
    private const string ReleaseDownloadPathPrefix =
        "/W1ght/Niratan-win/releases/download/";

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
        var isUpdateAvailable = IsVersionNewer(latestVersion, currentVersion);
        var installerAsset = isUpdateAvailable ? FindInstallerAsset(release.Assets) : null;
        if (isUpdateAvailable && installerAsset is null)
            throw new HttpRequestException("GitHub release is missing the x64 setup asset.");

        return new AppUpdateCheckResult(
            currentVersion,
            latestVersion,
            releasePageUri,
            installerAsset,
            isUpdateAvailable
        );
    }

    public async Task<AppUpdatePackage> DownloadUpdateAsync(
        AppUpdateCheckResult update,
        string destinationDirectory,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!update.IsUpdateAvailable || update.InstallerAsset is null)
            throw new InvalidOperationException("No downloadable app update is available.");

        var asset = update.InstallerAsset;
        ValidateInstallerAsset(asset);

        var directory = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(directory);

        var installerPath = Path.Combine(directory, asset.Name);
        var partialPath = installerPath + ".partial";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            request.Headers.UserAgent.ParseAdd("Niratan-Windows");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                var buffer = new byte[81920];
                long bytesReceived = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesReceived += read;
                    progress?.Report(new AppUpdateDownloadProgress(bytesReceived, asset.Size));
                }

                await destination.FlushAsync(ct);
                if (bytesReceived != asset.Size)
                {
                    throw new InvalidDataException(
                        $"Downloaded update size mismatch. Expected {asset.Size}, received {bytesReceived}."
                    );
                }
            }

            await using (var downloaded = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct))
                    .ToLowerInvariant();
                if (!string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded update SHA-256 mismatch.");
            }

            File.Move(partialPath, installerPath, true);
            progress?.Report(new AppUpdateDownloadProgress(asset.Size, asset.Size));
            return new AppUpdatePackage(update.LatestVersion, installerPath);
        }
        catch
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            throw;
        }
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

    private static AppUpdateAsset? FindInstallerAsset(GitHubAsset[]? assets)
    {
        var asset = assets?.FirstOrDefault(candidate =>
            candidate.Name.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return null;

        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri))
            throw new HttpRequestException("GitHub returned an invalid setup download URL.");

        var sha256 = NormalizeSha256(asset.Digest);
        var result = new AppUpdateAsset(asset.Name, downloadUri, asset.Size, sha256);
        ValidateInstallerAsset(result);
        return result;
    }

    private static string NormalizeSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("GitHub setup asset is missing a SHA-256 digest.");
        }

        var value = digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new HttpRequestException("GitHub setup asset has an invalid SHA-256 digest.");

        return value.ToLowerInvariant();
    }

    private static void ValidateInstallerAsset(AppUpdateAsset asset)
    {
        if (!asset.Name.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)
            || !asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update asset is not the Niratan x64 installer.");
        }

        if (!string.Equals(Path.GetFileName(asset.Name), asset.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The update asset name is not a safe file name.");

        if (asset.Size <= 0)
            throw new InvalidOperationException("The update asset size is invalid.");

        if (asset.DownloadUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(asset.DownloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !asset.DownloadUri.AbsolutePath.StartsWith(
                ReleaseDownloadPathPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update asset URL is not a trusted GitHub release URL.");
        }

        if (asset.Sha256.Length != 64 || asset.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The update asset SHA-256 is invalid.");
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets
    );

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest
    );
}
