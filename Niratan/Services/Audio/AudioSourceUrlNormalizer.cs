using System;

namespace Niratan.Services.Audio;

internal static class AudioSourceUrlNormalizer
{
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        var normalized = url.Replace("\\", "/", StringComparison.Ordinal);
        var schemeSeparator = normalized.IndexOf(":/", StringComparison.Ordinal);
        if (schemeSeparator > 1
            && schemeSeparator + 2 < normalized.Length
            && normalized[schemeSeparator + 2] != '/')
        {
            normalized = normalized.Insert(schemeSeparator + 2, "/");
        }

        return RewriteHttpLocalhostToIpv4Loopback(normalized);
    }

    private static string RewriteHttpLocalhostToIpv4Loopback(string url)
    {
        const string localhostPrefix = "http://localhost";
        if (!url.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase))
            return url;

        var boundary = localhostPrefix.Length;
        if (boundary < url.Length && url[boundary] is not (':' or '/' or '?' or '#'))
            return url;

        return "http://127.0.0.1" + url[boundary..];
    }
}
