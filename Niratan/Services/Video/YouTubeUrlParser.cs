using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Niratan.Models;

namespace Niratan.Services.Video;

public static class YouTubeUrlParser
{
    public const string ProviderId = "youtube";
    private static readonly Regex StartTimePattern = new(
        "^(?:(?<hours>[0-9]+)h)?(?:(?<minutes>[0-9]+)m)?(?:(?<seconds>[0-9]+)s)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string? value, out string videoId, out string canonicalUrl)
        => TryParse(value, out videoId, out canonicalUrl, out _);

    public static bool TryParse(
        string? value,
        out string videoId,
        out string canonicalUrl,
        out TimeSpan? requestedStartPosition)
    {
        videoId = string.Empty;
        canonicalUrl = string.Empty;
        requestedStartPosition = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (HasQueryParameter(uri.Query, "list"))
            return false;

        string? candidate = null;
        if (host is "youtu.be" or "www.youtu.be")
        {
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 1)
                candidate = path[0];
        }
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 1 && path[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
                candidate = ParseQuery(uri.Query, "v");
            else if (path.Length == 2
                     && (path[0].Equals("shorts", StringComparison.OrdinalIgnoreCase)
                         || path[0].Equals("embed", StringComparison.OrdinalIgnoreCase)))
                candidate = path[1];
        }
        else if (host is "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 2 && path[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
                candidate = path[1];
        }

        if (!IsValidVideoId(candidate))
            return false;

        videoId = candidate!;
        canonicalUrl = $"https://www.youtube.com/watch?v={videoId}";
        requestedStartPosition = ParseStartPosition(uri);
        return true;
    }

    public static bool IsRemoteKey(string? value) =>
        RemoteVideoIdentity.IsPersistenceKey(value, ProviderId);

    private static bool IsValidVideoId(string? value) =>
        value?.Length == 11
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string? ParseQuery(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            if (!key.Equals(name, StringComparison.Ordinal))
                continue;

            return Uri.UnescapeDataString(separator < 0 ? string.Empty : part[(separator + 1)..]);
        }

        return null;
    }

    private static bool HasQueryParameter(string query, string name) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Select(Uri.UnescapeDataString)
            .Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan? ParseStartPosition(Uri uri)
    {
        var value = ParseQuery(uri.Query, "t") ?? ParseQuery(uri.Query, "start");
        if (string.IsNullOrWhiteSpace(value) && uri.Fragment.StartsWith("#t=", StringComparison.OrdinalIgnoreCase))
            value = Uri.UnescapeDataString(uri.Fragment[3..]);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var plainSeconds))
            return ToTimeSpan(plainSeconds);

        var match = StartTimePattern.Match(value);
        if (!match.Success || match.Value.Length == 0)
            return null;

        var hours = ParseComponent(match, "hours");
        var minutes = ParseComponent(match, "minutes");
        var seconds = ParseComponent(match, "seconds");
        if (hours == 0 && minutes == 0 && seconds == 0)
            return value.Contains('0') ? TimeSpan.Zero : null;

        return ToTimeSpan((hours * 3600d) + (minutes * 60d) + seconds);
    }

    private static double ParseComponent(Match match, string name) =>
        double.TryParse(match.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static TimeSpan? ToTimeSpan(double seconds) =>
        seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
