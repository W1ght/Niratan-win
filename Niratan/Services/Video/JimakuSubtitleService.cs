using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

internal sealed partial class JimakuSubtitleService : IJimakuSubtitleService, IDisposable
{
    private const long MaximumJsonBytes = 8L * 1024 * 1024;
    private const long MaximumSubtitleBytes = 32L * 1024 * 1024;
    private static readonly Uri ApiBase = new("https://jimaku.cc/api/");
    private static readonly HashSet<string> TextSubtitleExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt" };

    private readonly IVideoMetadataCredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public JimakuSubtitleService(IVideoMetadataCredentialStore credentials)
        : this(
            credentials,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            },
            ownsHttpClient: true)
    {
    }

    internal JimakuSubtitleService(
        IVideoMetadataCredentialStore credentials,
        HttpClient http,
        bool ownsHttpClient = false)
    {
        _credentials = credentials;
        _http = http;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<Result<IReadOnlyList<JimakuSubtitleItem>>> SearchAsync(
        VideoSubtitleSearchRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var apiKey = await ReadApiKeyAsync(ct);
            if (apiKey is null)
                return Result<IReadOnlyList<JimakuSubtitleItem>>.Failure(
                    ResourceStringHelper.GetString(
                        "JimakuApiKeyRequired",
                        "Configure a Jimaku API key in Video settings before searching subtitles."),
                    JimakuTitle());

            var entries = await SearchEntriesAsync(request, apiKey, ct);
            var results = new List<JimakuSubtitleItem>();
            foreach (var entry in entries.Take(10))
            {
                var files = await ListFilesAsync(entry.Id, request.Identity.EpisodeNumber, apiKey, ct);
                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file.Name);
                    if (!TextSubtitleExtensions.Contains(extension)
                        || !IsTrustedJimakuUri(file.Uri))
                        continue;

                    results.Add(new JimakuSubtitleItem(
                        entry.Id,
                        entry.Name,
                        file.Name,
                        file.Uri,
                        file.Size,
                        DetectLanguage(file.Name),
                        ParseEpisode(file.Name)));
                    if (results.Count >= 500)
                        break;
                }
                if (results.Count >= 500)
                    break;
            }

            return Result<IReadOnlyList<JimakuSubtitleItem>>.Success(
                results
                    .OrderBy(item => LanguageRank(item.Language))
                    .ThenBy(item => item.EpisodeNumber ?? int.MaxValue)
                    .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<JimakuSubtitleItem>>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<JimakuSubtitleItem>>.Failure(ex.Message, JimakuTitle());
        }
    }

    public async Task<Result<string>> DownloadAsync(
        JimakuSubtitleItem item,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (!IsTrustedJimakuUri(item.DownloadUri))
            return Result<string>.Failure(
                ResourceStringHelper.GetString(
                    "JimakuUntrustedUrl",
                    "Jimaku returned an untrusted subtitle URL."),
                JimakuTitle());

        var extension = Path.GetExtension(item.FileName);
        if (!TextSubtitleExtensions.Contains(extension)
            || !TextSubtitleExtensions.Contains(Path.GetExtension(destinationPath)))
            return Result<string>.Failure(
                ResourceStringHelper.GetString(
                    "JimakuUnsupportedExtension",
                    "Only SRT, ASS, SSA, and VTT subtitle files are supported."),
                JimakuTitle());

        string? temporaryPath = null;
        try
        {
            var apiKey = await ReadApiKeyAsync(ct);
            if (apiKey is null)
                return Result<string>.Failure(
                    ResourceStringHelper.GetString(
                        "JimakuApiKeyMissing",
                        "The Jimaku API key is no longer configured."),
                    JimakuTitle());

            using var request = CreateRequest(item.DownloadUri, apiKey);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            EnsureSuccessWithoutRedirect(
                response,
                ResourceStringHelper.GetString("JimakuOperationDownload", "download subtitles"));
            var bytes = await ReadLimitedAsync(response, MaximumSubtitleBytes, ct);
            if (bytes.Length == 0)
                throw new InvalidDataException(ResourceStringHelper.GetString(
                    "JimakuEmptySubtitle",
                    "Jimaku returned an empty subtitle file."));

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
                ?? throw new InvalidOperationException(ResourceStringHelper.GetString(
                    "DiscoverSubtitleInvalidDestination",
                    "The subtitle destination is invalid."));
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(ResourceStringHelper.GetString(
                    "DiscoverSubtitleDirectoryMissing",
                    "The selected subtitle folder no longer exists."));
            }
            temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
            // Subtitle installation is non-destructive. Callers choose a
            // conflict-free destination; the service still enforces that
            // contract atomically so a stale UI choice cannot overwrite a
            // user's existing sidecar.
            File.Move(temporaryPath, destinationPath, overwrite: false);
            temporaryPath = null;
            return Result<string>.Success(destinationPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<string>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(ex.Message, JimakuTitle());
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private async Task<IReadOnlyList<JimakuEntry>> SearchEntriesAsync(
        VideoSubtitleSearchRequest request,
        string apiKey,
        CancellationToken ct)
    {
        var filterValues = request.Identity.MediaKind == VideoMetadataMediaKind.Anime
            ? new[] { "true" }
            : new[] { "true", "false" };

        var anilistId = ResolveAniListId(request.Identity);
        if (anilistId is not null)
        {
            foreach (var anime in filterValues)
            {
                var entries = await SearchEntriesOnceAsync(
                    new Dictionary<string, string>
                    {
                        ["anilist_id"] = anilistId.Value.ToString(),
                        ["anime"] = anime,
                    },
                    apiKey,
                    ct);
                if (entries.Count > 0)
                    return entries;
            }
        }

        var queries = new[] { request.Query, request.Identity.Title, request.Identity.OriginalTitle }
            .Concat(request.Identity.Aliases.IsDefault ? [] : request.Identity.Aliases)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5);
        foreach (var query in queries)
        {
            foreach (var anime in filterValues)
            {
                var entries = await SearchEntriesOnceAsync(
                    new Dictionary<string, string> { ["query"] = query, ["anime"] = anime },
                    apiKey,
                    ct);
                if (entries.Count > 0)
                    return entries;
            }
        }
        return [];
    }

    private async Task<IReadOnlyList<JimakuEntry>> SearchEntriesOnceAsync(
        IReadOnlyDictionary<string, string> query,
        string apiKey,
        CancellationToken ct)
    {
        var uri = new UriBuilder(new Uri(ApiBase, "entries/search"))
        {
            Query = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        }.Uri;
        using var request = CreateRequest(uri, apiKey);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccessWithoutRedirect(
            response,
            ResourceStringHelper.GetString("JimakuOperationSearch", "search entries"));
        var bytes = await ReadLimitedAsync(response, MaximumJsonBytes, ct);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "JimakuInvalidSearchResponse",
                "Jimaku returned an invalid search response."));

        var entries = new List<JimakuEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("id", out var idValue)
                || !idValue.TryGetInt32(out var id))
                continue;
            var name = ReadString(element, "name") ?? ReadString(element, "english_name") ?? $"#{id}";
            entries.Add(new JimakuEntry(id, name));
        }
        return entries;
    }

    private async Task<IReadOnlyList<JimakuFile>> ListFilesAsync(
        int entryId,
        int? episode,
        string apiKey,
        CancellationToken ct)
    {
        var uri = new Uri(ApiBase, $"entries/{entryId}/files");
        if (episode is int number)
            uri = new UriBuilder(uri) { Query = $"episode={number}" }.Uri;
        using var request = CreateRequest(uri, apiKey);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccessWithoutRedirect(
            response,
            ResourceStringHelper.GetString("JimakuOperationListFiles", "list subtitle files"));
        var bytes = await ReadLimitedAsync(response, MaximumJsonBytes, ct);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "JimakuInvalidFileList",
                "Jimaku returned an invalid file list."));

        var files = new List<JimakuFile>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var name = ReadString(element, "name");
            var url = ReadString(element, "url");
            if (string.IsNullOrWhiteSpace(name)
                || !Uri.TryCreate(url, UriKind.Absolute, out var downloadUri))
                continue;
            long? size = element.TryGetProperty("size", out var sizeValue)
                && sizeValue.TryGetInt64(out var parsedSize)
                ? parsedSize
                : null;
            files.Add(new JimakuFile(name, downloadUri, size));
        }
        return files;
    }

    private async Task<string?> ReadApiKeyAsync(CancellationToken ct)
    {
        var key = await _credentials.ReadAsync("jimaku", "token", ct);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static void EnsureSuccessWithoutRedirect(HttpResponseMessage response, string operation)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException(ResourceStringHelper.FormatString(
                "JimakuRedirectRejected",
                "Jimaku refused to {0}: redirects are not trusted.",
                operation));
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException(ResourceStringHelper.GetString(
                "JimakuApiKeyRejected",
                "The Jimaku API key was rejected."));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ResourceStringHelper.FormatString(
                "JimakuRequestFailed",
                "Jimaku could not {0} (HTTP {1}).",
                operation,
                (int)response.StatusCode));
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        long maximumBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "JimakuResponseTooLarge",
                "The Jimaku response is too large."));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new InvalidDataException(ResourceStringHelper.GetString(
                        "JimakuResponseTooLarge",
                        "The Jimaku response is too large."));
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int? ResolveAniListId(VideoMetadataCandidate identity)
    {
        var raw = identity.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
            ? identity.ProviderItemId
            : identity.ExternalIds.TryGetValue("anilist", out var externalId) ? externalId : null;
        return int.TryParse(raw, out var id) ? id : null;
    }

    private static bool IsTrustedJimakuUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("jimaku.cc", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".jimaku.cc", StringComparison.OrdinalIgnoreCase));

    private static string JimakuTitle() => ResourceStringHelper.GetString(
        "JimakuTitle",
        "Jimaku subtitles");

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? DetectLanguage(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (JapaneseLanguageRegex().IsMatch(lower) || fileName.Contains("日本語", StringComparison.Ordinal)) return "ja";
        if (ChineseLanguageRegex().IsMatch(lower) || fileName.Contains("中文", StringComparison.Ordinal)
            || fileName.Contains("简体", StringComparison.Ordinal) || fileName.Contains("繁體", StringComparison.Ordinal)) return "zh";
        if (EnglishLanguageRegex().IsMatch(lower)) return "en";
        if (KoreanLanguageRegex().IsMatch(lower)) return "ko";
        return null;
    }

    private static int LanguageRank(string? language) => language switch
    {
        "ja" => 0,
        "zh" => 1,
        "en" => 2,
        "ko" => 3,
        _ => 4,
    };

    private static int? ParseEpisode(string fileName)
    {
        var match = EpisodeRegex().Match(Path.GetFileNameWithoutExtension(fileName));
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode) ? episode : null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record JimakuEntry(int Id, string Name);
    private sealed record JimakuFile(string Name, Uri Uri, long? Size);

    [GeneratedRegex(@"(?:s\d{1,2}e|\bep?|\s-\s|第)\s*0*(\d{1,4})(?:\b|[話话集])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();
    [GeneratedRegex(@"(?:^|[.\[(_ -])(ja|jpn|jp)(?:[.\])_ -]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex JapaneseLanguageRegex();
    [GeneratedRegex(@"(?:^|[.\[(_ -])(zh|zho|chi|chs|cht)(?:-[a-z]+)?(?:[.\])_ -]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseLanguageRegex();
    [GeneratedRegex(@"(?:^|[.\[(_ -])(en|eng)(?:[.\])_ -]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishLanguageRegex();
    [GeneratedRegex(@"(?:^|[.\[(_ -])(ko|kor)(?:[.\])_ -]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex KoreanLanguageRegex();
}
