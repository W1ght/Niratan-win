using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.ZLibrary;

namespace Niratan.Services.ZLibrary;

public sealed class ZLibraryClient : IZLibraryClient, IDisposable
{
    private const long MaximumEpubBytes = 512L * 1024 * 1024;
    private const string ApiUserAgent = "Niratan/0.6 (Windows; Z-Library client)";
    private const int MaximumTransientRetries = 2;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public ZLibraryClient()
        : this(
            new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            })
            {
                Timeout = TimeSpan.FromSeconds(45),
            },
            ownsHttpClient: true)
    {
    }

    internal ZLibraryClient(
        HttpClient httpClient,
        bool ownsHttpClient = false,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
    }

    public async Task<ZLibrarySession> LoginAsync(
        ZLibraryCredentials credentials,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var baseUri = NormalizeBaseUri(credentials.BaseUrl);
        if (string.IsNullOrWhiteSpace(credentials.Email)
            || string.IsNullOrWhiteSpace(credentials.Password))
        {
            throw new ZLibraryException("Enter both the Z-Library email and password.");
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            new Uri(baseUri, "eapi/user/login"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = credentials.Email.Trim(),
            ["password"] = credentials.Password,
        });

        using var response = await SendAsync(request, ct);
        using var document = await ReadJsonAsync(response, "sign-in", ct);
        var root = document.RootElement;
        EnsureApiSuccess(root, "Z-Library sign-in failed.");

        JsonElement sessionData;
        if (!TryGetObject(root, "user", out sessionData)
            && !TryGetObject(root, "response", out sessionData))
        {
            throw new ZLibraryException("Z-Library returned an invalid sign-in session.");
        }

        var userId = GetString(sessionData, "id") ?? GetString(sessionData, "user_id");
        var userKey = GetString(sessionData, "remix_userkey")
            ?? GetString(sessionData, "user_key");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userKey))
            throw new ZLibraryException("Z-Library did not return a usable sign-in session.");

        ValidateCookieValue(userId, "user id");
        ValidateCookieValue(userKey, "user key");
        return new ZLibrarySession(baseUri, userId, userKey);
    }

    public async Task<ZLibrarySearchResult> SearchAsync(
        ZLibrarySession session,
        ZLibrarySearchOptions options,
        int page = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query))
            throw new ZLibraryException("Enter a title, author, ISBN, or keyword.");
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (options.YearFrom is < 0 or > 9999 || options.YearTo is < 0 or > 9999)
            throw new ZLibraryException("Enter a valid publication year.");
        if (options.YearFrom is not null
            && options.YearTo is not null
            && options.YearFrom > options.YearTo)
        {
            throw new ZLibraryException("The start year cannot be later than the end year.");
        }

        return await SearchBooksApiAsync(session, options, page, ct);
    }

    private async Task<ZLibrarySearchResult> SearchBooksApiAsync(
        ZLibrarySession session,
        ZLibrarySearchOptions options,
        int page,
        CancellationToken ct)
    {
        using var request = CreateAuthedRequest(
            HttpMethod.Post,
            new Uri(session.BaseUri, "eapi/book/search"),
            session);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        var parameters = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("message", options.Query.Trim()),
            KeyValuePair.Create("page", page.ToString(CultureInfo.InvariantCulture)),
            KeyValuePair.Create("limit", "50"),
        };
        if (options.YearFrom is not null)
        {
            parameters.Add(KeyValuePair.Create(
                "yearFrom",
                options.YearFrom.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (options.YearTo is not null)
        {
            parameters.Add(KeyValuePair.Create(
                "yearTo",
                options.YearTo.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            parameters.Add(KeyValuePair.Create(
                "languages[0]",
                options.Language.Trim().ToLowerInvariant()));
        }
        if (!string.IsNullOrWhiteSpace(options.Extension))
        {
            parameters.Add(KeyValuePair.Create(
                "extensions[0]",
                options.Extension.Trim().ToLowerInvariant()));
        }
        request.Content = new FormUrlEncodedContent(parameters);

        using var response = await SendAsync(request, ct);
        using var document = await ReadJsonAsync(response, "search", ct);
        var root = document.RootElement;
        EnsureApiSuccess(root, "Z-Library search failed.");

        JsonElement booksElement = default;
        JsonElement exactMatch = default;
        var hasExactMatch = TryGetObject(root, "exactMatch", out exactMatch);
        var hasBooks = TryGetArray(root, "books", out booksElement);
        if (options.ExactMatching
            && hasExactMatch
            && TryGetArray(exactMatch, "books", out var exactBooks))
        {
            booksElement = exactBooks;
            hasBooks = true;
        }
        else if (!hasBooks
            && hasExactMatch
            && TryGetArray(exactMatch, "books", out exactBooks))
        {
            booksElement = exactBooks;
            hasBooks = true;
        }

        var books = hasBooks
            ? booksElement.EnumerateArray()
                .Select(ParseBook)
                .Where(book => book is not null)
                .Cast<ZLibraryBook>()
                .ToList()
            : [];

        var totalCount = options.ExactMatching
            ? GetInt32(root, "exactBooksCount")
            : null;
        int? totalPages = null;
        if (TryGetObject(root, "pagination", out var pagination))
        {
            totalCount ??= GetInt32(pagination, "total_items")
                ?? GetInt32(pagination, "totalItems");
            totalPages = GetInt32(pagination, "total_pages")
                ?? GetInt32(pagination, "totalPages");
        }
        totalCount ??= GetInt32(root, "booksCount");
        totalPages ??= books.Count > 0 ? 1 : 0;

        return new ZLibrarySearchResult(
            books,
            totalCount,
            totalCount?.ToString("N0", CultureInfo.CurrentCulture),
            page,
            totalPages);
    }

    public async Task DownloadEpubAsync(
        ZLibrarySession session,
        ZLibraryBook book,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        if (!string.Equals(book.Extension, "EPUB", StringComparison.OrdinalIgnoreCase))
            throw new ZLibraryException("Only EPUB books can be imported into the novel shelf.");

        Uri downloadUri;
        if (!string.IsNullOrWhiteSpace(book.DirectDownloadPath))
        {
            if (!Uri.TryCreate(session.BaseUri, book.DirectDownloadPath, out var directDownloadUri)
                || !SameOrigin(session.BaseUri, directDownloadUri))
            {
                throw new ZLibraryException("Z-Library returned an unsafe download link.");
            }
            downloadUri = directDownloadUri;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(book.Hash))
                throw new ZLibraryException("Z-Library did not return a usable download link.");

            var fileEndpoint = new Uri(
                session.BaseUri,
                $"eapi/book/{Uri.EscapeDataString(book.Id)}/{Uri.EscapeDataString(book.Hash)}/file");
            using var linkRequest = CreateAuthedRequest(HttpMethod.Get, fileEndpoint, session);
            using var linkResponse = await SendAsync(linkRequest, ct);
            using var linkDocument = await ReadJsonAsync(linkResponse, "download link", ct);
            var root = linkDocument.RootElement;
            EnsureApiSuccess(root, "Z-Library could not create a download link.");
            if (!TryGetObject(root, "file", out var file))
                throw new ZLibraryException("Z-Library returned an invalid download response.");

            var allowDownload = GetBoolean(file, "allowDownload");
            var downloadLink = GetString(file, "downloadLink");
            if (allowDownload == false || string.IsNullOrWhiteSpace(downloadLink))
            {
                throw new ZLibraryException(
                    GetString(file, "disallowDownloadMessage")
                    ?? "The account download limit has been reached. Try again later.");
            }

            if (!Uri.TryCreate(session.BaseUri, downloadLink, out var resolvedDownloadUri)
                || resolvedDownloadUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZLibraryException("Z-Library returned an unsafe download link.");
            }
            downloadUri = resolvedDownloadUri;
        }

        using var downloadRequest = SameOrigin(session.BaseUri, downloadUri)
            ? CreateAuthedRequest(HttpMethod.Get, downloadUri, session)
            : CreateRequest(HttpMethod.Get, downloadUri);
        var fallbackReferrer = new Uri(
            session.BaseUri,
            $"book/{Uri.EscapeDataString(book.Id)}/{Uri.EscapeDataString(book.Hash)}");
        downloadRequest.Headers.Referrer = Uri.TryCreate(
                session.BaseUri,
                book.DetailPath,
                out var detailUri)
            && SameOrigin(session.BaseUri, detailUri)
                ? detailUri
                : fallbackReferrer;
        using var downloadResponse = await SendFollowingSafeRedirectsAsync(
            downloadRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!downloadResponse.IsSuccessStatusCode)
            throw await CreateHttpExceptionAsync(downloadResponse, "book download", ct);

        var mediaType = downloadResponse.Content.Headers.ContentType?.MediaType;
        if (mediaType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            throw new ZLibraryException("The server returned a web page instead of an EPUB file.");
        if (downloadResponse.Content.Headers.ContentLength > MaximumEpubBytes)
            throw new ZLibraryException("The EPUB is larger than the 512 MB safety limit.");

        await using var source = await downloadResponse.Content.ReadAsStreamAsync(ct);
        await CopyWithLimitAsync(source, destination, MaximumEpubBytes, ct);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal static Uri NormalizeBaseUri(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ZLibraryException("Enter a valid HTTPS Z-Library server address.");
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd(ApiUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpRequestMessage CreateAuthedRequest(
        HttpMethod method,
        Uri uri,
        ZLibrarySession session)
    {
        ValidateCookieValue(session.UserId, "user id");
        ValidateCookieValue(session.UserKey, "user key");
        var request = CreateRequest(method, uri);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"remix_userid={session.UserId}; remix_userkey={session.UserKey}");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendFollowingSafeRedirectsAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ZLibraryException("The Z-Library request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ZLibraryException("Could not connect to the configured Z-Library server.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await CreateHttpExceptionAsync(response, "request", ct);
            response.Dispose();
            throw error;
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        HttpRequestMessage initialRequest,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        const int maximumRedirects = 5;
        var originalHeaders = initialRequest.Headers
            .ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var originalContentHeaders = initialRequest.Content?.Headers
            .ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var originalBody = initialRequest.Content is null
            ? null
            : await initialRequest.Content.ReadAsByteArrayAsync(ct);
        var currentRequest = initialRequest;
        var ownsCurrentRequest = false;
        var allowCredentialHeaders = true;

        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                var response = await SendWithTransientRetryAsync(
                    currentRequest,
                    completionOption,
                    ct);
                if (!IsRedirect(response.StatusCode))
                    return response;
                if (redirectCount >= maximumRedirects)
                {
                    response.Dispose();
                    throw new ZLibraryException("The Z-Library server redirected too many times.");
                }

                var location = response.Headers.Location;
                if (location is null
                    || !Uri.TryCreate(currentRequest.RequestUri, location, out var nextUri)
                    || nextUri.Scheme != Uri.UriSchemeHttps)
                {
                    response.Dispose();
                    throw new ZLibraryException("The Z-Library server returned an unsafe redirect.");
                }

                var preservesMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect;
                var currentMethod = currentRequest.Method;
                var nextMethod = preservesMethod ? currentMethod : HttpMethod.Get;
                var crossesOrigin = !SameOrigin(currentRequest.RequestUri!, nextUri);
                if (crossesOrigin
                    && preservesMethod
                    && currentMethod is not null
                    && currentMethod != HttpMethod.Get
                    && currentMethod != HttpMethod.Head)
                {
                    response.Dispose();
                    throw new ZLibraryException(
                        "The configured server redirected a credential-bearing request to another site. Update the server address instead.");
                }

                response.Dispose();
                if (ownsCurrentRequest)
                    currentRequest.Dispose();

                var nextRequest = new HttpRequestMessage(nextMethod, nextUri);
                if (crossesOrigin)
                    allowCredentialHeaders = false;
                foreach (var (name, values) in originalHeaders)
                {
                    if (!allowCredentialHeaders
                        && (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Referer", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    nextRequest.Headers.TryAddWithoutValidation(name, values);
                }

                if (preservesMethod && originalBody is not null)
                {
                    nextRequest.Content = new ByteArrayContent(originalBody);
                    if (originalContentHeaders is not null)
                    {
                        foreach (var (name, values) in originalContentHeaders)
                            nextRequest.Content.Headers.TryAddWithoutValidation(name, values);
                    }
                }

                currentRequest = nextRequest;
                ownsCurrentRequest = true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ZLibraryException("The Z-Library request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ZLibraryException("Could not connect to the configured Z-Library server.", ex);
        }
        finally
        {
            if (ownsCurrentRequest)
                currentRequest.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private async Task<HttpResponseMessage> SendWithTransientRetryAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var canRetry = request.Method == HttpMethod.Get
            || request.Method == HttpMethod.Head
            || (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.Equals(
                    "/eapi/book/search",
                    StringComparison.OrdinalIgnoreCase) == true);
        if (!canRetry)
            return await _httpClient.SendAsync(request, completionOption, ct);

        var requestBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(ct);
        var contentHeaders = request.Content?.Headers
            .ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var currentRequest = request;
        var ownsCurrentRequest = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await _httpClient.SendAsync(currentRequest, completionOption, ct);
                if (!IsTransientGatewayFailure(response.StatusCode)
                    || attempt >= MaximumTransientRetries)
                {
                    return response;
                }

                var delay = GetTransientRetryDelay(response, attempt);
                response.Dispose();
                await _delayAsync(delay, ct);

                if (ownsCurrentRequest)
                    currentRequest.Dispose();
                currentRequest = CloneRequest(request, requestBody, contentHeaders);
                ownsCurrentRequest = true;
            }
        }
        finally
        {
            if (ownsCurrentRequest)
                currentRequest.Dispose();
        }
    }

    private static bool IsTransientGatewayFailure(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetTransientRetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is DateTimeOffset retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : attempt == 0
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromMilliseconds(1500));
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
    }

    private static HttpRequestMessage CloneRequest(
        HttpRequestMessage source,
        byte[]? body,
        IReadOnlyDictionary<string, string[]>? contentHeaders)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };
        foreach (var (name, values) in source.Headers)
            clone.Headers.TryAddWithoutValidation(name, values);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (contentHeaders is not null)
            {
                foreach (var (name, values) in contentHeaders)
                    clone.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }
        return clone;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new ZLibraryException(
                $"The Z-Library {operation} response was not valid JSON. The server may be showing a browser challenge.",
                ex);
        }
    }

    private static void EnsureApiSuccess(JsonElement root, string fallback)
    {
        ThrowIfApiError(root, fallback);
        var success = GetInt32(root, "success");
        if (success is not null && success != 1)
            throw new ZLibraryException(GetApiMessage(root) ?? fallback);
    }

    private static void ThrowIfApiError(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("error", out var error)
            || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        var message = error.ValueKind == JsonValueKind.Object
            ? GetString(error, "message")
            : ElementToString(error);
        throw new ZLibraryException(string.IsNullOrWhiteSpace(message) ? fallback : message);
    }

    private static string? GetApiMessage(JsonElement root) =>
        GetString(root, "message")
        ?? (TryGetObject(root, "response", out var response)
            ? GetString(response, "message")
            : null);

    private static long? ParseFileSize(string value)
    {
        var match = Regex.Match(
            value,
            @"^\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>B|KB|MB|GB)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (!match.Success
            || !decimal.TryParse(
                match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024m,
            "MB" => 1024m * 1024m,
            "GB" => 1024m * 1024m * 1024m,
            _ => 1m,
        };
        var bytes = number * multiplier;
        return bytes is >= 0 and <= long.MaxValue ? (long)bytes : null;
    }

    private static ZLibraryBook? ParseBook(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        var id = GetString(value, "id");
        var hash = GetString(value, "hash");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hash))
            return null;

        var coverText = GetString(value, "cover");
        Uri? coverUri = null;
        if (Uri.TryCreate(coverText, UriKind.Absolute, out var parsedCover)
            && parsedCover.Scheme == Uri.UriSchemeHttps)
        {
            coverUri = parsedCover;
        }

        return new ZLibraryBook(
            id,
            hash,
            GetString(value, "title")?.Trim() is { Length: > 0 } title ? title : "Unknown title",
            GetString(value, "author")?.Trim() is { Length: > 0 } author ? author : "Unknown author",
            GetString(value, "extension") ?? "Unknown",
            GetString(value, "filesizeString") ?? GetString(value, "filesize") ?? "Unknown size",
            GetInt64(value, "filesize"),
            GetString(value, "language") ?? "Unknown",
            GetInt32(value, "year"),
            coverUri);
    }

    private static bool TryGetObject(JsonElement parent, string property, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement parent, string property, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var value)
            ? ElementToString(value)
            : null;

    private static string? ElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static int? GetInt32(JsonElement parent, string property)
    {
        var text = GetString(parent, property);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? GetInt64(JsonElement parent, string property)
    {
        var text = GetString(parent, property);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetBoolean(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static void ValidateCookieValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny([';', '\r', '\n']) >= 0)
        {
            throw new ZLibraryException($"Z-Library returned an invalid {name}.");
        }
    }

    private static async Task<ZLibraryException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        string? message = null;
        string? responseText = null;
        try
        {
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(responseText)
                && responseText.Length <= 64 * 1024)
            {
                using var json = JsonDocument.Parse(responseText);
                message = GetApiMessage(json.RootElement)
                    ?? GetString(json.RootElement, "error");
            }
        }
        catch (JsonException)
        {
        }

        if (IsBrowserChallenge(response, responseText))
        {
            return new ZLibraryException(
                "This Z-Library server requires browser verification and cannot be used by the in-app client. "
                + "Paste the current HTTPS server address shown by Z-Access, reconnect, and try again.");
        }

        return new ZLibraryException(
            message
            ?? $"Z-Library {operation} failed with HTTP {(int)response.StatusCode}.");
    }

    private static bool IsBrowserChallenge(
        HttpResponseMessage response,
        string? responseText)
    {
        if ((int)response.StatusCode == 513)
            return true;

        if (response.Headers.TryGetValues("Server", out var serverValues)
            && serverValues.Any(value =>
                value.Contains("DiamWall", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return responseText is not null
            && (responseText.Contains("Verifying your browser", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("diamwall", StringComparison.OrdinalIgnoreCase)
                || responseText.Contains("cdn-cgi/challenge", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw new ZLibraryException("The EPUB is larger than the 512 MB safety limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
