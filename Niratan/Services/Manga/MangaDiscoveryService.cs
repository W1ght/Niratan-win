using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaDiscoveryService :
    IMangaDiscoveryService,
    IMangaDiscoveryBatchService
{
    private const int RecommendationPageSize = 12;
    private const int SearchPageSize = 24;
    private const string UserAgent =
        "wight554/Niratan/0.9 (https://github.com/wight554/Hoshi-Reader)";
    private const string AniListMediaFields =
        "id title{romaji english native}synonyms description startDate{year}" +
        "averageScore coverImage{extraLarge large}siteUrl";
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const long MaxPosterBytes = 20L * 1024 * 1024;
    private static readonly TimeSpan PageCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DiscoveryRequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PosterRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PosterCacheLifetime = TimeSpan.FromDays(30);
    private static readonly HttpClient s_redirectSafeHttpClient = CreateRedirectSafeHttpClient();
    private readonly HttpClient _http;
    private readonly string _posterCacheRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _posterLocks = new();
    private readonly ConcurrentDictionary<DiscoveryPageCacheKey, DiscoveryPageCacheEntry>
        _pageCache = [];
    private readonly ConcurrentDictionary<DiscoveryPageCacheKey, SemaphoreSlim>
        _pageCacheLocks = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _batchCacheLocks =
        new(StringComparer.Ordinal);
    private int _cacheGeneration;

    public MangaDiscoveryService()
        : this(s_redirectSafeHttpClient)
    {
    }

    internal MangaDiscoveryService(
        HttpClient http,
        string? posterCacheRoot = null)
    {
        _http = http;
        _posterCacheRoot = posterCacheRoot ?? Path.Combine(
            AppDataHelper.GetMangaCachePath(),
            "Discovery");
        Directory.CreateDirectory(_posterCacheRoot);
    }

    private static HttpClient CreateRedirectSafeHttpClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        _pageCache.Clear();
    }

    public IReadOnlyList<MangaDiscoveryProvider> Providers { get; } =
    [
        new("bangumi", "Bangumi"),
        new("anilist", "AniList"),
    ];

    public IReadOnlyList<MangaDiscoveryFeed> GetFeeds(
        string providerId,
        MangaDiscoveryFeedKind kind) =>
        providerId.ToLowerInvariant() switch
        {
            "bangumi" =>
            new MangaDiscoveryFeed[]
            {
                new MangaDiscoveryFeed("bangumi", "rank", "Top rated manga", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "heat", "Popular manga", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "date", "Latest manga", MangaDiscoveryFeedKind.Recommendation),
            }.Where(feed => feed.Kind == kind).ToList(),
            "anilist" =>
            new MangaDiscoveryFeed[]
            {
                new MangaDiscoveryFeed("anilist", "trending", "Trending manga", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("anilist", "popular", "Popular manga", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("anilist", "updated", "Latest manga", MangaDiscoveryFeedKind.Recommendation),
            }.Where(feed => feed.Kind == kind).ToList(),
            _ => new List<MangaDiscoveryFeed>(),
        };

    public async Task<MangaDiscoveryPage> GetPageAsync(
        string providerId,
        MangaDiscoveryRequest request,
        CancellationToken ct = default)
    {
        var normalizedProviderId = providerId.ToLowerInvariant();
        var normalizedRequest = NormalizeRequest(request);
        var key = CreatePageCacheKey(
            normalizedProviderId,
            normalizedRequest,
            query: null);
        return await GetCachedPageAsync(
            key,
            async token => normalizedProviderId switch
            {
                "bangumi" => await GetBangumiPageAsync(normalizedRequest, token),
                "anilist" => await GetAniListPageAsync(normalizedRequest, null, token),
                _ => throw new ArgumentException(
                    "Unknown manga discovery provider.",
                    nameof(providerId)),
            },
            ct);
    }

    public async Task<IReadOnlyList<MangaDiscoveryPage>> GetPagesAsync(
        string providerId,
        IReadOnlyList<MangaDiscoveryRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return [];

        var normalizedProviderId = providerId.ToLowerInvariant();
        var normalizedRequests = requests.Select(NormalizeRequest).ToList();
        if (normalizedProviderId.Equals("anilist", StringComparison.Ordinal))
        {
            var keys = normalizedRequests
                .Select(request => CreatePageCacheKey(
                    normalizedProviderId,
                    request,
                    query: null))
                .ToList();
            if (TryGetCachedPages(keys, out var cachedPages))
                return cachedPages;

            var batchKey = string.Join(
                ";",
                keys.Select(key => $"{key.ProviderId}:{key.FeedId}:{key.Page}"));
            var gate = _batchCacheLocks.GetOrAdd(
                batchKey,
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (TryGetCachedPages(keys, out cachedPages))
                    return cachedPages;

                var generation = Volatile.Read(ref _cacheGeneration);
                var pages = await GetAniListPagesAsync(normalizedRequests, ct);
                var createdAt = DateTimeOffset.UtcNow;
                for (var index = 0; index < pages.Count; index++)
                {
                    _pageCache[keys[index]] = new DiscoveryPageCacheEntry(
                        pages[index],
                        createdAt,
                        generation);
                }
                return pages;
            }
            finally
            {
                gate.Release();
            }
        }

        return await Task.WhenAll(
            normalizedRequests.Select(request =>
                GetPageAsync(normalizedProviderId, request, ct)));
    }

    public async Task<MangaDiscoveryPage> SearchAsync(
        string providerId,
        string query,
        int page = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Enter a manga title before searching.", nameof(query));

        var normalizedProviderId = providerId.ToLowerInvariant();
        var normalizedQuery = query.Trim();
        var request = NormalizeRequest(new MangaDiscoveryRequest("search", page));
        var key = CreatePageCacheKey(
            normalizedProviderId,
            request,
            normalizedQuery);
        return await GetCachedPageAsync(
            key,
            async token => normalizedProviderId switch
            {
                "bangumi" => await SearchBangumiAsync(
                    normalizedQuery,
                    request.Page,
                    token),
                "anilist" => await GetAniListPageAsync(
                    request,
                    normalizedQuery,
                    token),
                _ => throw new ArgumentException(
                    "Unknown manga discovery provider.",
                    nameof(providerId)),
            },
            ct);
    }

    private async Task<MangaDiscoveryPage> GetCachedPageAsync(
        DiscoveryPageCacheKey key,
        Func<CancellationToken, Task<MangaDiscoveryPage>> loader,
        CancellationToken ct)
    {
        if (TryGetCachedPage(key, out var cached))
            return cached;

        var gate = _pageCacheLocks.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetCachedPage(key, out cached))
                return cached;

            var generation = Volatile.Read(ref _cacheGeneration);
            var page = await loader(ct);
            _pageCache[key] = new DiscoveryPageCacheEntry(
                page,
                DateTimeOffset.UtcNow,
                generation);
            return page;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetCachedPages(
        IReadOnlyList<DiscoveryPageCacheKey> keys,
        out IReadOnlyList<MangaDiscoveryPage> pages)
    {
        var cachedPages = new List<MangaDiscoveryPage>(keys.Count);
        foreach (var key in keys)
        {
            if (!TryGetCachedPage(key, out var page))
            {
                pages = [];
                return false;
            }
            cachedPages.Add(page);
        }

        pages = cachedPages;
        return true;
    }

    private bool TryGetCachedPage(
        DiscoveryPageCacheKey key,
        out MangaDiscoveryPage page)
    {
        if (_pageCache.TryGetValue(key, out var entry))
        {
            if (entry.Generation == Volatile.Read(ref _cacheGeneration)
                && DateTimeOffset.UtcNow - entry.CreatedAt <= PageCacheLifetime)
            {
                page = entry.Page;
                return true;
            }

            _pageCache.TryRemove(key, out _);
        }

        page = default!;
        return false;
    }

    private static MangaDiscoveryRequest NormalizeRequest(
        MangaDiscoveryRequest request) =>
        new(request.FeedId.ToLowerInvariant(), Math.Max(1, request.Page));

    private static DiscoveryPageCacheKey CreatePageCacheKey(
        string providerId,
        MangaDiscoveryRequest request,
        string? query) =>
        new(
            providerId.ToLowerInvariant(),
            request.FeedId.ToLowerInvariant(),
            Math.Max(1, request.Page),
            query);

    public async Task<string?> GetPosterPathAsync(
        MangaDiscoveryItem item,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.PosterUrl)
            || !Uri.TryCreate(item.PosterUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        ValidatePosterUri(uri);
        var key = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))
            .ToLowerInvariant();
        var gate = _posterLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await GetPosterPathCoreAsync(uri, key, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> GetPosterPathCoreAsync(
        Uri uri,
        string key,
        CancellationToken ct)
    {
        var metadataPath = Path.Combine(_posterCacheRoot, key + ".json");
        if (File.Exists(metadataPath))
        {
            PosterMetadata? metadata = null;
            try
            {
                metadata = JsonSerializer.Deserialize<PosterMetadata>(
                    await File.ReadAllTextAsync(metadataPath, ct));
                if (metadata is not null
                    && metadata.Url == uri.AbsoluteUri
                    && IsExpectedPosterCachePath(metadata.Path, key)
                    && File.Exists(metadata.Path)
                    && DateTimeOffset.UtcNow - metadata.FetchedAt < PosterCacheLifetime)
                {
                    _ = DetectImageExtension(metadata.Path, contentType: null);
                    return metadata.Path;
                }
            }
            catch (Exception ex) when (ex is JsonException
                                       or InvalidDataException
                                       or IOException
                                       or UnauthorizedAccessException)
            {
                // Re-fetch damaged cache metadata or an invalid image file.
            }

            if (metadata is not null && IsExpectedPosterCachePath(metadata.Path, key))
                TryDeleteFile(metadata.Path);
            TryDeleteFile(metadataPath);
        }

        using var posterTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        posterTimeoutCts.CancelAfter(PosterRequestTimeout);
        using var posterRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        posterRequest.Headers.TryAddWithoutValidation(
            "User-Agent",
            UserAgent);
        posterRequest.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await _http.SendAsync(
            posterRequest,
            HttpCompletionOption.ResponseHeadersRead,
            posterTimeoutCts.Token);
        RejectRedirect(response, "poster");
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null
            && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manga poster response is not an image.");
        }

        var temporaryPath = Path.Combine(
            _posterCacheRoot,
            key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(posterTimeoutCts.Token))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, posterTimeoutCts.Token);
                    if (read == 0)
                        break;
                    if (output.Length + read > MaxPosterBytes)
                        throw new InvalidDataException("Manga poster exceeds the cache limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), posterTimeoutCts.Token);
                }
                await output.FlushAsync(posterTimeoutCts.Token);
            }

            var extension = DetectImageExtension(temporaryPath, contentType);
            var finalPath = Path.Combine(_posterCacheRoot, key + extension);
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(temporaryPath, finalPath);
            var metadata = new PosterMetadata(uri.AbsoluteUri, finalPath, DateTimeOffset.UtcNow);
            var metadataTemporaryPath = metadataPath + ".tmp";
            await File.WriteAllTextAsync(
                metadataTemporaryPath,
                JsonSerializer.Serialize(metadata),
                posterTimeoutCts.Token);
            File.Move(metadataTemporaryPath, metadataPath, true);
            return finalPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<MangaDiscoveryPage> GetBangumiPageAsync(
        MangaDiscoveryRequest request,
        CancellationToken ct)
    {
        var feed = GetFeeds("bangumi", MangaDiscoveryFeedKind.Recommendation)
            .FirstOrDefault(item => item.Id.Equals(request.FeedId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown Bangumi manga feed.", nameof(request));
        var offset = Math.Max(0, request.Page - 1) * RecommendationPageSize;
        using var httpRequest = CreateBangumiFeedRequest(feed.Id, offset);
        using var json = await SendJsonAsync(httpRequest, "bangumi", ct);
        return MapBangumiPage(
            json,
            request.FeedId,
            request.Page,
            RecommendationPageSize);
    }

    private static HttpRequestMessage CreateBangumiFeedRequest(
        string feedId,
        int offset)
    {
        if (feedId is "rank" or "date")
        {
            var uri = new Uri(
                $"https://api.bgm.tv/v0/subjects?type=1&cat=1001&series=true&sort={feedId}&limit={RecommendationPageSize}&offset={offset}");
            return new HttpRequestMessage(HttpMethod.Get, uri);
        }

        var searchUri = new Uri(
            $"https://api.bgm.tv/v0/search/subjects?limit={RecommendationPageSize}&offset={offset}");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            keyword = string.Empty,
            sort = "heat",
            filter = new
            {
                type = new[] { 1 },
                meta_tags = new[] { "漫画" },
            },
        });
        var request = new HttpRequestMessage(HttpMethod.Post, searchUri)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private async Task<MangaDiscoveryPage> SearchBangumiAsync(
        string query,
        int page,
        CancellationToken ct)
    {
        var uri = new Uri(
            $"https://api.bgm.tv/v0/search/subjects?limit={SearchPageSize}&offset={Math.Max(0, page - 1) * SearchPageSize}");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            keyword = query,
            sort = "match",
            filter = new
            {
                type = new[] { 1 },
                meta_tags = new[] { "漫画" },
            },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var json = await SendJsonAsync(request, "bangumi", ct);
        return MapBangumiPage(json, "search", page, SearchPageSize);
    }

    private async Task<MangaDiscoveryPage> GetAniListPageAsync(
        MangaDiscoveryRequest request,
        string? query,
        CancellationToken ct)
    {
        var sort = GetAniListSort(request.FeedId);
        var graph = query is null
            ? "query($page:Int,$perPage:Int){Page(page:$page,perPage:$perPage){pageInfo{lastPage}" +
              "media(type:MANGA,sort:[" + sort + "]){" + AniListMediaFields + "}}}"
            : "query($page:Int,$perPage:Int,$search:String){Page(page:$page,perPage:$perPage){pageInfo{lastPage}" +
              "media(type:MANGA,sort:[" + sort + "],search:$search){" + AniListMediaFields + "}}}";
        var variables = new Dictionary<string, object?>
        {
            ["page"] = Math.Max(1, request.Page),
            ["perPage"] = query is null
                ? RecommendationPageSize
                : SearchPageSize,
        };
        if (query is not null)
            variables["search"] = query;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query = graph,
            variables,
        });
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://graphql.anilist.co"))
        {
            Content = new ByteArrayContent(body),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var json = await SendJsonAsync(httpRequest, "anilist", ct);
        ThrowIfAniListError(json.RootElement);

        var page = json.RootElement.GetProperty("data").GetProperty("Page");
        return MapAniListPage(page, request.FeedId, request.Page);
    }

    private async Task<IReadOnlyList<MangaDiscoveryPage>> GetAniListPagesAsync(
        IReadOnlyList<MangaDiscoveryRequest> requests,
        CancellationToken ct)
    {
        var selections = requests.Select((request, index) =>
        {
            if (request.FeedId.Equals("search", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Search requests cannot be batched as recommendations.", nameof(requests));

            var sort = GetAniListSort(request.FeedId);
            return $"feed{index}:Page(page:{Math.Max(1, request.Page)},perPage:{RecommendationPageSize})" +
                   "{pageInfo{lastPage}media(type:MANGA,sort:[" + sort + "]){" +
                   AniListMediaFields + "}}";
        });
        var graph = "query{" + string.Concat(selections) + "}";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { query = graph });
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://graphql.anilist.co"))
        {
            Content = new ByteArrayContent(body),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var json = await SendJsonAsync(httpRequest, "anilist", ct);
        ThrowIfAniListError(json.RootElement);

        var data = json.RootElement.GetProperty("data");
        return requests.Select((request, index) =>
                MapAniListPage(
                    data.GetProperty($"feed{index}"),
                    request.FeedId,
                    request.Page))
            .ToList();
    }

    private static MangaDiscoveryPage MapAniListPage(
        JsonElement page,
        string feedId,
        int pageNumber)
    {
        var pageInfo = page.GetProperty("pageInfo");
        var items = page.GetProperty("media")
            .EnumerateArray()
            .Select(MapAniListItem)
            .Where(item => item is not null)
            .Cast<MangaDiscoveryItem>()
            .ToList();
        return new MangaDiscoveryPage(
            "anilist",
            feedId,
            pageNumber,
            Int(pageInfo, "lastPage"),
            items);
    }

    private static string GetAniListSort(string feedId) =>
        feedId.ToLowerInvariant() switch
        {
            "popular" => "POPULARITY_DESC",
            "updated" => "UPDATED_AT_DESC",
            "search" => "SEARCH_MATCH",
            "trending" => "TRENDING_DESC",
            _ => throw new ArgumentException("Unknown AniList manga feed.", nameof(feedId)),
        };

    private static void ThrowIfAniListError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            return;
        }

        var message = errors[0].TryGetProperty("message", out var errorMessage)
            ? errorMessage.GetString()
            : null;
        throw new InvalidOperationException(message ?? "AniList returned a GraphQL error.");
    }

    private static MangaDiscoveryPage MapBangumiPage(
        JsonDocument json,
        string feedId,
        int page,
        int pageSize)
    {
        var root = json.RootElement;
        var items = root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Select(MapBangumiItem)
                .Where(item => item is not null)
                .Cast<MangaDiscoveryItem>()
                .ToList()
            : [];
        var total = Int(root, "total");
        var totalPages = total is int count
            ? (int?)Math.Max(1, (int)Math.Ceiling(count / (double)pageSize))
            : null;
        return new MangaDiscoveryPage("bangumi", feedId, page, totalPages, items);
    }

    private static MangaDiscoveryItem? MapBangumiItem(JsonElement item)
    {
        var id = Int(item, "id");
        var native = String(item, "name");
        var translated = String(item, "name_cn");
        if (id is null || string.IsNullOrWhiteSpace(native ?? translated))
            return null;
        var images = item.TryGetProperty("images", out var imageObject)
            ? String(imageObject, "large") ?? String(imageObject, "common")
            : null;
        var rating = item.TryGetProperty("rating", out var ratingObject)
            ? Double(ratingObject, "score")
            : null;
        var rank = item.TryGetProperty("rating", out ratingObject)
            ? Int(ratingObject, "rank")
            : null;
        var idText = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new MangaDiscoveryItem(
            "bangumi",
            idText,
            translated ?? native!,
            native,
            Year(String(item, "date")),
            String(item, "summary"),
            rating,
            rank,
            images,
            $"https://bgm.tv/subject/{idText}",
            Titles(native, translated));
    }

    private static MangaDiscoveryItem? MapAniListItem(JsonElement item)
    {
        var id = Int(item, "id");
        if (id is null || !item.TryGetProperty("title", out var titleObject))
            return null;
        var native = String(titleObject, "native");
        var romaji = String(titleObject, "romaji");
        var english = String(titleObject, "english");
        var title = native ?? english ?? romaji;
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var cover = item.TryGetProperty("coverImage", out var coverObject)
            ? String(coverObject, "extraLarge") ?? String(coverObject, "large")
            : null;
        var idText = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var score = Double(item, "averageScore");
        var aliases = new List<string>(Titles(native, english, romaji));
        if (item.TryGetProperty("synonyms", out var synonymObject)
            && synonymObject.ValueKind == JsonValueKind.Array)
        {
            aliases.AddRange(
                synonymObject.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!));
        }
        return new MangaDiscoveryItem(
            "anilist",
            idText,
            title!,
            romaji,
            item.TryGetProperty("startDate", out var dateObject)
                ? Int(dateObject, "year")
                : null,
            ToPlainText(String(item, "description")),
            score is double value ? value / 10 : null,
            null,
            cover,
            String(item, "siteUrl") ?? $"https://anilist.co/manga/{idText}",
            aliases
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    private static string? ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var withLineBreaks = Regex.Replace(
            value,
            "<br\\s*/?>",
            Environment.NewLine,
            RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withLineBreaks, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        string providerId,
        CancellationToken ct)
    {
        ValidateEndpoint(request.RequestUri!, providerId);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DiscoveryRequestTimeout);
        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            RejectRedirect(response, "metadata");
            if (response.Content.Headers.ContentLength is > MaxJsonBytes)
                throw new InvalidDataException("Manga discovery response exceeds the size limit.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            await using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0)
                    break;
                if (output.Length + read > MaxJsonBytes)
                    throw new InvalidDataException("Manga discovery response exceeds the size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                   && timeoutCts.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "Manga discovery request timed out. Try another source or refresh later.");
        }
    }

    private static void RejectRedirect(
        HttpResponseMessage response,
        string resourceKind)
    {
        var statusCode = (int)response.StatusCode;
        if (statusCode is 300 or 301 or 302 or 303 or 307 or 308)
        {
            throw new InvalidOperationException(
                $"Manga discovery {resourceKind} redirects are not allowed.");
        }
    }

    private static void ValidateEndpoint(Uri uri, string providerId)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Manga discovery requests must use HTTPS.");
        var allowed = providerId.Equals("bangumi", StringComparison.OrdinalIgnoreCase)
            ? uri.IdnHost.Equals("api.bgm.tv", StringComparison.OrdinalIgnoreCase)
            : uri.IdnHost.Equals("graphql.anilist.co", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            throw new InvalidOperationException("Manga discovery host is not allowlisted.");
    }

    private static void ValidatePosterUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !new[] { "lain.bgm.tv", "s4.anilist.co", "img.anilist.co" }
                .Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Manga poster host is not allowlisted.");
        }
    }

    private static string DetectImageExtension(string path, string? contentType)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read >= 3
            && header[0] == 0xff
            && header[1] == 0xd8
            && header[2] == 0xff
            && HasJpegEndMarker(stream))
        {
            return ".jpg";
        }
        if (read >= 24
            && header[..8].SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadInt32BigEndian(header[8..12]) == 13
            && Encoding.ASCII.GetString(header[12..16]) == "IHDR"
            && BinaryPrimitives.ReadInt32BigEndian(header[16..20]) > 0
            && BinaryPrimitives.ReadInt32BigEndian(header[20..24]) > 0
            && HasPngEndChunk(stream))
        {
            return ".png";
        }
        if (read >= 12
            && Encoding.ASCII.GetString(header[..4]) == "RIFF"
            && Encoding.ASCII.GetString(header[8..12]) == "WEBP"
            && BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8
                == stream.Length)
        {
            return ".webp";
        }
        throw new InvalidDataException(
            $"Manga poster response is not a supported image ({contentType ?? "unknown"}).");
    }

    private static bool HasJpegEndMarker(Stream stream)
    {
        if (stream.Length < 4)
            return false;

        stream.Seek(-2, SeekOrigin.End);
        Span<byte> tail = stackalloc byte[2];
        return stream.Read(tail) == tail.Length
               && tail[0] == 0xff
               && tail[1] == 0xd9;
    }

    private static bool HasPngEndChunk(Stream stream)
    {
        if (stream.Length < 33)
            return false;

        stream.Seek(-12, SeekOrigin.End);
        Span<byte> tail = stackalloc byte[12];
        return stream.Read(tail) == tail.Length
               && BinaryPrimitives.ReadInt32BigEndian(tail[..4]) == 0
               && Encoding.ASCII.GetString(tail[4..8]) == "IEND";
    }

    private bool IsExpectedPosterCachePath(string? path, string key)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return new[] { ".jpg", ".png", ".webp" }.Any(extension =>
                string.Equals(
                    fullPath,
                    Path.GetFullPath(Path.Combine(_posterCacheRoot, key + extension)),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? Double(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? Year(string? value) =>
        value?.Length >= 4
        && int.TryParse(value.AsSpan(0, 4), out var year)
            ? year
            : null;

    private static IReadOnlyList<string> Titles(params string?[] values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private sealed record PosterMetadata(
        string Url,
        string Path,
        DateTimeOffset FetchedAt);

    private sealed record DiscoveryPageCacheKey(
        string ProviderId,
        string FeedId,
        int Page,
        string? Query);

    private sealed record DiscoveryPageCacheEntry(
        MangaDiscoveryPage Page,
        DateTimeOffset CreatedAt,
        int Generation);
}
