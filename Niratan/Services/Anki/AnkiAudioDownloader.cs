using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Niratan.Services.Audio;

namespace Niratan.Services.Anki;

internal sealed record AnkiAudioDownloadResult(byte[] Bytes, string Filename, string SourceUrl);

internal sealed class AnkiAudioDownloader
{
    private const int MaxCacheEntries = 256;
    private readonly HttpClient _http;
    private readonly LocalAudioSourceListResolver _localAudioSourceListResolver;
    private readonly ConcurrentDictionary<string, AnkiAudioDownloadResult> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<AnkiAudioDownloadResult?>>> _inflight = new(StringComparer.Ordinal);

    public AnkiAudioDownloader(HttpClient http)
        : this(http, new LocalAudioSourceListResolver())
    {
    }

    internal AnkiAudioDownloader(HttpClient http, LocalAudioSourceListResolver localAudioSourceListResolver)
    {
        _http = http;
        _localAudioSourceListResolver = localAudioSourceListResolver;
    }

    public async Task<AnkiAudioDownloadResult?> DownloadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = NormalizeAudioSourceUrl(url);
        if (_cache.TryGetValue(url, out var cached))
            return CloneResult(cached);

        var operation = _inflight.GetOrAdd(
            url,
            static (key, self) => new Lazy<Task<AnkiAudioDownloadResult?>>(
                () => self.DownloadAndCacheAsync(key)),
            this);
        try
        {
            var result = await operation.Value;
            return result == null ? null : CloneResult(result);
        }
        finally
        {
            if (_inflight.TryGetValue(url, out var current) && ReferenceEquals(current, operation))
                _inflight.TryRemove(url, out _);
        }
    }

    private async Task<AnkiAudioDownloadResult?> DownloadAndCacheAsync(string url)
    {
        if (_cache.TryGetValue(url, out var cached))
            return CloneResult(cached);

        var cacheKey = url;
        var localResolution = await _localAudioSourceListResolver.ResolveAsync(url);
        if (localResolution != null)
            url = localResolution.AudioUrl;

        if (TryReadLocalAudioFile(url, out var localBytes, out var localSourceUrl))
        {
            var localResult = BuildResult(localBytes, localSourceUrl, null);
            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();

            _cache[cacheKey] = CloneResult(localResult);
            return CloneResult(localResult);
        }

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (LooksLikeAudioSourceList(contentType, bytes))
        {
            var resolvedUrl = ResolveAudioSourceListUrl(bytes, response.RequestMessage?.RequestUri);
            if (string.IsNullOrWhiteSpace(resolvedUrl))
                return null;

            using var audioResponse = await _http.GetAsync(resolvedUrl, HttpCompletionOption.ResponseHeadersRead);
            audioResponse.EnsureSuccessStatusCode();
            contentType = audioResponse.Content.Headers.ContentType?.MediaType;
            bytes = await audioResponse.Content.ReadAsByteArrayAsync();
            url = audioResponse.RequestMessage?.RequestUri?.ToString() ?? resolvedUrl;
        }
        else
        {
            url = response.RequestMessage?.RequestUri?.ToString() ?? url;
        }

        var result = BuildResult(bytes, url, contentType);
        if (_cache.Count >= MaxCacheEntries)
            _cache.Clear();

        _cache[cacheKey] = CloneResult(result);
        return CloneResult(result);
    }

    private static AnkiAudioDownloadResult CloneResult(AnkiAudioDownloadResult result) =>
        result with { Bytes = (byte[])result.Bytes.Clone() };

    private static AnkiAudioDownloadResult BuildResult(byte[] bytes, string sourceUrl, string? contentType)
    {
        var extension = InferExtension(sourceUrl, contentType);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new AnkiAudioDownloadResult(bytes, $"niratan_audio_{hash}{extension}", sourceUrl);
    }

    private static bool TryReadLocalAudioFile(string url, out byte[] bytes, out string sourceUrl)
    {
        bytes = [];
        sourceUrl = url;

        string path;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            sourceUrl = uri.AbsoluteUri;
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out _) && File.Exists(url))
        {
            path = url;
            sourceUrl = new Uri(path).AbsoluteUri;
        }
        else
        {
            return false;
        }

        if (!File.Exists(path))
            return false;

        bytes = File.ReadAllBytes(path);
        return bytes.Length > 0;
    }

    private static bool LooksLikeAudioSourceList(string? contentType, byte[] bytes)
    {
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        var prefixLength = Math.Min(bytes.Length, 64);
        var prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, prefixLength).TrimStart();
        return prefix.StartsWith('{');
    }

    private static string? ResolveAudioSourceListUrl(byte[] bytes, Uri? baseUri)
    {
        try
        {
            var json = JsonSerializer.Deserialize<AudioSourceListEnvelope>(bytes);
            if (json == null || !string.Equals(json.Type, "audioSourceList", StringComparison.OrdinalIgnoreCase))
                return null;

            var sourceUrl = json.AudioSources?.Find(s => !string.IsNullOrWhiteSpace(s.Url))?.Url;
            if (string.IsNullOrWhiteSpace(sourceUrl))
                return null;

            sourceUrl = NormalizeAudioSourceUrl(sourceUrl);
            if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var absolute))
                return absolute.ToString();

            return baseUri != null && Uri.TryCreate(baseUri, sourceUrl, out var relative)
                ? relative.ToString()
                : sourceUrl;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeAudioSourceUrl(string url)
    {
        return AudioSourceUrlNormalizer.Normalize(url);
    }

    private static string InferExtension(string url, string? contentType)
    {
        var pathExtension = Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url);
        if (IsSupportedAudioExtension(pathExtension))
            return pathExtension.ToLowerInvariant();

        return contentType?.ToLowerInvariant() switch
        {
            "audio/aac" => ".aac",
            "audio/mp4" => ".m4a",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/wav" => ".wav",
            "audio/x-wav" => ".wav",
            _ => ".mp3",
        };
    }

    private static bool IsSupportedAudioExtension(string extension) =>
        extension.ToLowerInvariant() is ".mp3" or ".aac" or ".m4a" or ".wav" or ".ogg";

    private sealed class AudioSourceListEnvelope
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("audioSources")]
        public List<AudioSourceEntry>? AudioSources { get; set; }
    }

    private sealed class AudioSourceEntry
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }
}
