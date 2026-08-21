using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private static readonly string[] LibrarySearchProviderOrder = [
        "tmdb",
        "anilist",
        "bangumi",
        "tvmaze",
        "anidb",
    ];
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
                            null,
                            null))
                        .ToImmutableArray();
                    return await CacheSearchArtworkAsync(
                        providerId,
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
        if (!_detailsProviders.TryGetValue(identity.ProviderId, out var provider))
            return Result<VideoDiscoveryDetails>.Failure("The metadata provider is not available.", "Details unavailable");

        try
        {
            var cached = await GetCachedDetailsAsync(
                new DetailsCacheKey(identity.ProviderId, identity.ProviderItemId, "ja-JP", "JP"),
                async token =>
                {
                    // The metadata response contains the detail text, cast and related
                    // identities. Fetch primary and bounded secondary artwork before
                    // projection so the detail view only receives local image paths.
                    var detailsTask = provider.GetDetailsAsync(identity, "ja-JP", "JP", token);
                    var artworkTask = GetArtworkAsync(identity, token);
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
            return cached is null
                ? Result<VideoDiscoveryDetails>.Failure("No details were returned for this title.", "Details unavailable")
                : Result<VideoDiscoveryDetails>.Success(cached);
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

        VideoDiscoveryDetails? fallbackDetails = null;
        VideoDiscoveryDetails? richestSeasonDetails = null;
        foreach (var providerId in LibrarySearchProviderOrder)
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
                    .ThenBy(item => ProviderPriority(item.Candidate.ProviderId))
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

    private static int ProviderPriority(string providerId) =>
        Array.IndexOf(LibrarySearchProviderOrder, providerId.ToLowerInvariant()) switch
        {
            < 0 => LibrarySearchProviderOrder.Length,
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

    private async Task<VideoDiscoveryPage> CacheSearchArtworkAsync(
        string providerId,
        VideoDiscoveryPage page,
        CancellationToken ct)
    {
        if (!_artworkProviders.ContainsKey(providerId))
            return page;

        using var gate = new SemaphoreSlim(SecondaryArtworkConcurrency, SecondaryArtworkConcurrency);
        var tasks = page.Items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var artwork = await GetArtworkAsync(item.Identity, ct);
                var posterUrl = artwork.FirstOrDefault(candidate =>
                    candidate.Kind.Equals("poster", StringComparison.OrdinalIgnoreCase))?.Url;
                var backdropUrl = artwork.FirstOrDefault(candidate =>
                    candidate.Kind.Equals("backdrop", StringComparison.OrdinalIgnoreCase))?.Url;
                return item with
                {
                    PosterUrl = item.PosterUrl ?? posterUrl,
                    BackdropUrl = item.BackdropUrl ?? backdropUrl,
                };
            }
            finally
            {
                gate.Release();
            }
        });

        var pageWithUrls = page with { Items = (await Task.WhenAll(tasks)).ToImmutableArray() };
        return await CachePageArtworkAsync(pageWithUrls, ct);
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
                    LocalThumbnailPath: await CacheWithGateAsync(episode.ThumbnailUrl)))
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

    private static string ArtworkProviderId(Uri uri) => uri.IdnHost.ToLowerInvariant() switch
    {
        "image.tmdb.org" => "tmdb",
        "s4.anilist.co" => "anilist",
        "lain.bgm.tv" => "bangumi",
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
        string Language,
        string Region);

    private sealed record DetailsCacheEntry(
        VideoDiscoveryDetails Details,
        DateTimeOffset CreatedAt,
        int Generation);
}
