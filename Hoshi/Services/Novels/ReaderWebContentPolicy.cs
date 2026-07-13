using System;

namespace Hoshi.Services.Novels;

public static class ReaderWebContentPolicy
{
    public const string BookHostName = "hoshi-novel-book.local";

    public const string ChapterResponseHeaders =
        "Content-Security-Policy: default-src 'none'; script-src 'none'; "
        + "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "font-src 'self' data: blob:; media-src 'self' data: blob:; "
        + "connect-src 'none'; frame-src 'none'; child-src 'none'; "
        + "object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'\r\n"
        + "X-Content-Type-Options: nosniff\r\n"
        + "Referrer-Policy: no-referrer\r\n";

    public static bool IsAllowedTopLevelNavigation(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IdnHost.Equals(BookHostName, StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsAllowedFrameNavigation(string? value) => false;

    public static bool IsHtmlMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        var normalized = mediaType.Split(';', 2)[0].Trim();
        return normalized.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("text/html", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedWebMessageSource(
        string? source,
        string? currentDocument)
    {
        if (!IsAllowedTopLevelNavigation(source)
            || !IsAllowedTopLevelNavigation(currentDocument)
            || !Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
            || !Uri.TryCreate(currentDocument, UriKind.Absolute, out var currentUri))
        {
            return false;
        }

        return Uri.Compare(
            sourceUri,
            currentUri,
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }
}
