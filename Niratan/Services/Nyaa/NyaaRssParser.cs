using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public sealed class NyaaRssParser
{
    private static readonly XNamespace NyaaNamespace = "https://nyaa.si/xmlns/nyaa";

    public IReadOnlyList<NyaaTorrentItem> Parse(string xml, Uri expectedBaseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(expectedBaseUri);

        var document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants("item")
            .Select(element => ParseItem(element, expectedBaseUri))
            .Where(item => item is not null)
            .Cast<NyaaTorrentItem>()
            .ToList();
    }

    private static NyaaTorrentItem? ParseItem(XElement element, Uri expectedBaseUri)
    {
        var title = element.Element("title")?.Value.Trim();
        var detailsUri = TryCreateSafeUri(
            element.Element("guid")?.Value ?? element.Element("link")?.Value,
            expectedBaseUri);
        var torrentUri = TryCreateSafeUri(
            element.Element("link")?.Value
                ?? element.Element("enclosure")?.Attribute("url")?.Value,
            expectedBaseUri);

        if (string.IsNullOrWhiteSpace(title) || detailsUri is null)
            return null;

        var id = ReadNyaaValue(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            var segment = detailsUri.Segments.LastOrDefault()?.Trim('/');
            id = long.TryParse(segment, out _) ? segment : null;
        }

        if (torrentUri is null || !torrentUri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            torrentUri = new Uri(expectedBaseUri, $"/download/{Uri.EscapeDataString(id)}.torrent");
        }

        if (!IsAllowedNyaaUri(torrentUri, expectedBaseUri)
            || !IsAllowedNyaaUri(detailsUri, expectedBaseUri))
        {
            return null;
        }

        DateTimeOffset? publishedAt = DateTimeOffset.TryParse(
            element.Element("pubDate")?.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsedDate)
            ? parsedDate
            : null;

        return new NyaaTorrentItem(
            id ?? torrentUri.AbsolutePath,
            title,
            torrentUri,
            detailsUri,
            ReadNyaaValue(element, "category") ?? "",
            ParseSize(ReadNyaaValue(element, "size")),
            ParseInt(ReadNyaaValue(element, "seeders")),
            ParseInt(ReadNyaaValue(element, "leechers")),
            ParseInt(ReadNyaaValue(element, "downloads")),
            publishedAt,
            ParseBoolean(ReadNyaaValue(element, "trusted")),
            ParseBoolean(ReadNyaaValue(element, "remake")));
    }

    private static string? ReadNyaaValue(XElement element, string localName) =>
        element.Element(NyaaNamespace + localName)?.Value.Trim()
        ?? element.Elements().FirstOrDefault(candidate =>
            candidate.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.NamespaceName.Contains("nyaa", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

    private static Uri? TryCreateSafeUri(string? value, Uri expectedBaseUri)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(expectedBaseUri, value.Trim(), out var uri)
            && IsAllowedNyaaUri(uri, expectedBaseUri)
                ? uri
                : null;
    }

    private static bool IsAllowedNyaaUri(Uri uri, Uri expectedBaseUri) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(expectedBaseUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == expectedBaseUri.Port
        && uri.UserInfo.Length == 0;

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;

    private static bool ParseBoolean(string? value) =>
        value is not null
        && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    internal static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return 0;
        }

        var multiplier = parts.Length < 2
            ? 1d
            : parts[1].Trim().ToUpperInvariant() switch
            {
                "KIB" or "KB" => 1024d,
                "MIB" or "MB" => 1024d * 1024,
                "GIB" or "GB" => 1024d * 1024 * 1024,
                "TIB" or "TB" => 1024d * 1024 * 1024 * 1024,
                _ => 1d,
            };
        return amount <= 0 ? 0 : checked((long)Math.Min(long.MaxValue, amount * multiplier));
    }
}
