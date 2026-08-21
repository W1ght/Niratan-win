using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Niratan.Models;

namespace Niratan.Services.Video;

internal sealed record YouTubePageResponse(
    string Title,
    string? ThumbnailUrl,
    TimeSpan? Duration,
    IReadOnlyList<RemoteVideoStream> Streams,
    IReadOnlyList<RemoteVideoSubtitleOption> SubtitleOptions)
{
    public static YouTubePageResponse Empty { get; } =
        new("", null, null, [], []);
}

internal sealed class YouTubePageResponseLoader : IDisposable
{
    private const int MaximumWatchPageBytes = 8 * 1024 * 1024;
    private const int MaximumPlayerResponseBytes = 4 * 1024 * 1024;
    private const int MaximumSubtitleBytes = 32 * 1024 * 1024;
    private const string AndroidVrClientName = "ANDROID_VR";
    private const string AndroidVrClientId = "28";
    private const string AndroidVrClientVersion = "1.65.10";
    private const string AndroidVrUserAgent =
        "com.google.android.apps.youtube.vr.oculus/1.65.10 "
        + "(Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";

    internal static IReadOnlyDictionary<string, string> PlaybackHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = "https://www.youtube.com/",
            ["Origin"] = "https://www.youtube.com",
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                             + "AppleWebKit/537.36 (KHTML, like Gecko) "
                             + "Chrome/131.0.0.0 Safari/537.36",
        };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<YouTubePageResponse>>> _cache = new();

    public YouTubePageResponseLoader()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal YouTubePageResponseLoader(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private YouTubePageResponseLoader(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<YouTubePageResponse> LoadAsync(
        string videoId,
        CancellationToken ct = default)
    {
        var lazy = _cache.GetOrAdd(
            videoId,
            id => new Lazy<Task<YouTubePageResponse>>(
                () => LoadCoreAsync(id, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<string, Lazy<Task<YouTubePageResponse>>>(videoId, lazy));
            throw;
        }
    }

    public async Task DownloadSubtitleAsync(
        RemoteVideoSubtitleOption option,
        string outputPath,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(option.SourceUrl, UriKind.Absolute, out var uri)
            || !IsYouTubeHost(uri.Host)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The YouTube subtitle URL is not trusted.");
        }

        var responseText = await GetTextAsync(uri, MaximumSubtitleBytes, ct);
        var subtitleText = ConvertToSrt(responseText);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outputPath, subtitleText, new UTF8Encoding(false), ct);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task<YouTubePageResponse> LoadCoreAsync(
        string videoId,
        CancellationToken ct)
    {
        var watchUri = new Uri(
            $"https://www.youtube.com/watch?v={videoId}&hl=en",
            UriKind.Absolute);
        var html = await GetTextAsync(watchUri, MaximumWatchPageBytes, ct);
        var watchResponse = YouTubePlayerResponseParser.ParseHtml(html);
        var visitorData = ExtractVisitorData(html);
        if (string.IsNullOrWhiteSpace(visitorData))
            return watchResponse;

        try
        {
            var playerResponse = await LoadAndroidVrPlayerResponseAsync(videoId, visitorData, ct);
            var androidResponse = YouTubePlayerResponseParser.ParseJson(playerResponse);
            return Merge(watchResponse, androidResponse);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The watch page still contains usable metadata for many videos. The
            // Android VR request is an availability workaround, not a second
            // required service boundary.
            return watchResponse;
        }
    }

    private async Task<string> LoadAndroidVrPlayerResponseAsync(
        string videoId,
        string visitorData,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    context = new
                    {
                        client = new
                        {
                            clientName = AndroidVrClientName,
                            clientVersion = AndroidVrClientVersion,
                            deviceMake = "Oculus",
                            deviceModel = "Quest 3",
                            androidSdkVersion = 32,
                            userAgent = AndroidVrUserAgent,
                            osName = "Android",
                            osVersion = "12L",
                            hl = "en",
                            timeZone = "UTC",
                            utcOffsetMinutes = 0,
                            visitorData,
                        },
                    },
                    videoId,
                    playbackContext = new
                    {
                        contentPlaybackContext = new
                        {
                            html5Preference = "HTML5_PREF_WANTS",
                        },
                    },
                    contentCheckOk = true,
                    racyCheckOk = true,
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", AndroidVrUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", AndroidVrClientId);
        request.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", AndroidVrClientVersion);
        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"YouTube player request returned {(int)response.StatusCode}.");
        return await ReadBoundedTextAsync(response, MaximumPlayerResponseBytes, ct);
    }

    private async Task<string> GetTextAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"YouTube request returned {(int)response.StatusCode}.");
        return await ReadBoundedTextAsync(response, maximumBytes, ct);
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The YouTube response exceeds the configured size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        var byteCount = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            byteCount += Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (byteCount > maximumBytes)
                throw new InvalidDataException("The YouTube response exceeds the configured size limit.");
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static YouTubePageResponse Merge(
        YouTubePageResponse watch,
        YouTubePageResponse player)
    {
        return new YouTubePageResponse(
            string.IsNullOrWhiteSpace(player.Title) ? watch.Title : player.Title,
            player.ThumbnailUrl ?? watch.ThumbnailUrl,
            player.Duration ?? watch.Duration,
            player.Streams.Count == 0 ? watch.Streams : player.Streams,
            player.SubtitleOptions.Count == 0 ? watch.SubtitleOptions : player.SubtitleOptions);
    }

    private static string? ExtractVisitorData(string html)
    {
        const string marker = "\"VISITOR_DATA\"";
        var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        var colonIndex = html.IndexOf(':', markerIndex + marker.Length);
        if (colonIndex < 0)
            return null;

        var valueStart = colonIndex + 1;
        while (valueStart < html.Length && char.IsWhiteSpace(html[valueStart]))
            valueStart++;
        if (valueStart >= html.Length || html[valueStart] != '"')
            return null;

        var valueEnd = valueStart + 1;
        var escaped = false;
        for (; valueEnd < html.Length; valueEnd++)
        {
            var character = html[valueEnd];
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '"')
            {
                var literal = html[valueStart..(valueEnd + 1)];
                try
                {
                    return JsonSerializer.Deserialize<string>(literal);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static string ConvertToSrt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("The YouTube subtitle response is empty.");

        if (text.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"\d{1,2}:\d{2}:\d{2}[\.,]\d{1,3}\s+-->", RegexOptions.CultureInvariant))
        {
            return text;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException("The YouTube subtitle response is invalid.", ex);
        }

        var cues = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase))
            .Select(ParseTimedTextCue)
            .Where(cue => cue != null)
            .Select(cue => cue!.Value)
            .ToList();
        if (cues.Count == 0)
            throw new InvalidDataException("The YouTube subtitle response has no cues.");

        var output = new StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            output.Append(index + 1).Append("\r\n");
            output.Append(FormatSrtTimestamp(cue.Start)).Append(" --> ")
                .Append(FormatSrtTimestamp(cue.End)).Append("\r\n");
            output.Append(cue.Text).Append("\r\n\r\n");
        }

        return output.ToString();
    }

    private static (TimeSpan Start, TimeSpan End, string Text)? ParseTimedTextCue(XElement element)
    {
        if (!TryParseMilliseconds(element.Attribute("t")?.Value, out var start))
            return null;

        var duration = TryParseMilliseconds(element.Attribute("d")?.Value, out var parsedDuration)
            ? parsedDuration
            : TimeSpan.FromSeconds(2);
        var text = element.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : (start, start + duration, WebUtility.HtmlDecode(text));
    }

    private static bool TryParseMilliseconds(string? value, out TimeSpan result)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            && double.IsFinite(milliseconds)
            && milliseconds >= 0
            && milliseconds <= TimeSpan.MaxValue.TotalMilliseconds)
        {
            result = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        result = default;
        return false;
    }

    private static string FormatSrtTimestamp(TimeSpan value)
    {
        var totalHours = (int)Math.Min(value.TotalHours, 99_999);
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase);
}

