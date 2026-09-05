using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Video;
using Niratan.Services.Settings;

namespace Niratan.Services.Video;

internal sealed class VideoDiscoveryService : IVideoDiscoveryService
{
    private static readonly TimeSpan PageCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DetailsCacheLifetime = TimeSpan.FromMinutes(30);
    private const int SecondaryArtworkConcurrency = 4;
    private const string SearchLanguage = "en-US";
    private const string SearchRegion = "US";
    private static readonly string[] AggregatedSearchProviderOrder = ["anilist", "tmdb"];
    private static readonly string[] AnimeLibrarySearchProviderOrder = ["anidb", "tmdb"];
    private static readonly string[] GeneralLibrarySearchProviderOrder = ["tmdb", "tvmaze"];
    private readonly IReadOnlyDictionary<string, IVideoDiscoveryProvider> _providers;
    private readonly IReadOnlyDictionary<string, IVideoMetadataSearchProvider> _searchProviders;
    private readonly IReadOnlyDictionary<string, IVideoMetadataDetailsProvider> _detailsProviders;
    private readonly IReadOnlyDictionary<string, IVideoArtworkProvider> _artworkProviders;
    private readonly IVideoMetadataTransport _transport;
    private readonly IVideoArtworkCache _artworkCache;
    private readonly ISettingsService _settings;
    private readonly ConcurrentDictionary<PageCacheKey, PageCacheEntry> _pageCache = [];
    private readonly ConcurrentDictionary<PageCacheKey, SemaphoreSlim> _pageCacheLocks = [];
    private readonly ConcurrentDictionary<DetailsCacheKey, DetailsCacheEntry> _detailsCache = [];
    private readonly ConcurrentDictionary<DetailsCacheKey, SemaphoreSlim> _detailsCacheLocks = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artworkLocks = new(StringComparer.Ordinal);
    private int _cacheGeneration;

