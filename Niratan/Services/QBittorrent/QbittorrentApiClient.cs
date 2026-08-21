using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;

namespace Niratan.Services.QBittorrent;

public sealed class QbittorrentApiClient : IQbittorrentClient, IDisposable
{
    private const string NiratanTag = "NIRATAN";
    private static readonly Uri NyaaBaseUri = new("https://nyaa.si/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public QbittorrentApiClient()
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), true)
    {
    }

    internal QbittorrentApiClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Niratan", "0.9"));
    }

    public async Task<Result<QbittorrentConnectionInfo>> TestConnectionAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        CancellationToken ct = default)
    {
        try
        {
            var version = await SendTextAsync(settings, credentials, HttpMethod.Get, "app/version", null, ct);
            if (!version.IsSuccess)
                return Result<QbittorrentConnectionInfo>.Failure(
                    version.Error ?? "Could not read qBittorrent version.",
                    "qBittorrent connection failed");

            var apiVersion = await SendTextAsync(
                settings,
                credentials,
                HttpMethod.Get,
                "app/webapiVersion",
                null,
                ct);
            return Result<QbittorrentConnectionInfo>.Success(
                new QbittorrentConnectionInfo(
                    version.Value ?? "unknown",
                    apiVersion.IsSuccess ? apiVersion.Value ?? "unknown" : "unknown"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<QbittorrentConnectionInfo>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<QbittorrentConnectionInfo>.Failure(ex.Message, "qBittorrent connection failed");
        }
    }

    public async Task<Result<IReadOnlyList<QbittorrentTorrent>>> GetTorrentsAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                settings,
                credentials,
                HttpMethod.Get,
                "torrents/info",
                null,
                ct);
            if (!response.IsSuccessStatusCode)
                return Result<IReadOnlyList<QbittorrentTorrent>>.Failure(
                    await ReadErrorAsync(response, ct),
                    "Could not read qBittorrent tasks");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var torrents = await JsonSerializer.DeserializeAsync<List<QbittorrentTorrentDto>>(
                stream,
                JsonOptions,
                ct) ?? [];
            return Result<IReadOnlyList<QbittorrentTorrent>>.Success(
                torrents.Select(ToModel).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<QbittorrentTorrent>>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<QbittorrentTorrent>>.Failure(
                ex.Message,
                "Could not read qBittorrent tasks");
        }
    }

    public async Task<Result<QbittorrentTorrentDetails>> GetTorrentDetailsAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default)
    {
        if (!IsTorrentHash(hash))
            return Result<QbittorrentTorrentDetails>.Failure(
                "The torrent hash is invalid.",
                "Could not read torrent details");

        try
        {
            var encodedHash = Uri.EscapeDataString(hash.Trim());
            var properties = await GetJsonAsync<QbittorrentTorrentPropertiesDto>(
                settings,
                credentials,
                $"torrents/properties?hash={encodedHash}",
                ct);
            if (!properties.IsSuccess)
                return Result<QbittorrentTorrentDetails>.Failure(
                    properties.Error ?? "Could not read torrent properties.",
                    "Could not read torrent details");

            var files = await GetJsonAsync<List<QbittorrentTorrentFileDto>>(
                settings,
                credentials,
                $"torrents/files?hash={encodedHash}",
                ct);
            if (!files.IsSuccess)
                return Result<QbittorrentTorrentDetails>.Failure(
                    files.Error ?? "Could not read torrent files.",
                    "Could not read torrent details");

            var trackers = await GetJsonAsync<List<QbittorrentTorrentTrackerDto>>(
                settings,
                credentials,
                $"torrents/trackers?hash={encodedHash}",
                ct);
            if (!trackers.IsSuccess)
                return Result<QbittorrentTorrentDetails>.Failure(
                    trackers.Error ?? "Could not read torrent trackers.",
                    "Could not read torrent details");

            return Result<QbittorrentTorrentDetails>.Success(
                new QbittorrentTorrentDetails(
                    ToModel(properties.Value!),
                    (files.Value ?? []).Select(ToModel).ToList(),
                    (trackers.Value ?? []).Select(ToModel).ToList()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<QbittorrentTorrentDetails>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<QbittorrentTorrentDetails>.Failure(
                ex.Message,
                "Could not read torrent details");
        }
    }

    public async Task<Result> AddTorrentAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        NyaaTorrentItem item,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            EnsureNyaaTorrentUri(item.TorrentUri);
            using var response = await SendAuthorizedAsync(
                settings,
                credentials,
                HttpMethod.Post,
                "torrents/add",
                () =>
                {
                    var content = new MultipartFormDataContent();
                    content.Add(new StringContent(item.TorrentUri.ToString()), "urls");
                    content.Add(new StringContent(NiratanTag), "tags");
                    content.Add(
                        new StringContent(settings.AddPaused ? "true" : "false"),
                        "paused");
                    if (!string.IsNullOrWhiteSpace(settings.DefaultSavePath))
                        content.Add(
                            new StringContent(settings.DefaultSavePath.Trim()),
                            "savepath");
                    if (!string.IsNullOrWhiteSpace(settings.DefaultCategory))
                        content.Add(
                            new StringContent(settings.DefaultCategory.Trim()),
                            "category");
                    return content;
                },
                ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(await ReadErrorAsync(response, ct), "Could not add torrent");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, "Could not add torrent");
        }
    }

    public Task<Result> PauseAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default) =>
        SendTorrentActionAsync(settings, credentials, "stop", "pause", hash, ct);

    public Task<Result> ResumeAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default) =>
        SendTorrentActionAsync(settings, credentials, "start", "resume", hash, ct);

    public async Task<Result> DeleteAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        bool deleteFiles,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return Result.Failure("The torrent hash is required.", "Could not delete torrent");

        try
        {
            using var response = await SendAuthorizedAsync(
                settings,
                credentials,
                HttpMethod.Post,
                "torrents/delete",
                () => new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["hashes"] = hash.Trim(),
                    ["deleteFiles"] = deleteFiles ? "true" : "false",
                }),
                ct);
            return response.IsSuccessStatusCode
                ? Result.Success()
                : Result.Failure(await ReadErrorAsync(response, ct), "Could not delete torrent");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, "Could not delete torrent");
        }
    }

    private async Task<Result> SendTorrentActionAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string currentPath,
        string legacyPath,
        string hash,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return Result.Failure("The torrent hash is required.", "Could not update torrent");

        try
        {
            var response = await SendAuthorizedAsync(
                settings,
                credentials,
                HttpMethod.Post,
                $"torrents/{currentPath}",
                () => new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["hashes"] = hash.Trim(),
                }),
                ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                response = await SendAuthorizedAsync(
                    settings,
                    credentials,
                    HttpMethod.Post,
                    $"torrents/{legacyPath}",
                    () => new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["hashes"] = hash.Trim(),
                    }),
                    ct);
            }

            using (response)
            {
                return response.IsSuccessStatusCode
                    ? Result.Success()
                    : Result.Failure(await ReadErrorAsync(response, ct), "Could not update torrent");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, "Could not update torrent");
        }
    }

    private async Task<Result<string>> SendTextAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        HttpMethod method,
        string path,
        Func<HttpContent?>? contentFactory,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                settings,
                credentials,
                method,
                path,
                contentFactory,
                ct);
            if (!response.IsSuccessStatusCode)
                return Result<string>.Failure(
                    await ReadErrorAsync(response, ct),
                    "qBittorrent request failed");
            return Result<string>.Success((await response.Content.ReadAsStringAsync(ct)).Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<string>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(ex.Message, "qBittorrent request failed");
        }
    }

    private async Task<Result<T>> GetJsonAsync<T>(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string path,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                settings,
                credentials,
                HttpMethod.Get,
                path,
                null,
                ct);
            if (!response.IsSuccessStatusCode)
                return Result<T>.Failure(
                    await ReadErrorAsync(response, ct),
                    "qBittorrent request failed");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
            return value is null
                ? Result<T>.Failure("qBittorrent returned an empty response.", "qBittorrent request failed")
                : Result<T>.Success(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<T>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(ex.Message, "qBittorrent request failed");
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        HttpMethod method,
        string path,
        Func<HttpContent?>? contentFactory,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var baseUri = NormalizeBaseUri(settings.BaseUrl);
        var sessionKey = $"{baseUri.AbsoluteUri}|{credentials.Username}";
        var hasApiKey = !string.IsNullOrWhiteSpace(credentials.ApiKey);
        if (!hasApiKey && !_sessions.ContainsKey(sessionKey))
            await LoginAsync(baseUri, credentials, sessionKey, ct);

        var response = await SendOnceAsync(
            baseUri,
            method,
            path,
            contentFactory,
            hasApiKey ? credentials.ApiKey.Trim() : null,
            hasApiKey ? null : _sessions.GetValueOrDefault(sessionKey),
            ct);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            || hasApiKey)
            return response;

        response.Dispose();
        _sessions.TryRemove(sessionKey, out _);
        await LoginAsync(baseUri, credentials, sessionKey, ct);
        return await SendOnceAsync(
            baseUri,
            method,
            path,
            contentFactory,
            null,
            _sessions.GetValueOrDefault(sessionKey),
            ct);
    }

    private async Task LoginAsync(
        Uri baseUri,
        QbittorrentCredentials credentials,
        string sessionKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username)
            || string.IsNullOrWhiteSpace(credentials.Password))
        {
            throw new InvalidOperationException(
                "Configure a qBittorrent API key or WebUI username and password.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(baseUri, "auth/login"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = credentials.Username,
                ["password"] = credentials.Password,
            }),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        EnsureNoRedirect(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadErrorAsync(response, ct));

        var sid = ExtractSid(response);
        _sessions[sessionKey] = sid ?? string.Empty;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Uri baseUri,
        HttpMethod method,
        string path,
        Func<HttpContent?>? contentFactory,
        string? apiKey,
        string? sid,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BuildEndpoint(baseUri, path));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else if (sid is not null && sid.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", $"SID={sid}");
        request.Content = contentFactory?.Invoke();

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        EnsureNoRedirect(response);
        return response;
    }

    private static Uri NormalizeBaseUri(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "qBittorrent server URL must be an HTTP(S) origin without credentials or query parameters.");
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !IsLoopback(uri.Host))
        {
            throw new InvalidOperationException(
                "Remote qBittorrent connections must use HTTPS. HTTP is allowed only for localhost.");
        }

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" };
        return builder.Uri;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static Uri BuildEndpoint(Uri baseUri, string path) =>
        new(baseUri, $"api/v2/{path.TrimStart('/')}");

    private static void EnsureNoRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("qBittorrent redirected the WebAPI request.");
    }

    private static string? ExtractSid(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return null;
        var cookie = values
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("SID=", StringComparison.OrdinalIgnoreCase));
        return cookie is null ? null : cookie[4..];
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        return string.IsNullOrWhiteSpace(body)
            ? $"qBittorrent returned HTTP {(int)response.StatusCode}."
            : $"qBittorrent returned HTTP {(int)response.StatusCode}: {body}";
    }

    private static void EnsureNyaaTorrentUri(Uri uri)
    {
        if (!uri.Scheme.Equals(NyaaBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(NyaaBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != NyaaBaseUri.Port
            || uri.UserInfo.Length > 0)
        {
            throw new InvalidDataException("Only torrent URLs from the allowed Nyaa origin can be sent to qBittorrent.");
        }
    }

    private static QbittorrentTorrent ToModel(QbittorrentTorrentDto dto) =>
        new(
            dto.Hash ?? string.Empty,
            dto.Name ?? "",
            dto.State ?? "unknown",
            dto.Progress,
            dto.Size,
            dto.AmountLeft,
            dto.DownloadSpeed,
            dto.UploadSpeed,
            dto.Eta,
            dto.Ratio,
            dto.Category ?? "",
            dto.Tags ?? "",
            dto.SavePath ?? "",
            dto.ContentPath ?? "",
            FromUnixSeconds(dto.AddedOn),
            FromUnixSeconds(dto.CompletionOn));

    private static QbittorrentTorrentProperties ToModel(QbittorrentTorrentPropertiesDto dto) =>
        new(
            dto.SavePath ?? "",
            FromUnixSeconds(dto.CreationDate),
            dto.PieceSize,
            dto.Comment ?? "",
            dto.TotalWasted,
            dto.TotalUploaded,
            dto.TotalDownloaded,
            dto.DownloadSpeedAverage,
            dto.UploadSpeedAverage,
            dto.Eta,
            dto.Peers,
            dto.PeersTotal,
            dto.Seeds,
            dto.SeedsTotal,
            dto.PiecesHave,
            dto.PiecesTotal,
            dto.Connections,
            dto.ConnectionsLimit,
            dto.ShareRatio,
            dto.TotalSize,
            dto.IsPrivate,
            dto.CreatedBy ?? "",
            FromUnixSeconds(dto.AdditionDate),
            FromUnixSeconds(dto.CompletionDate));

    private static QbittorrentTorrentFile ToModel(QbittorrentTorrentFileDto dto) =>
        new(
            dto.Index,
            dto.Name ?? "",
            dto.Size,
            dto.Progress,
            dto.Priority,
            dto.IsSeed,
            dto.Availability);

    private static QbittorrentTorrentTracker ToModel(QbittorrentTorrentTrackerDto dto) =>
        new(
            dto.Url ?? "",
            dto.Status,
            dto.Tier,
            dto.Peers,
            dto.Seeds,
            dto.Leeches,
            dto.Downloaded,
            dto.Message ?? "");

    private static DateTimeOffset? FromUnixSeconds(long value) =>
        value <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(value);

    private static bool IsTorrentHash(string? hash) =>
        !string.IsNullOrWhiteSpace(hash)
        && hash.Trim().Length <= 64
        && hash.Trim().Length >= 20
        && hash.Trim().All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sessions.Clear();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed class QbittorrentTorrentDto
    {
        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("amount_left")]
        public long AmountLeft { get; set; }

        [JsonPropertyName("dlspeed")]
        public long DownloadSpeed { get; set; }

        [JsonPropertyName("upspeed")]
        public long UploadSpeed { get; set; }

        [JsonPropertyName("eta")]
        public long Eta { get; set; }

        [JsonPropertyName("ratio")]
        public double Ratio { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("tags")]
        public string? Tags { get; set; }

        [JsonPropertyName("save_path")]
        public string? SavePath { get; set; }

        [JsonPropertyName("content_path")]
        public string? ContentPath { get; set; }

        [JsonPropertyName("added_on")]
        public long AddedOn { get; set; }

        [JsonPropertyName("completion_on")]
        public long CompletionOn { get; set; }
    }

    private sealed class QbittorrentTorrentPropertiesDto
    {
        [JsonPropertyName("save_path")]
        public string? SavePath { get; set; }

        [JsonPropertyName("creation_date")]
        public long CreationDate { get; set; }

        [JsonPropertyName("piece_size")]
        public long PieceSize { get; set; }

        [JsonPropertyName("comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("total_wasted")]
        public long TotalWasted { get; set; }

        [JsonPropertyName("total_uploaded")]
        public long TotalUploaded { get; set; }

        [JsonPropertyName("total_downloaded")]
        public long TotalDownloaded { get; set; }

        [JsonPropertyName("dl_speed_avg")]
        public long DownloadSpeedAverage { get; set; }

        [JsonPropertyName("up_speed_avg")]
        public long UploadSpeedAverage { get; set; }

        [JsonPropertyName("eta")]
        public long Eta { get; set; }

        [JsonPropertyName("peers")]
        public long Peers { get; set; }

        [JsonPropertyName("peers_total")]
        public long PeersTotal { get; set; }

        [JsonPropertyName("seeds")]
        public long Seeds { get; set; }

        [JsonPropertyName("seeds_total")]
        public long SeedsTotal { get; set; }

        [JsonPropertyName("pieces_have")]
        public long PiecesHave { get; set; }

        [JsonPropertyName("pieces_num")]
        public long PiecesTotal { get; set; }

        [JsonPropertyName("nb_connections")]
        public long Connections { get; set; }

        [JsonPropertyName("nb_connections_limit")]
        public long ConnectionsLimit { get; set; }

        [JsonPropertyName("share_ratio")]
        public double ShareRatio { get; set; }

        [JsonPropertyName("total_size")]
        public long TotalSize { get; set; }

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("created_by")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("addition_date")]
        public long AdditionDate { get; set; }

        [JsonPropertyName("completion_date")]
        public long CompletionDate { get; set; }
    }

    private sealed class QbittorrentTorrentFileDto
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("is_seed")]
        public bool IsSeed { get; set; }

        [JsonPropertyName("availability")]
        public double Availability { get; set; }
    }

    private sealed class QbittorrentTorrentTrackerDto
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("tier")]
        public int Tier { get; set; }

        [JsonPropertyName("num_peers")]
        public int Peers { get; set; }

        [JsonPropertyName("num_seeds")]
        public int Seeds { get; set; }

        [JsonPropertyName("num_leeches")]
        public int Leeches { get; set; }

        [JsonPropertyName("num_downloaded")]
        public int Downloaded { get; set; }

        [JsonPropertyName("msg")]
        public string? Message { get; set; }
    }
}