internal static class YouTubePlayerResponseParser
{
    private static readonly Uri YouTubeOrigin = new("https://www.youtube.com");

    public static YouTubePageResponse ParseHtml(string html)
    {
        foreach (var marker in new[]
                 {
                     "var ytInitialPlayerResponse =",
                     "ytInitialPlayerResponse =",
                     "\"ytInitialPlayerResponse\":",
                 })
        {
            var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var openBrace = html.IndexOf('{', markerIndex + marker.Length);
            if (openBrace < 0)
                continue;

            var json = ExtractBalancedObject(html, openBrace);
            if (json == null)
                continue;

            return ParseJson(json);
        }

        return YouTubePageResponse.Empty;
    }

    public static YouTubePageResponse ParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseResponse(document.RootElement);
        }
        catch (JsonException)
        {
            return YouTubePageResponse.Empty;
        }
    }

    private static YouTubePageResponse ParseResponse(JsonElement root)
    {
        var details = Property(root, "videoDetails");
        var title = ReadString(details, "title") ?? "";
        var duration = ParseDuration(ReadString(details, "lengthSeconds"));
        var thumbnail = ReadThumbnail(details);
        var streams = ParseStreams(root);
        var subtitles = ParseSubtitles(root);
        return new YouTubePageResponse(title, thumbnail, duration, streams, subtitles);
    }

    private static IReadOnlyList<RemoteVideoStream> ParseStreams(JsonElement root)
    {
        var result = new List<RemoteVideoStream>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var streamingData = Property(root, "streamingData");
        foreach (var propertyName in new[] { "formats", "adaptiveFormats" })
        {
            var values = Property(streamingData, propertyName);
            if (values.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var value in values.EnumerateArray())
            {
                var url = ReadString(value, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)
                    || !seenUrls.Add(uri.AbsoluteUri))
                {
                    continue;
                }

                var mime = ReadString(value, "mimeType");
                var mimeType = mime?.Split(';', 2)[0].Trim().ToLowerInvariant();
                var hasVideo = mimeType == "video/mp4" || mimeType == "video/webm";
                var hasAudio = mimeType == "audio/mp4"
                               || mimeType == "audio/webm"
                               || (hasVideo && mime?.Contains("mp4a", StringComparison.OrdinalIgnoreCase) == true);
                if (!hasVideo && !hasAudio)
                    continue;

                var codecs = ParseCodecs(mime);
                var formatId = ReadString(value, "itag")
                               ?? ReadInt(value, "itag")?.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(formatId))
                    continue;

                result.Add(new RemoteVideoStream(
                    uri.AbsoluteUri,
                    formatId,
                    ReadInt(value, "height"),
                    hasVideo,
                    hasAudio,
                    ParseContainer(mimeType, hasAudio && !hasVideo),
                    hasVideo ? codecs.FirstOrDefault() : null,
                    hasAudio ? codecs.LastOrDefault() : null,
                    ReadLong(value, "averageBitrate") ?? ReadLong(value, "bitrate") ?? 0,
                    YouTubePageResponseLoader.PlaybackHeaders));
            }
        }

        return result;
    }

    private static IReadOnlyList<RemoteVideoSubtitleOption> ParseSubtitles(JsonElement root)
    {
        var result = new List<RemoteVideoSubtitleOption>();
        var captions = Property(root, "captions");
        var renderer = Property(captions, "playerCaptionsTracklistRenderer");
        var tracks = Property(renderer, "captionTracks");
        if (tracks.ValueKind != JsonValueKind.Array)
            return result;

        var index = 0;
        foreach (var track in tracks.EnumerateArray())
        {
            var rawUrl = ReadString(track, "baseUrl");
            var language = ReadString(track, "languageCode");
            if (!TryCreateWebVttUrl(rawUrl, out var url) || string.IsNullOrWhiteSpace(language))
            {
                index++;
                continue;
            }

            var name = ReadString(Property(track, "name"), "simpleText")
                       ?? ReadRuns(Property(track, "name"))
                       ?? language;
            var id = ReadString(track, "vssId")
                     ?? $"{language}:{index}";
            result.Add(new RemoteVideoSubtitleOption(
                id,
                language,
                name,
                url.AbsoluteUri,
                string.Equals(ReadString(track, "kind"), "asr", StringComparison.OrdinalIgnoreCase)));
            index++;
        }

        return result;
    }

    private static bool TryCreateWebVttUrl(string? rawUrl, out Uri url)
    {
        url = default!;
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(YouTubeOrigin, rawUrl, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = parsed.Query.TrimStart('?');
        var queryParts = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("fmt=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        queryParts.Add("fmt=vtt");
        var builder = new UriBuilder(parsed)
        {
            Query = string.Join('&', queryParts),
        };
        url = builder.Uri;
        return true;
    }

    private static string? ExtractBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
                inString = true;
            else if (character == '{')
                depth++;
            else if (character == '}' && --depth == 0)
                return text[start..(index + 1)];
        }

        return null;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? ReadString(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static TimeSpan? ParseDuration(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static string? ReadThumbnail(JsonElement details)
    {
        var thumbnails = Property(Property(details, "thumbnail"), "thumbnails");
        return thumbnails.ValueKind == JsonValueKind.Array
            ? thumbnails.EnumerateArray()
                .Select(item => (Url: ReadString(item, "url"), Width: ReadInt(item, "width") ?? 0, Height: ReadInt(item, "height") ?? 0))
                .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out _))
                .OrderByDescending(item => item.Width * item.Height)
                .Select(item => item.Url)
                .FirstOrDefault()
            : null;
    }

    private static IReadOnlyList<string> ParseCodecs(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return [];

        var marker = "codecs=\"";
        var start = mime.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return [];
        start += marker.Length;
        var end = mime.IndexOf('"', start);
        return (end < 0 ? mime[start..] : mime[start..end])
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(codec => codec.Trim())
            .ToList();
    }

    private static string ParseContainer(string? mimeType, bool audioOnly) =>
        mimeType switch
        {
            "audio/mp4" when audioOnly => "m4a",
            "video/mp4" or "audio/mp4" => "mp4",
            "video/webm" or "audio/webm" => "webm",
            _ => mimeType?.Split('/').LastOrDefault() ?? "",
        };

    private static string? ReadRuns(JsonElement element)
    {
        var runs = Property(element, "runs");
        return runs.ValueKind == JsonValueKind.Array
            ? string.Concat(runs.EnumerateArray().Select(run => ReadString(run, "text") ?? ""))
            : null;
    }
}