    public VideoDiscoveryService(
        IEnumerable<IVideoDiscoveryProvider> providers,
        IEnumerable<IVideoMetadataDetailsProvider> detailsProviders,
        IEnumerable<IVideoArtworkProvider> artworkProviders,
        IVideoMetadataTransport transport,
        IVideoArtworkCache artworkCache,
        ISettingsService settings,
        IEnumerable<IVideoMetadataSearchProvider>? searchProviders = null)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _searchProviders = (searchProviders ?? []).ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _detailsProviders = detailsProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _artworkProviders = artworkProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _transport = transport;
        _artworkCache = artworkCache;
        _settings = settings;
    }

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        _pageCache.Clear();
        _detailsCache.Clear();
    }

    public IReadOnlyList<VideoDiscoveryFeed> GetFeeds(string providerId, VideoDiscoveryFeedKind kind)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
            return [];
        return provider.Feeds.Where(feed => feed.Kind == kind).ToList();
    }

    public async Task<Result<VideoDiscoveryPage>> GetPageAsync(
        string providerId,
        VideoDiscoveryRequest request,
        CancellationToken ct = default)
    {
        if (!_settings.Current.VideoSettings.Metadata.OnlineConsentAccepted)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverOnlineConsentRequired",
                    "Enable online video metadata in Video settings before using Discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverConsentTitle",
                    "Online metadata permission required"));
        if (!_providers.TryGetValue(providerId, out var provider))
            return Result<VideoDiscoveryPage>.Failure("The discovery provider is not available.", "Discovery unavailable");

        try
        {
            var page = await GetCachedPageAsync(
                new PageCacheKey(providerId, request, null),
                async token => await CachePageArtworkAsync(
                    await provider.GetPageAsync(request, token), token),
                ct);
            return Result<VideoDiscoveryPage>.Success(page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<VideoDiscoveryPage>.Cancelled();
        }
        catch (Exception ex)
        {
            var message = providerId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                && ex is HttpRequestException
                && ex.Message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
                ? ResourceStringHelper.GetString(
                    "DiscoverTmdbCredentialInvalid",
                    "TMDB authentication failed. Enter a TMDB v4 Read Token or a v3 API key, then refresh.")
                : ex.Message;
            return Result<VideoDiscoveryPage>.Failure(
                message,
                ResourceStringHelper.GetString(
                    "DiscoverProviderFailedTitle",
                    $"{provider.DisplayName} discovery failed"));
        }
    }

    public async Task<Result<VideoDiscoveryPage>> GetAggregatedPageAsync(
        IReadOnlyList<string> enabledProviderIds,
        VideoDiscoveryAggregateRequest request,
        CancellationToken ct = default)
    {
        if (!_settings.Current.VideoSettings.Metadata.OnlineConsentAccepted)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverOnlineConsentRequired",
                    "Enable online video metadata in Video settings before using Discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverConsentTitle",
                    "Online metadata permission required"));

        var enabled = new HashSet<string>(
            enabledProviderIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        var normalizedRequest = request with
        {
            Page = Math.Max(1, request.Page),
            // TMDB fixes its remote page at 20 items. Keeping the aggregate
            // window at or below that size prevents gaps when only one source
            // is enabled; the cumulative-prefix load below handles AniList's
            // different 24-item page size.
            PageSize = Math.Clamp(request.PageSize, 1, 20),
        };
        var providerJobs = AggregatedSearchProviderOrder
            .Where(providerId => enabled.Contains(providerId)
                                 && _providers.ContainsKey(providerId))
            .Select(providerId => CreateAggregateBrowseJob(providerId, normalizedRequest))
            .Where(job => job is not null)
            .Cast<AggregateBrowseJob>()
            .ToArray();
        if (providerJobs.Length == 0)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverBrowseNoSources",
                    "No enabled source is available for video discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverBrowseFailedTitle",
                    "Video discovery failed"));

        try
        {
            // Providers start together, but Task.WhenAll preserves the stable
            // AniList -> TMDB input order consumed by the round-robin below.
            var responses = await Task.WhenAll(providerJobs.Select(job =>
                LoadAggregateBrowseProviderWindowAsync(job, normalizedRequest.Page, ct)));
            ct.ThrowIfCancellationRequested();

            var failedProviders = responses
                .Where(response => response.FailedStreams > 0)
                .Select(response => _providers.TryGetValue(response.ProviderId, out var provider)
                    ? provider.DisplayName
                    : response.ProviderId)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (responses.All(response => response.SuccessfulStreams == 0))
                return Result<VideoDiscoveryPage>.Failure(
                    ResourceStringHelper.FormatString(
                        "DiscoverBrowseAllSourcesFailed",
                        "Every available discovery source failed ({0}).",
                        string.Join(", ", failedProviders)),
                    ResourceStringHelper.GetString(
                        "DiscoverBrowseFailedTitle",
                        "Video discovery failed"));

            // Match Fushi's pagination contract. Rebuilding each provider's
            // cumulative 1..N prefix is necessary because a page-N-only merge
            // would permanently drop the unconsumed tail of page N-1.
            var interleaved = RoundRobin(responses
                .Where(response => response.SuccessfulStreams > 0)
                .Select(response => (IReadOnlyList<VideoDiscoveryItem>)response.Items)
                .ToArray());
            var mergedWindow = MergeAggregatedItems(interleaved);
            var offset = (normalizedRequest.Page - 1) * normalizedRequest.PageSize;
            var pageEnd = offset + normalizedRequest.PageSize;
            var items = mergedWindow
                .Skip(offset)
                .Take(normalizedRequest.PageSize)
                .ToImmutableArray();
            var hasMore = responses.Any(response => response.HasMore)
                          || mergedWindow.Length > pageEnd;
            var warning = failedProviders.Length == 0
                ? null
                : ResourceStringHelper.FormatString(
                    "DiscoverBrowsePartialWarning",
                    "Some sources failed ({0}). Results from the other sources are still shown.",
                    string.Join(", ", failedProviders));

            return Result<VideoDiscoveryPage>.Success(new VideoDiscoveryPage(
                "aggregate",
                AggregateFeedId(normalizedRequest.Feed),
                normalizedRequest.Page,
                hasMore ? normalizedRequest.Page + 1 : normalizedRequest.Page,
                items,
                warning));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<VideoDiscoveryPage>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<VideoDiscoveryPage>.Failure(
                ex.Message,
                ResourceStringHelper.GetString(
                    "DiscoverBrowseFailedTitle",
                    "Video discovery failed"));
        }
    }

    public async Task<Result<IReadOnlyList<VideoDiscoveryPage>>> GetAggregatedRecommendationsAsync(
        IReadOnlyList<string> enabledProviderIds,
        CancellationToken ct = default)
    {
        var feeds = new[]
        {
            VideoDiscoveryAggregateFeed.Trending,
            VideoDiscoveryAggregateFeed.Seasonal,
            VideoDiscoveryAggregateFeed.Popular,
        };
        var results = await Task.WhenAll(feeds.Select(feed => GetAggregatedPageAsync(
            enabledProviderIds,
            new VideoDiscoveryAggregateRequest(Feed: feed),
            ct)));
        ct.ThrowIfCancellationRequested();

        var loadedPages = results
            .Where(result => result.IsSuccess && result.Value is not null)
            .Select(result => result.Value!)
            .ToArray();
        if (loadedPages.Length > 0)
        {
            // A title can trend on TMDB while appearing only in AniList's
            // seasonal or popular shelf. Resolve one canonical identity across
            // all three loaded windows, then project it back into each shelf so
            // every occurrence carries the same romaji/native title and xrefs.
            var canonicalItems = MergeAggregatedItems(
                loadedPages.SelectMany(page => page.Items));
            var pages = loadedPages.Select(page => page with
            {
                Items = page.Items.Select(item =>
                        canonicalItems.FirstOrDefault(canonical =>
                            CanMergeAggregatedItem([canonical], item))
                        ?? item)
                    .ToImmutableArray(),
            }).ToArray();
            return Result<IReadOnlyList<VideoDiscoveryPage>>.Success(pages);
        }
        if (results.Any(result => result.IsCancelled))
            return Result<IReadOnlyList<VideoDiscoveryPage>>.Cancelled();

        var errors = results
            .Select(result => result.Error)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
        return Result<IReadOnlyList<VideoDiscoveryPage>>.Failure(
            string.Join(Environment.NewLine, errors),
            ResourceStringHelper.GetString(
                "DiscoverRecommendationsFailedTitle",
                "Recommendations unavailable"));
    }

    private static AggregateBrowseJob? CreateAggregateBrowseJob(
        string providerId,
        VideoDiscoveryAggregateRequest request)
    {
        if (providerId.Equals("anilist", StringComparison.OrdinalIgnoreCase))
        {
            var feedId = request.Feed switch
            {
                VideoDiscoveryAggregateFeed.Trending => "trending",
                VideoDiscoveryAggregateFeed.Seasonal => "seasonal",
                _ => "popular",
            };
            return new AggregateBrowseJob(
                "anilist",
                [new VideoDiscoveryRequest(
                    feedId,
                    VideoMetadataMediaKind.Anime,
                    Year: request.Year,
                    GenreId: request.GenreId,
                    SortBy: request.SortBy,
                    Language: request.Language,
                    Region: request.Region)]);
        }

        if (!providerId.Equals("tmdb", StringComparison.OrdinalIgnoreCase))
            return null;
        if (request.Feed == VideoDiscoveryAggregateFeed.Seasonal)
            return null;

        if (request.Feed == VideoDiscoveryAggregateFeed.Trending)
        {
            return new AggregateBrowseJob(
                "tmdb",
                [
                    new VideoDiscoveryRequest(
                        "trending-movie",
                        VideoMetadataMediaKind.Movie,
                        TimeWindow: "week",
                        Language: request.Language,
                        Region: request.Region),
                    new VideoDiscoveryRequest(
                        "trending-tv",
                        VideoMetadataMediaKind.Series,
                        TimeWindow: "week",
                        Language: request.Language,
                        Region: request.Region),
                ]);
        }

        var useDiscoverFeeds = request.Year is not null
                               || !string.IsNullOrWhiteSpace(request.GenreId)
                               || !string.IsNullOrWhiteSpace(request.SortBy)
                               && !request.SortBy.Equals("popularity.desc", StringComparison.OrdinalIgnoreCase);
        var movieSort = NormalizeTmdbAggregateSort(request.SortBy, VideoMetadataMediaKind.Movie);
        var seriesSort = NormalizeTmdbAggregateSort(request.SortBy, VideoMetadataMediaKind.Series);
        return new AggregateBrowseJob(
            "tmdb",
            [
                new VideoDiscoveryRequest(
                    useDiscoverFeeds ? "discover-movie" : "popular-movie",
                    VideoMetadataMediaKind.Movie,
                    Year: request.Year,
                    GenreId: request.GenreId,
                    SortBy: movieSort,
                    Language: request.Language,
                    Region: request.Region),
                new VideoDiscoveryRequest(
                    useDiscoverFeeds ? "discover-tv" : "popular-tv",
                    VideoMetadataMediaKind.Series,
                    Year: request.Year,
                    GenreId: request.GenreId,
                    SortBy: seriesSort,
                    Language: request.Language,
                    Region: request.Region),
            ]);
    }

    private static string AggregateFeedId(VideoDiscoveryAggregateFeed feed) => feed switch
    {
        VideoDiscoveryAggregateFeed.Trending => "trending",
        VideoDiscoveryAggregateFeed.Seasonal => "seasonal",
        _ => "popular",
    };

    private static string NormalizeTmdbAggregateSort(
        string? sortBy,
        VideoMetadataMediaKind kind) => sortBy?.ToLowerInvariant() switch
    {
        "vote_average.desc" => "vote_average.desc",
        "release_date.desc" or "primary_release_date.desc" or "first_air_date.desc" =>
            kind == VideoMetadataMediaKind.Movie
                ? "primary_release_date.desc"
                : "first_air_date.desc",
        _ => "popularity.desc",
    };

    private async Task<AggregateBrowseResponse> LoadAggregateBrowseProviderWindowAsync(
        AggregateBrowseJob job,
        int requestedPage,
        CancellationToken ct)
    {
        var streams = await Task.WhenAll(job.Requests.Select(request =>
            LoadAggregateBrowseStreamWindowAsync(job.ProviderId, request, requestedPage, ct)));
        ct.ThrowIfCancellationRequested();
        return new AggregateBrowseResponse(
            job.ProviderId,
            streams.Count(stream => stream.SuccessfulPages > 0),
            streams.Count(stream => stream.FailedPages > 0),
            streams.Any(stream => stream.HasMore),
            RoundRobin(streams
                    .Where(stream => stream.SuccessfulPages > 0)
                    .Select(stream => (IReadOnlyList<VideoDiscoveryItem>)stream.Items)
                    .ToArray())
                .ToImmutableArray());
    }

    private async Task<AggregateBrowseStreamWindow> LoadAggregateBrowseStreamWindowAsync(
        string providerId,
        VideoDiscoveryRequest baseRequest,
        int requestedPage,
        CancellationToken ct)
    {
        var pages = new List<VideoDiscoveryPage>();
        var failedPages = 0;
        var hasMore = false;
        for (var page = 1; page <= requestedPage; page++)
        {
            var result = await GetPageAsync(providerId, baseRequest with { Page = page }, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.IsSuccess || result.Value is null)
            {
                failedPages++;
                break;
            }

            pages.Add(result.Value);
            hasMore = result.Value.HasMore;
            if (!hasMore)
                break;
        }

        return new AggregateBrowseStreamWindow(
            pages.Count,
            failedPages,
            hasMore,
            pages.SelectMany(page => page.Items).ToImmutableArray());
    }

    public async Task<Result<VideoDiscoveryPage>> SearchAsync(
        string providerId,
        string query,
        VideoMetadataMediaKind mediaKind,
        CancellationToken ct = default)
    {
        if (!_settings.Current.VideoSettings.Metadata.OnlineConsentAccepted)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverOnlineConsentRequired",
                    "Enable online video metadata in Video settings before using Discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverConsentTitle",
                    "Online metadata permission required"));
        if (!_searchProviders.TryGetValue(providerId, out var provider))
            return Result<VideoDiscoveryPage>.Failure("The metadata search provider is not available.", "Search unavailable");
        if (string.IsNullOrWhiteSpace(query))
            return Result<VideoDiscoveryPage>.Failure("Enter a title before searching.", "Video search");

        try
        {
            var normalizedQuery = query.Trim();
            var page = await GetCachedPageAsync(
                new PageCacheKey(
                    providerId,
                    new VideoDiscoveryRequest("search", mediaKind, Language: SearchLanguage, Region: SearchRegion),
                    normalizedQuery),
                async token =>
                {
                    var candidates = await provider.SearchAsync(new VideoMetadataSearchQuery(
                        normalizedQuery, mediaKind, null, null, null, null, SearchLanguage, SearchRegion,
                        ImmutableDictionary<string, string>.Empty), token);
                    var items = candidates
                        .Where(candidate => candidate is not null)
                        .Select(candidate => new VideoDiscoveryItem(
                            candidate,
                            null,
                            null,
                            null,
                            candidate.PosterUrl,
                            candidate.BackdropUrl))
                        .ToImmutableArray();
                    return await CachePageArtworkAsync(
                        new VideoDiscoveryPage(providerId, "search", 1, 1, items),
                        token);
                },
                ct);
            return Result<VideoDiscoveryPage>.Success(page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<VideoDiscoveryPage>.Cancelled();
        }
        catch (Exception ex)
        {
            var message = providerId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                && ex is HttpRequestException
                && ex.Message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
                ? ResourceStringHelper.GetString(
                    "DiscoverTmdbCredentialInvalid",
                    "TMDB authentication failed. Enter a TMDB v4 Read Token or a v3 API key, then refresh.")
                : ex.Message;
            return Result<VideoDiscoveryPage>.Failure(
                message,
                ResourceStringHelper.GetString(
                    "DiscoverSearchFailedTitle",
                    $"{provider.DisplayName} search failed"));
        }
    }

    public async Task<Result<VideoDiscoveryPage>> SearchAggregatedAsync(
        IReadOnlyList<string> enabledProviderIds,
        string query,
        VideoDiscoverySearchCategory category,
        CancellationToken ct = default)
    {
        if (!_settings.Current.VideoSettings.Metadata.OnlineConsentAccepted)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverOnlineConsentRequired",
                    "Enable online video metadata in Video settings before using Discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverConsentTitle",
                    "Online metadata permission required"));

        if (string.IsNullOrWhiteSpace(query))
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverSearchQueryRequired",
                    "Enter a title before searching."),
                ResourceStringHelper.GetString(
                    "DiscoverSearchFailedTitle",
                    "Video search failed"));
        var normalizedQuery = query.Trim();

        var enabled = new HashSet<string>(
            enabledProviderIds ?? [],
            StringComparer.OrdinalIgnoreCase);
        var providerJobs = AggregatedSearchProviderOrder
            .Where(providerId => enabled.Contains(providerId)
                                 && _searchProviders.ContainsKey(providerId))
            .Select(providerId => new AggregateSearchJob(
                providerId,
                AggregateSearchKinds(providerId, category)))
            .Where(job => !job.MediaKinds.IsDefaultOrEmpty)
            .ToArray();
        if (providerJobs.Length == 0)
            return Result<VideoDiscoveryPage>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverSearchNoSources",
                    "No enabled discovery source supports this search type."),
                ResourceStringHelper.GetString(
                    "DiscoverSearchFailedTitle",
                    "Video search failed"));

        try
        {
            // Preserve Fushi's provider priority and TMDB movie/series ordering:
            // Task.WhenAll starts every source concurrently but returns responses
            // in the stable input order used by the round-robin below.
            var responses = await Task.WhenAll(providerJobs.Select(job =>
                SearchAggregateProviderAsync(job, normalizedQuery, ct)));
            ct.ThrowIfCancellationRequested();

            var failedProviders = responses
                .Where(response => response.FailedKinds > 0)
                .Select(response => _searchProviders.TryGetValue(response.ProviderId, out var provider)
                    ? provider.DisplayName
                    : response.ProviderId)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (responses.All(response => response.SuccessfulKinds == 0))
                return Result<VideoDiscoveryPage>.Failure(
                    ResourceStringHelper.FormatString(
                        "DiscoverSearchAllSourcesFailed",
                        "Every available discovery source failed to complete the search ({0}).",
                        string.Join(", ", failedProviders)),
                    ResourceStringHelper.GetString(
                        "DiscoverSearchFailedTitle",
                        "Video search failed"));

            var interleaved = RoundRobin(responses
                .Where(response => response.SuccessfulKinds > 0)
                .Select(response => (IReadOnlyList<VideoDiscoveryItem>)response.Items)
                .ToArray());
            var merged = MergeAggregatedItems(interleaved);
            var warning = failedProviders.Length == 0
                ? null
                : ResourceStringHelper.FormatString(
                    "DiscoverSearchPartialWarning",
                    "Some sources failed ({0}). Results from the other sources are still shown.",
                    string.Join(", ", failedProviders));

            return Result<VideoDiscoveryPage>.Success(new VideoDiscoveryPage(
                "aggregate",
                "search",
                1,
                1,
                merged,
                warning));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<VideoDiscoveryPage>.Cancelled();
        }
    }

    private async Task<AggregateSearchResponse> SearchAggregateProviderAsync(
        AggregateSearchJob job,
        string query,
        CancellationToken ct)
    {
        var results = await Task.WhenAll(job.MediaKinds.Select(mediaKind =>
            SearchAsync(job.ProviderId, query, mediaKind, ct)));
        ct.ThrowIfCancellationRequested();

        var successfulPages = results
            .Where(result => result.IsSuccess && result.Value is not null)
            .Select(result => result.Value!)
            .ToArray();
        return new AggregateSearchResponse(
            job.ProviderId,
            successfulPages.Length,
            results.Count(result => !result.IsSuccess),
            RoundRobin(successfulPages
                    .Select(page => (IReadOnlyList<VideoDiscoveryItem>)page.Items)
                    .ToArray())
                .ToImmutableArray());
    }

    private static ImmutableArray<VideoMetadataMediaKind> AggregateSearchKinds(
        string providerId,
        VideoDiscoverySearchCategory category) => providerId.ToLowerInvariant() switch
    {
        "anilist" when category is VideoDiscoverySearchCategory.All
            or VideoDiscoverySearchCategory.Anime => [VideoMetadataMediaKind.Anime],
        "tmdb" when category == VideoDiscoverySearchCategory.All =>
            [VideoMetadataMediaKind.Movie, VideoMetadataMediaKind.Series],
        "tmdb" when category == VideoDiscoverySearchCategory.Movie =>
            [VideoMetadataMediaKind.Movie],
        "tmdb" when category == VideoDiscoverySearchCategory.Series =>
            [VideoMetadataMediaKind.Series],
        _ => [],
    };

    public async Task<Result<VideoDiscoveryDetails>> GetDetailsAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default)
    {
        if (!_settings.Current.VideoSettings.Metadata.OnlineConsentAccepted)
            return Result<VideoDiscoveryDetails>.Failure(
                ResourceStringHelper.GetString(
                    "DiscoverOnlineConsentRequired",
                    "Enable online video metadata in Video settings before using Discovery."),
                ResourceStringHelper.GetString(
                    "DiscoverConsentTitle",
                    "Online metadata permission required"));
        if (!_detailsProviders.ContainsKey(identity.ProviderId))
            return Result<VideoDiscoveryDetails>.Failure("The metadata provider is not available.", "Details unavailable");

        try
        {
            var supplementalIdentity = CreateSupplementalDetailsIdentity(identity);
            var primaryTask = GetProviderDetailsAsync(identity, ct);
            var supplementalTask = supplementalIdentity is null
                ? Task.FromResult<VideoDiscoveryDetails?>(null)
                : GetSupplementalProviderDetailsAsync(supplementalIdentity, ct);
            await Task.WhenAll(primaryTask, supplementalTask);

            var primary = primaryTask.Result;
            if (primary is null)
            {
                return Result<VideoDiscoveryDetails>.Failure(
                    "No details were returned for this title.",
                    "Details unavailable");
            }

            var merged = MergeIdentityIntoDetails(primary, identity);
            if (supplementalTask.Result is { } supplemental)
                merged = MergeSupplementalDetails(merged, supplemental);
            return Result<VideoDiscoveryDetails>.Success(merged);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<VideoDiscoveryDetails>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<VideoDiscoveryDetails>.Failure(ex.Message, "Loading video details failed");
        }
    }

    private async Task<VideoDiscoveryDetails?> GetProviderDetailsAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct)
    {
        if (!_detailsProviders.TryGetValue(identity.ProviderId, out var provider))
            return null;
        var providerIdentity = identity with
        {
            // Keep cached provider payloads independent from transient aggregate
            // cross-references. The current navigation identity is merged into the
            // returned value after every cache lookup, so a corrected mapping is
            // visible immediately without poisoning the provider-local cache.
            ExternalIds = ImmutableDictionary.Create<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                .Add(identity.ProviderId, identity.ProviderItemId),
        };

        return await GetCachedDetailsAsync(
            new DetailsCacheKey(
                identity.ProviderId,
                identity.ProviderItemId,
                identity.MediaKind,
                "ja-JP",
                "JP"),
            async token =>
            {
                // Fetch the provider text and artwork together. Each provider keeps
                // its own cache entry so an optional Fushi-style detail supplement
                // cannot replace the navigation identity owned by the primary source.
                var detailsTask = provider.GetDetailsAsync(providerIdentity, "ja-JP", "JP", token);
                var artworkTask = GetArtworkAsync(providerIdentity, token);
                await Task.WhenAll(detailsTask, artworkTask);

                var details = detailsTask.Result;
                if (details is null)
                    return null;

                details = details.WithInitializedCollections();
                var artwork = artworkTask.Result;
                var mainArtworkUrls = new[]
                {
                    artwork.FirstOrDefault(item => item.Kind.Equals("poster", StringComparison.OrdinalIgnoreCase))?.Url,
                    artwork.FirstOrDefault(item => item.Kind.Equals("backdrop", StringComparison.OrdinalIgnoreCase))?.Url,
                    artwork.FirstOrDefault(item => item.Kind.Equals("logo", StringComparison.OrdinalIgnoreCase))?.Url,
                };
                var mainArtworkPaths = await Task.WhenAll(
                    mainArtworkUrls.Select(url => CacheArtworkAsync(url, token)));
                var mainArtwork = new VideoDiscoveryArtwork(
                    mainArtworkPaths[0], mainArtworkPaths[1], mainArtworkPaths[2]);
                details = await CacheSecondaryArtworkAsync(details, token);
                var seasons = await CacheSeasonArtworkAsync(details.Seasons, token);
                return new VideoDiscoveryDetails(details, mainArtwork, seasons);
            },
            ct);
    }

    private async Task<VideoDiscoveryDetails?> GetSupplementalProviderDetailsAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct)
    {
        try
        {
            return await GetProviderDetailsAsync(identity, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private VideoMetadataCandidate? CreateSupplementalDetailsIdentity(
        VideoMetadataCandidate identity)
    {
        if (identity.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
            && _settings.Current.VideoSettings.Metadata.TmdbEnabled
            && TryGetExternalId(identity.ExternalIds, "tmdb", out var tmdbId)
            && _detailsProviders.ContainsKey("tmdb"))
        {
            return identity with
            {
                ProviderId = "tmdb",
                ProviderItemId = tmdbId,
                MediaKind = identity.MediaKind == VideoMetadataMediaKind.Movie
                    ? VideoMetadataMediaKind.Movie
                    : VideoMetadataMediaKind.Series,
                SourceUrl = null,
            };
        }

        if (identity.ProviderId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
            && _settings.Current.VideoSettings.Metadata.AniListEnabled
            && TryGetExternalId(identity.ExternalIds, "anilist", out var aniListId)
            && _detailsProviders.ContainsKey("anilist"))
        {
            return identity with
            {
                ProviderId = "anilist",
                ProviderItemId = aniListId,
                MediaKind = VideoMetadataMediaKind.Anime,
                SourceUrl = null,
            };
        }

        return null;
    }

    private static bool TryGetExternalId(
        IReadOnlyDictionary<string, string>? externalIds,
        string key,
        out string id)
    {
        id = externalIds?
            .FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Value?
            .Trim() ?? string.Empty;
        return id.Length > 0;
    }

    public async Task<Result<VideoDiscoveryDetails>> GetDetailsByTitleAsync(
        IReadOnlyList<string> titles,
        VideoMetadataMediaKind mediaKind,
        int? year = null,
        CancellationToken ct = default)
    {
        var searchTitles = titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (searchTitles.Length == 0)
            return Result<VideoDiscoveryDetails>.Failure("No title is available for metadata search.", "Details unavailable");

        var providerOrder = mediaKind == VideoMetadataMediaKind.Anime
            ? AnimeLibrarySearchProviderOrder
            : GeneralLibrarySearchProviderOrder;
        VideoDiscoveryDetails? fallbackDetails = null;
        VideoDiscoveryDetails? richestSeasonDetails = null;
        foreach (var providerId in providerOrder)
        {
            if (!_searchProviders.TryGetValue(providerId, out var provider)
                || !_detailsProviders.ContainsKey(providerId))
            {
                continue;
            }

            foreach (var title in searchTitles)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<VideoMetadataCandidate> candidates;
                try
                {
                    candidates = await provider.SearchAsync(new VideoMetadataSearchQuery(
                        title,
                        mediaKind,
                        year,
                        null,
                        null,
                        null,
                        SearchLanguage,
                        SearchRegion,
                        ImmutableDictionary<string, string>.Empty),
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                var orderedCandidates = candidates
                    .Select(candidate => (Candidate: candidate, Score: ScoreLibraryCandidate(candidate, searchTitles, year)))
                    .Where(item => item.Score > 0)
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => ProviderPriority(item.Candidate.ProviderId, providerOrder))
                    .Select(item => item.Candidate)
                    .DistinctBy(candidate => (candidate.ProviderId, candidate.ProviderItemId));
                foreach (var candidate in orderedCandidates)
                {
                    var result = await GetDetailsAsync(candidate, ct);
                    if (!result.IsSuccess || result.Value is null)
                        continue;

                    if (mediaKind == VideoMetadataMediaKind.Movie)
                    {
                        return result;
                    }

                    // Different providers often expose a season as a separate
                    // identity. Keep searching and prefer the response with the
                    // largest scraped season/episode inventory instead of letting
                    // the first valid (but partial) response hide other seasons.
                    if (!result.Value.Seasons.IsDefaultOrEmpty
                        && (richestSeasonDetails is null
                            || GetSeasonInventoryScore(result.Value)
                                > GetSeasonInventoryScore(richestSeasonDetails)))
                    {
                        richestSeasonDetails = result.Value;
                    }

                    fallbackDetails ??= result.Value;
                }
            }
        }

        return richestSeasonDetails is not null
            ? Result<VideoDiscoveryDetails>.Success(richestSeasonDetails)
            : fallbackDetails is not null
                ? Result<VideoDiscoveryDetails>.Success(fallbackDetails)
                : Result<VideoDiscoveryDetails>.Failure(
                    "No matching metadata was found for this series.",
                    "Details unavailable");
    }

    private static int GetSeasonInventoryScore(VideoDiscoveryDetails details) =>
        details.Seasons.IsDefaultOrEmpty
            ? 0
            : details.Seasons.Sum(season =>
                1_000
                + Math.Max(season.EpisodeCount, season.Episodes.IsDefaultOrEmpty
                    ? 0
                    : season.Episodes.Length));

    private static int ProviderPriority(string providerId, string[] providerOrder) =>
        Array.IndexOf(providerOrder, providerId.ToLowerInvariant()) switch
        {
            < 0 => providerOrder.Length,
            var value => value,
        };

    private static double ScoreLibraryCandidate(
        VideoMetadataCandidate candidate,
        IReadOnlyList<string> searchTitles,
        int? year)
    {
        var candidateTitles = new[] { candidate.Title, candidate.OriginalTitle }
            .Concat(candidate.Aliases.IsDefault ? [] : candidate.Aliases)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => new
            {
                Raw = title!,
                Normalized = NormalizeTitle(title!),
            })
            .Where(title => title.Normalized.Length > 0)
            .DistinctBy(title => title.Normalized, StringComparer.Ordinal)
            .ToArray();
        if (candidateTitles.Length == 0)
            return 0;

        var best = 0d;
        foreach (var searchTitle in searchTitles)
        {
            var normalizedSearch = NormalizeTitle(searchTitle);
            if (normalizedSearch.Length == 0)
                continue;
            foreach (var candidateTitle in candidateTitles)
            {
                var score = normalizedSearch.Equals(candidateTitle.Normalized, StringComparison.Ordinal)
                    ? 1d
                    : normalizedSearch.Contains(candidateTitle.Normalized, StringComparison.Ordinal)
                        || candidateTitle.Normalized.Contains(normalizedSearch, StringComparison.Ordinal)
                        ? 0.75d
                        : TokenOverlap(searchTitle, candidateTitle.Raw);
                best = Math.Max(best, score);
            }
        }

        if (year.HasValue && candidate.Year.HasValue)
            best += year.Value == candidate.Year.Value ? 0.1d : -0.1d;
        return best;
    }

    private static double TokenOverlap(string left, string right)
    {
        var leftTokens = SplitTitleTokens(left);
        var rightTokens = SplitTitleTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0;
        var common = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return 0.75d * common / Math.Max(leftTokens.Count, rightTokens.Count);
    }

    private static HashSet<string> SplitTitleTokens(string title)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < title.Length;)
        {
            while (index < title.Length && !char.IsLetterOrDigit(title[index]))
                index++;
            var start = index;
            while (index < title.Length && char.IsLetterOrDigit(title[index]))
                index++;
            if (index > start)
                tokens.Add(title[start..index]);
        }
        return tokens;
    }

    private static string NormalizeTitle(string title) =>
        new string(title.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static IReadOnlyList<T> RoundRobin<T>(IReadOnlyList<IReadOnlyList<T>> sources)
    {
        var result = new List<T>();
        for (var index = 0; sources.Any(source => index < source.Count); index++)
        {
            foreach (var source in sources)
            {
                if (index < source.Count)
                    result.Add(source[index]);
            }
        }
        return result;
    }

    private static ImmutableArray<VideoDiscoveryItem> MergeAggregatedItems(
        IEnumerable<VideoDiscoveryItem> items)
    {
        var groups = new List<List<VideoDiscoveryItem>>();
        foreach (var item in items)
        {
            var group = groups.FirstOrDefault(existing => CanMergeAggregatedItem(existing, item));
            if (group is null)
                groups.Add([item]);
            else
                group.Add(item);
        }

        return groups.Select(MergeAggregatedGroup).ToImmutableArray();
    }

    private static bool CanMergeAggregatedItem(
        IReadOnlyList<VideoDiscoveryItem> existingItems,
        VideoDiscoveryItem candidate)
    {
        var right = StrongIdentities(candidate.Identity);
        var lefts = existingItems
            .Select(item => StrongIdentities(item.Identity))
            .ToArray();

        // A conflicting value in any shared strong namespace vetoes the whole
        // group, so a transitive title match cannot bridge two distinct works.
        if (lefts.Any(left => HasStrongIdentityConflict(left, right)))
            return false;
        if (lefts.Any(left => left.Any(pair =>
                right.TryGetValue(pair.Key, out var value)
                && value.Equals(pair.Value, StringComparison.Ordinal))))
        {
            return true;
        }

        if (existingItems.Any(existing => AggregationKindsConflict(
                existing.Identity.MediaKind,
                candidate.Identity.MediaKind)))
        {
            return false;
        }

        var candidateTitles = NormalizedCandidateTitles(candidate.Identity);
        if (candidateTitles.Count == 0 || candidate.Identity.Year is not int candidateYear)
            return false;
        return existingItems.Any(existing =>
            existing.Identity.Year == candidateYear
            && NormalizedCandidateTitles(existing.Identity).Overlaps(candidateTitles));
    }

    private static Dictionary<string, string> StrongIdentities(VideoMetadataCandidate identity)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string rawNamespace, string? rawValue)
        {
            var value = rawValue?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return;
            var identityNamespace = rawNamespace.Trim().ToLowerInvariant();
            if (identityNamespace == "tmdb")
                identityNamespace = $"tmdb-{identity.MediaKind.ToString().ToLowerInvariant()}";
            result[identityNamespace] = value;
        }

        foreach (var pair in identity.ExternalIds ?? ImmutableDictionary<string, string>.Empty)
            Add(pair.Key, pair.Value);
        Add(
            $"{identity.ProviderId}-{identity.MediaKind.ToString().ToLowerInvariant()}",
            identity.ProviderItemId);
        return result;
    }

    private static bool HasStrongIdentityConflict(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) => left.Any(pair =>
        right.TryGetValue(pair.Key, out var value)
        && !value.Equals(pair.Value, StringComparison.Ordinal));

    private static bool AggregationKindsConflict(
        VideoMetadataMediaKind left,
        VideoMetadataMediaKind right)
    {
        // AniList search summaries do not expose a reliable movie-vs-series
        // aggregation kind. Match Fushi by treating Anime as unknown here; only
        // an explicit Movie/Series disagreement vetoes the weak title match.
        if (left == VideoMetadataMediaKind.Anime || right == VideoMetadataMediaKind.Anime)
            return false;
        return left != right;
    }

    private static HashSet<string> NormalizedCandidateTitles(VideoMetadataCandidate identity) =>
        new[] { identity.Title, identity.OriginalTitle }
            .Concat(identity.Aliases.IsDefault ? [] : identity.Aliases)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => NormalizeAggregatedTitle(title!))
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeAggregatedTitle(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var result = new StringBuilder(normalized.Length);
        var separatorPending = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0)
                    result.Append(' ');
                result.Append(character);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }
        return result.ToString();
    }

    private static VideoDiscoveryItem MergeAggregatedGroup(IReadOnlyList<VideoDiscoveryItem> group)
    {
        if (group.Count == 1)
            return group[0];

        var hasMovie = group.Any(item => item.Identity.MediaKind == VideoMetadataMediaKind.Movie);
        var hasAnime = group.Any(item =>
            item.Identity.MediaKind == VideoMetadataMediaKind.Anime
            || item.Identity.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase));
        var ranked = group
            .Select((item, index) => (Item: item, Index: index))
            .OrderBy(entry => hasMovie && entry.Item.Identity.MediaKind != VideoMetadataMediaKind.Movie ? 1 : 0)
            .ThenBy(entry => AggregatedPrimaryRank(entry.Item.Identity.ProviderId, hasAnime))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();
        var primary = ranked[0];

        var externalIds = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in ranked)
        {
            foreach (var pair in item.Identity.ExternalIds ?? ImmutableDictionary<string, string>.Empty)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    externalIds.TryAdd(pair.Key.Trim(), pair.Value.Trim());
            }
            if (!string.IsNullOrWhiteSpace(item.Identity.ProviderId)
                && !string.IsNullOrWhiteSpace(item.Identity.ProviderItemId))
            {
                externalIds.TryAdd(item.Identity.ProviderId, item.Identity.ProviderItemId);
            }
            AddAlias(aliases, item.Identity.Title);
            AddAlias(aliases, item.Identity.OriginalTitle);
            if (!item.Identity.Aliases.IsDefault)
            {
                foreach (var alias in item.Identity.Aliases)
                    AddAlias(aliases, alias);
            }
        }

        var posterUrl = FirstNonEmpty(ranked.Select(item => item.Identity.PosterUrl))
                        ?? FirstNonEmpty(ranked.Select(item => item.PosterUrl));
        var backdropUrl = FirstNonEmpty(ranked.Select(item => item.Identity.BackdropUrl))
                          ?? FirstNonEmpty(ranked.Select(item => item.BackdropUrl));
        var aniListTitleItem = hasAnime
            ? ranked.FirstOrDefault(item =>
                item.Identity.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase))
            : null;
        var displayTitle = aniListTitleItem?.Identity.Title ?? primary.Identity.Title;
        var originalTitle = aniListTitleItem?.Identity.OriginalTitle
                            ?? primary.Identity.OriginalTitle
                            ?? FirstNonEmpty(ranked.Select(item => item.Identity.OriginalTitle));
        var identity = primary.Identity with
        {
            Title = displayTitle,
            OriginalTitle = originalTitle,
            Year = primary.Identity.Year
                   ?? ranked.Select(item => item.Identity.Year).FirstOrDefault(year => year.HasValue),
            Aliases = aliases
                .Where(alias => !alias.Equals(displayTitle, StringComparison.CurrentCultureIgnoreCase))
                .ToImmutableArray(),
            ExternalIds = externalIds.ToImmutable(),
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
        };
        return primary with
        {
            Identity = identity,
            Overview = primary.Overview ?? FirstNonEmpty(ranked.Select(item => item.Overview)),
            CommunityRating = primary.CommunityRating
                              ?? ranked.Select(item => item.CommunityRating).FirstOrDefault(value => value.HasValue),
            VoteCount = primary.VoteCount
                        ?? ranked.Select(item => item.VoteCount).FirstOrDefault(value => value.HasValue),
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            LocalPosterPath = primary.LocalPosterPath
                              ?? FirstNonEmpty(ranked.Select(item => item.LocalPosterPath)),
            LocalBackdropPath = primary.LocalBackdropPath
                                ?? FirstNonEmpty(ranked.Select(item => item.LocalBackdropPath)),
        };
    }

    private static int AggregatedPrimaryRank(string providerId, bool anime) =>
        providerId.ToLowerInvariant() switch
        {
            "anilist" when anime => 0,
            "tmdb" when !anime => 0,
            "tmdb" => 2,
            "anilist" => 2,
            _ => 10,
        };

    private static void AddAlias(ISet<string> aliases, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            aliases.Add(value.Trim());
    }

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static VideoDiscoveryDetails MergeIdentityIntoDetails(
        VideoDiscoveryDetails details,
        VideoMetadataCandidate identity)
    {
        var metadata = details.Metadata.WithInitializedCollections();
        var externalIds = metadata.ExternalIds.ToBuilder();
        foreach (var pair in identity.ExternalIds ?? ImmutableDictionary<string, string>.Empty)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            var key = pair.Key.Trim();
            if (!externalIds.ContainsKey(key))
                externalIds[key] = pair.Value.Trim();
        }
        if (!string.IsNullOrWhiteSpace(identity.ProviderId)
            && !string.IsNullOrWhiteSpace(identity.ProviderItemId)
            && !externalIds.ContainsKey(identity.ProviderId))
        {
            externalIds[identity.ProviderId] = identity.ProviderItemId;
        }

        var aliases = new HashSet<string>(
            metadata.Aliases,
            StringComparer.CurrentCultureIgnoreCase);
        AddAlias(aliases, identity.Title);
        AddAlias(aliases, identity.OriginalTitle);
        if (!identity.Aliases.IsDefault)
        {
            foreach (var alias in identity.Aliases)
                AddAlias(aliases, alias);
        }

        var keepCanonicalAnimeTitle = identity.ProviderId.Equals(
            "tmdb",
            StringComparison.OrdinalIgnoreCase)
            && TryGetExternalId(identity.ExternalIds, "anilist", out _);
        var displayTitle = keepCanonicalAnimeTitle
            ? PreferText(identity.Title, metadata.Title) ?? string.Empty
            : metadata.Title;
        var originalTitle = keepCanonicalAnimeTitle
            ? PreferText(identity.OriginalTitle, metadata.OriginalTitle)
            : metadata.OriginalTitle ?? identity.OriginalTitle;
        AddAlias(aliases, metadata.Title);
        AddAlias(aliases, metadata.OriginalTitle);

        return details with
        {
            Metadata = metadata with
            {
                Title = displayTitle,
                OriginalTitle = originalTitle,
                Year = metadata.Year ?? identity.Year,
                Aliases = aliases
                    .Where(alias => !alias.Equals(displayTitle, StringComparison.CurrentCultureIgnoreCase))
                    .ToImmutableArray(),
                ExternalIds = externalIds.ToImmutable(),
                SourceUrl = metadata.SourceUrl ?? identity.SourceUrl,
            },
        };
    }

    private static VideoDiscoveryDetails MergeSupplementalDetails(
        VideoDiscoveryDetails primary,
        VideoDiscoveryDetails supplemental)
    {
        var primaryMetadata = primary.Metadata.WithInitializedCollections();
        var supplementalMetadata = supplemental.Metadata.WithInitializedCollections();
        var externalIds = primaryMetadata.ExternalIds.ToBuilder();
        foreach (var pair in supplementalMetadata.ExternalIds)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key)
                && !string.IsNullOrWhiteSpace(pair.Value)
                && !externalIds.ContainsKey(pair.Key.Trim()))
            {
                externalIds[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        var aniListMetadata = primaryMetadata.ProviderId.Equals(
            "anilist",
            StringComparison.OrdinalIgnoreCase)
            ? primaryMetadata
            : supplementalMetadata.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
                ? supplementalMetadata
                : null;
        var fallbackTitle = PreferText(primaryMetadata.Title, supplementalMetadata.Title);
        var displayTitle = aniListMetadata is null
            ? fallbackTitle
            : PreferText(aniListMetadata.Title, fallbackTitle);
        var fallbackOriginalTitle = PreferText(
            primaryMetadata.OriginalTitle,
            supplementalMetadata.OriginalTitle);
        var originalTitle = aniListMetadata is null
            ? fallbackOriginalTitle
            : PreferText(aniListMetadata.OriginalTitle, fallbackOriginalTitle);
        var aliases = new HashSet<string>(
            MergeTextValues(primaryMetadata.Aliases, supplementalMetadata.Aliases),
            StringComparer.CurrentCultureIgnoreCase);
        AddAlias(aliases, primaryMetadata.Title);
        AddAlias(aliases, primaryMetadata.OriginalTitle);
        AddAlias(aliases, supplementalMetadata.Title);
        AddAlias(aliases, supplementalMetadata.OriginalTitle);

        var metadata = primaryMetadata with
        {
            Title = displayTitle ?? string.Empty,
            OriginalTitle = originalTitle,
            Subtitle = PreferText(primaryMetadata.Subtitle, supplementalMetadata.Subtitle),
            Overview = PreferText(primaryMetadata.Overview, supplementalMetadata.Overview),
            Year = primaryMetadata.Year ?? supplementalMetadata.Year,
            SeasonNumber = primaryMetadata.SeasonNumber ?? supplementalMetadata.SeasonNumber,
            EpisodeNumber = primaryMetadata.EpisodeNumber ?? supplementalMetadata.EpisodeNumber,
            AbsoluteEpisodeNumber = primaryMetadata.AbsoluteEpisodeNumber
                                    ?? supplementalMetadata.AbsoluteEpisodeNumber,
            Aliases = aliases
                .Where(alias => !alias.Equals(
                    displayTitle,
                    StringComparison.CurrentCultureIgnoreCase))
                .ToImmutableArray(),
            Genres = MergeTextValues(primaryMetadata.Genres, supplementalMetadata.Genres),
            Actors = MergeTextValues(primaryMetadata.Actors, supplementalMetadata.Actors),
            ExternalIds = externalIds.ToImmutable(),
            SourceUrl = PreferText(primaryMetadata.SourceUrl, supplementalMetadata.SourceUrl),
            Tagline = PreferText(primaryMetadata.Tagline, supplementalMetadata.Tagline),
            OfficialRating = PreferText(
                primaryMetadata.OfficialRating,
                supplementalMetadata.OfficialRating),
            CommunityRating = primaryMetadata.CommunityRating
                              ?? supplementalMetadata.CommunityRating,
            EndYear = primaryMetadata.EndYear ?? supplementalMetadata.EndYear,
            Status = PreferText(primaryMetadata.Status, supplementalMetadata.Status),
            Tags = MergeTextValues(primaryMetadata.Tags, supplementalMetadata.Tags),
            Studios = MergeTextValues(primaryMetadata.Studios, supplementalMetadata.Studios),
            People = MergePeople(primaryMetadata.People, supplementalMetadata.People),
            RelatedItems = MergeRelatedItems(
                primaryMetadata.RelatedItems,
                supplementalMetadata.RelatedItems),
            Seasons = primaryMetadata.Seasons.IsDefaultOrEmpty
                ? supplementalMetadata.Seasons
                : primaryMetadata.Seasons,
            TmdbOrdering = primaryMetadata.TmdbOrdering ?? supplementalMetadata.TmdbOrdering,
        };

        return primary with
        {
            Metadata = metadata,
            Artwork = new VideoDiscoveryArtwork(
                primary.Artwork.PosterPath ?? supplemental.Artwork.PosterPath,
                primary.Artwork.BackdropPath ?? supplemental.Artwork.BackdropPath,
                primary.Artwork.LogoPath ?? supplemental.Artwork.LogoPath),
            Seasons = primary.Seasons.IsDefaultOrEmpty
                ? supplemental.Seasons
                : primary.Seasons,
        };
    }

    private static string? PreferText(string? primary, string? supplemental) =>
        string.IsNullOrWhiteSpace(primary) ? supplemental : primary;

    private static ImmutableArray<string> MergeTextValues(
        ImmutableArray<string> primary,
        ImmutableArray<string> supplemental) =>
        (primary.IsDefault ? [] : primary)
        .Concat(supplemental.IsDefault ? [] : supplemental)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToImmutableArray();

    private static ImmutableArray<VideoPersonCredit> MergePeople(
        ImmutableArray<VideoPersonCredit> primary,
        ImmutableArray<VideoPersonCredit> supplemental)
    {
        var merged = (primary.IsDefault ? [] : primary).ToList();
        foreach (var person in supplemental.IsDefault ? [] : supplemental)
        {
            var index = merged.FindIndex(existing =>
                existing.Name.Equals(person.Name, StringComparison.CurrentCultureIgnoreCase)
                && string.Equals(existing.Role, person.Role, StringComparison.CurrentCultureIgnoreCase)
                && existing.Type.Equals(person.Type, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(person);
                continue;
            }

            var existing = merged[index];
            merged[index] = existing with
            {
                ImageUrl = existing.ImageUrl ?? person.ImageUrl,
                LocalImagePath = existing.LocalImagePath ?? person.LocalImagePath,
            };
        }
        return merged.ToImmutableArray();
    }

    private static ImmutableArray<VideoRelatedItem> MergeRelatedItems(
        ImmutableArray<VideoRelatedItem> primary,
        ImmutableArray<VideoRelatedItem> supplemental)
    {
        var merged = (primary.IsDefault ? [] : primary).ToList();
        foreach (var item in supplemental.IsDefault ? [] : supplemental)
        {
            var index = merged.FindIndex(existing =>
                existing.ProviderId.Equals(item.ProviderId, StringComparison.OrdinalIgnoreCase)
                && existing.ProviderItemId.Equals(
                    item.ProviderItemId,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(item);
                continue;
            }

            var existing = merged[index];
            merged[index] = existing with
            {
                OriginalTitle = PreferText(existing.OriginalTitle, item.OriginalTitle),
                Year = existing.Year ?? item.Year,
                PosterUrl = existing.PosterUrl ?? item.PosterUrl,
                BackdropUrl = existing.BackdropUrl ?? item.BackdropUrl,
                SourceUrl = PreferText(existing.SourceUrl, item.SourceUrl),
                LocalPosterPath = existing.LocalPosterPath ?? item.LocalPosterPath,
                LocalBackdropPath = existing.LocalBackdropPath ?? item.LocalBackdropPath,
                Aliases = MergeTextValues(existing.Aliases, item.Aliases),
            };
        }
        return merged.ToImmutableArray();
    }

    private async Task<VideoDiscoveryPage> GetCachedPageAsync(
        PageCacheKey key,
        Func<CancellationToken, Task<VideoDiscoveryPage>> loader,
        CancellationToken ct)
    {
        if (TryGetPage(key, out var cached))
            return cached;

        var gate = _pageCacheLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetPage(key, out cached))
                return cached;

            var page = await loader(ct);
            _pageCache[key] = new PageCacheEntry(
                page,
                DateTimeOffset.UtcNow,
                Volatile.Read(ref _cacheGeneration));
            return page;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VideoDiscoveryDetails?> GetCachedDetailsAsync(
        DetailsCacheKey key,
        Func<CancellationToken, Task<VideoDiscoveryDetails?>> loader,
        CancellationToken ct)
    {
        if (TryGetDetails(key, out var cached))
            return cached;

        var gate = _detailsCacheLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetDetails(key, out cached))
                return cached;

            var details = await loader(ct);
            if (details is not null)
                _detailsCache[key] = new DetailsCacheEntry(
                    details,
                    DateTimeOffset.UtcNow,
                    Volatile.Read(ref _cacheGeneration));
            return details;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetPage(PageCacheKey key, out VideoDiscoveryPage page)
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

    private bool TryGetDetails(DetailsCacheKey key, out VideoDiscoveryDetails details)
    {
        if (_detailsCache.TryGetValue(key, out var entry))
        {
            if (entry.Generation == Volatile.Read(ref _cacheGeneration)
                && DateTimeOffset.UtcNow - entry.CreatedAt <= DetailsCacheLifetime)
            {
                details = entry.Details;
                return true;
            }

            _detailsCache.TryRemove(key, out _);
        }

        details = default!;
        return false;
    }

    private async Task<VideoDiscoveryPage> CachePageArtworkAsync(
        VideoDiscoveryPage page,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(6, 6);
        var tasks = page.Items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var artwork = await Task.WhenAll(
                    CacheArtworkAsync(item.PosterUrl, ct),
                    CacheArtworkAsync(item.BackdropUrl, ct));
                return item with { LocalPosterPath = artwork[0], LocalBackdropPath = artwork[1] };
            }
            finally
            {
                gate.Release();
            }
        });
        return page with { Items = (await Task.WhenAll(tasks)).ToImmutableArray() };
    }

    private async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct)
    {
        if (!_artworkProviders.TryGetValue(identity.ProviderId, out var provider))
            return [];
        try
        {
            return await provider.GetArtworkAsync(identity, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<VideoMetadataDetails> CacheSecondaryArtworkAsync(
        VideoMetadataDetails details,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(SecondaryArtworkConcurrency, SecondaryArtworkConcurrency);

        async Task<string?> CacheWithGateAsync(string? localPath, string? remoteUrl)
        {
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                return localPath;
            if (string.IsNullOrWhiteSpace(remoteUrl))
                return null;

            await gate.WaitAsync(ct);
            try
            {
                return await CacheArtworkAsync(remoteUrl, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var peopleTask = Task.WhenAll(details.People.Select(async person => person with
        {
            LocalImagePath = await CacheWithGateAsync(person.LocalImagePath, person.ImageUrl),
        }));
        var relatedTask = Task.WhenAll(details.RelatedItems.Select(async item => item with
        {
            LocalPosterPath = await CacheWithGateAsync(
                item.LocalPosterPath,
                item.PosterUrl),
            LocalBackdropPath = await CacheWithGateAsync(
                item.LocalBackdropPath,
                item.BackdropUrl),
        }));

        await Task.WhenAll(peopleTask, relatedTask);
        return details with
        {
            People = peopleTask.Result.ToImmutableArray(),
            RelatedItems = relatedTask.Result.ToImmutableArray(),
        };
    }

    private async Task<ImmutableArray<VideoDiscoverySeason>> CacheSeasonArtworkAsync(
        ImmutableArray<VideoMetadataSeason> seasons,
        CancellationToken ct)
    {
        if (seasons.IsDefaultOrEmpty)
            return [];

        using var gate = new SemaphoreSlim(SecondaryArtworkConcurrency, SecondaryArtworkConcurrency);
        var seasonTasks = seasons.Select(async season =>
        {
            async Task<string?> CacheWithGateAsync(string? url)
            {
                if (string.IsNullOrWhiteSpace(url))
                    return null;
                await gate.WaitAsync(ct);
                try
                {
                    return await CacheArtworkAsync(url, ct);
                }
                finally
                {
                    gate.Release();
                }
            }

            var localPosterTask = CacheWithGateAsync(season.PosterUrl);
            var episodeTasks = season.Episodes
                .Select(async episode => new VideoDiscoveryEpisode(
                    episode.EpisodeNumber,
                    episode.Title,
                    episode.OriginalTitle,
                    episode.Overview,
                    episode.AirDate,
                    episode.RuntimeMinutes,
                    episode.ThumbnailUrl,
                    episode.SourceUrl,
                    LocalThumbnailPath: await CacheWithGateAsync(episode.ThumbnailUrl),
                    DisplayNumber: episode.DisplayNumber))
                .ToArray();
            var localEpisodes = await Task.WhenAll(episodeTasks);
            var localPoster = await localPosterTask;
            return new VideoDiscoverySeason(
                season.SeasonNumber,
                season.Title,
                season.Overview,
                season.AirDate,
                season.EpisodeCount ?? Math.Max(season.Episodes.Length, localEpisodes.Length),
                season.PosterUrl,
                localEpisodes.ToImmutableArray(),
                localPoster);
        });

        return (await Task.WhenAll(seasonTasks))
            .OrderBy(season => season.SeasonNumber)
            .ToImmutableArray();
    }

    private async Task<string?> CacheArtworkAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        VideoArtworkCacheEntry? cached;
        try
        {
            cached = await _artworkCache.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cached = null;
        }
        if (cached is not null)
            return cached.LocalPath;

        var gate = _artworkLocks.GetOrAdd(url, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            try
            {
                cached = await _artworkCache.GetAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                cached = null;
            }
            if (cached is not null)
                return cached.LocalPath;

            try
            {
                var response = await _transport.SendAsync(new VideoMetadataRequest(
                    ArtworkProviderId(uri),
                    HttpMethod.Get,
                    uri,
                    MaxResponseBytes: 20L * 1024 * 1024), ct);
                if (response.StatusCode is < 200 or >= 300 || response.Content.Length == 0)
                    return null;
                await using var content = new MemoryStream(response.Content, writable: false);
                var entry = await _artworkCache.StoreAsync(
                    url,
                    content,
                    response.ContentType,
                    response.ETag,
                    response.LastModified,
                    ct);
                return entry.LocalPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<string?> ResolveArtworkAsync(
        string? url,
        CancellationToken ct = default) => CacheArtworkAsync(url, ct);

    private static string ArtworkProviderId(Uri uri) => uri.IdnHost.ToLowerInvariant() switch
    {
        "image.tmdb.org" => "tmdb",
        "s4.anilist.co" => "anilist",
        "static.tvmaze.com" => "tvmaze",
        "artworks.thetvdb.com" => "tvdb",
        _ => throw new HttpRequestException("Artwork host is not allowed."),
    };

    private sealed record PageCacheKey(
        string ProviderId,
        VideoDiscoveryRequest Request,
        string? SearchQuery);

    private sealed record PageCacheEntry(
        VideoDiscoveryPage Page,
        DateTimeOffset CreatedAt,
        int Generation);

    private sealed record DetailsCacheKey(
        string ProviderId,
        string ProviderItemId,
        VideoMetadataMediaKind MediaKind,
        string Language,
        string Region);

    private sealed record AggregateSearchJob(
        string ProviderId,
        ImmutableArray<VideoMetadataMediaKind> MediaKinds);

    private sealed record AggregateSearchResponse(
        string ProviderId,
        int SuccessfulKinds,
        int FailedKinds,
        ImmutableArray<VideoDiscoveryItem> Items);

    private sealed record AggregateBrowseJob(
        string ProviderId,
        ImmutableArray<VideoDiscoveryRequest> Requests);

    private sealed record AggregateBrowseResponse(
        string ProviderId,
        int SuccessfulStreams,
        int FailedStreams,
        bool HasMore,
        ImmutableArray<VideoDiscoveryItem> Items);

    private sealed record AggregateBrowseStreamWindow(
        int SuccessfulPages,
        int FailedPages,
        bool HasMore,
        ImmutableArray<VideoDiscoveryItem> Items);

    private sealed record DetailsCacheEntry(
        VideoDiscoveryDetails Details,
        DateTimeOffset CreatedAt,
        int Generation);
}
