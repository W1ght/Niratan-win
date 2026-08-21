using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Services.Video;

/// <summary>
/// Supports both TMDB's v4 bearer token and the legacy v3 API key while keeping
/// the credential value out of application diagnostics.
/// </summary>
internal static class TmdbCredentialAuth
{
    public static Uri Apply(Uri uri, string credential)
    {
        if (!LooksLikeV3ApiKey(credential))
            return uri;

        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        if (query.Length > 0)
            query += "&";
        query += "api_key=" + Uri.EscapeDataString(credential);
        builder.Query = query;
        return builder.Uri;
    }

    public static IReadOnlyDictionary<string, string> Headers(string credential) =>
        LooksLikeV3ApiKey(credential)
            ? new Dictionary<string, string> { ["Accept"] = "application/json" }
            : new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + credential,
                ["Accept"] = "application/json",
            };

    private static bool LooksLikeV3ApiKey(string credential) =>
        credential.Length >= 20
        && credential.Length <= 64
        && credential.IndexOf('.') < 0
        && credential.IndexOfAny([' ', '\t', '\r', '\n']) < 0
        && credential.All(char.IsLetterOrDigit);
}
