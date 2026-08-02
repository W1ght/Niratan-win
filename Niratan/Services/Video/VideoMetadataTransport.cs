using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Video;
using Niratan.Services.Storage;

namespace Niratan.Services.Video;

internal sealed class VideoMetadataTransport : IVideoMetadataTransport
{
    private sealed record Policy(
        int MaxConcurrency,
        TimeSpan MinimumInterval,
        IReadOnlySet<string> AllowedHosts);

    private sealed class State(Policy policy)
    {
        public Policy Policy { get; } = policy;
        public SemaphoreSlim Concurrency { get; } = new(policy.MaxConcurrency, policy.MaxConcurrency);
        public SemaphoreSlim IntervalGate { get; } = new(1, 1);
        public DateTimeOffset LastRequestAt { get; set; } = DateTimeOffset.MinValue;
    }

    private static readonly IReadOnlyDictionary<string, Policy> Policies =
        new Dictionary<string, Policy>(StringComparer.OrdinalIgnoreCase)
        {
            ["tmdb"] = new(4, TimeSpan.FromMilliseconds(100), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.themoviedb.org", "image.tmdb.org",
            }),
            ["tvmaze"] = new(1, TimeSpan.FromMilliseconds(500), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.tvmaze.com", "static.tvmaze.com",
            }),
            ["anilist"] = new(1, TimeSpan.FromSeconds(2), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "graphql.anilist.co", "s4.anilist.co",
            }),
            ["anidb"] = new(1, TimeSpan.FromSeconds(2.1), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.anidb.net", "anidb.net",
            }),
            ["bangumi"] = new(1, TimeSpan.FromSeconds(1), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.bgm.tv", "lain.bgm.tv",
            }),
            ["tvdb"] = new(1, TimeSpan.FromSeconds(1), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api4.thetvdb.com", "artworks.thetvdb.com",
            }),
        };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VideoMetadataTransport> _logger;
    private readonly IVideoCatalogRepository? _cache;
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new(StringComparer.Ordinal);

    public VideoMetadataTransport(
        HttpClient httpClient,
        ILogger<VideoMetadataTransport> logger)
        : this(httpClient, TimeProvider.System, logger, null)
    {
    }

    public VideoMetadataTransport(
        HttpClient httpClient,
        ILogger<VideoMetadataTransport> logger,
        IVideoCatalogRepository cache)
        : this(httpClient, TimeProvider.System, logger, cache)
    {
    }

    internal VideoMetadataTransport(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<VideoMetadataTransport> logger)
        : this(httpClient, timeProvider, logger, null)
    {
    }

    internal VideoMetadataTransport(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<VideoMetadataTransport> logger,
        IVideoCatalogRepository? cache)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
        _cache = cache;
    }

    public async Task<VideoMetadataResponse> SendAsync(
        VideoMetadataRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var cacheKey = request.IsIdempotent ? CreateCacheKey(request) : null;
        if (cacheKey == null)
            return await SendCoreAsync(request, null, ct);
        var requestGate = _requestGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await requestGate.WaitAsync(ct);
        try
        {
            // Re-read the catalog cache after acquiring the keyed gate. A preceding
            // request for the same provider query may have populated it while we waited.
            return await SendCoreAsync(request, cacheKey, ct);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task<VideoMetadataResponse> SendCoreAsync(
        VideoMetadataRequest request,
        string? cacheKey,
        CancellationToken ct)
    {
        VideoProviderCacheEntry? cached = null;
        if (cacheKey != null && _cache != null)
        {
            try
            {
                cached = await _cache.GetProviderCacheAsync(cacheKey, ct);
                if (cached != null && cached.ExpiresAt > _timeProvider.GetUtcNow())
                    return FromCache(cached);
                if (cached != null)
                    request = request with { ETag = cached.ETag, LastModified = cached.LastModified };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Metadata cache read failed for {ProviderId}", request.ProviderId);
            }
        }
        var policy = Policies[request.ProviderId];
        var state = _states.GetOrAdd(request.ProviderId, _ => new State(policy));
        await state.Concurrency.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await WaitForIntervalAsync(state, ct);
                using var message = CreateRequest(request);
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);
                }
                catch (Exception ex) when (
                    request.IsIdempotent
                    && attempt < 2
                    && ex is HttpRequestException or IOException or TaskCanceledException
                    && !ct.IsCancellationRequested)
                {
                    await DelayForRetryAsync(attempt, null, ct);
                    continue;
                }
                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        return await ReadResponseAsync(response, request.MaxResponseBytes, ct);
                    if ((response.StatusCode == HttpStatusCode.TooManyRequests
                         || (int)response.StatusCode >= 500)
                        && request.IsIdempotent
                        && attempt < 2)
                    {
                        var retryAfter = ResolveRetryAfter(response.Headers.RetryAfter);
                        _logger.LogWarning(
                            "Metadata provider {ProviderId} requested a retry with status {StatusCode}",
                            request.ProviderId,
                            (int)response.StatusCode);
                        await DelayForRetryAsync(attempt, retryAfter, ct);
                        continue;
                    }
                    var result = await ReadResponseAsync(response, request.MaxResponseBytes, ct);
                    if (result.IsNotModified && cached != null)
                        result = FromCache(cached);
                    if (cacheKey != null && _cache != null && result.StatusCode is >= 200 and < 300)
                        await TryStoreCacheAsync(cacheKey, request.ProviderId, result, ct);
                    return result;
                }
            }
        }
        finally
        {
            state.Concurrency.Release();
        }
    }

    private async Task TryStoreCacheAsync(
        string cacheKey,
        string providerId,
        VideoMetadataResponse response,
        CancellationToken ct)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _cache!.UpsertProviderCacheAsync(new VideoProviderCacheEntry(
                cacheKey, providerId, response.ETag, response.LastModified,
                response.Content, response.ContentType, now, now.AddDays(30)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata cache write failed for {ProviderId}", providerId);
        }
    }

    private static VideoMetadataResponse FromCache(VideoProviderCacheEntry cached) => new(
        200, cached.Payload, cached.ContentType, cached.ETag, cached.LastModified,
        cached.FetchedAt, false);

    private static string CreateCacheKey(VideoMetadataRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.ProviderId}\n{request.Method.Method}\n{request.Uri.AbsoluteUri}\n"));
        if (request.Body != null)
            hash.AppendData(request.Body);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task WaitForIntervalAsync(State state, CancellationToken ct)
    {
        await state.IntervalGate.WaitAsync(ct);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var remaining = state.Policy.MinimumInterval - (now - state.LastRequestAt);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, _timeProvider, ct);
            state.LastRequestAt = _timeProvider.GetUtcNow();
        }
        finally
        {
            state.IntervalGate.Release();
        }
    }

    private async Task DelayForRetryAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(25, 125));
        await Task.Delay(delay + jitter, _timeProvider, ct);
    }

    private static HttpRequestMessage CreateRequest(VideoMetadataRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.Uri);
        if (request.Body != null)
        {
            message.Content = new ByteArrayContent(request.Body);
            if (!string.IsNullOrWhiteSpace(request.ContentType))
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        if (request.Headers != null)
        {
            foreach (var pair in request.Headers)
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
        if (!string.IsNullOrWhiteSpace(request.ETag))
            message.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(request.ETag));
        if (request.LastModified.HasValue)
            message.Headers.IfModifiedSince = request.LastModified;
        return message;
    }

    private static async Task<VideoMetadataResponse> ReadResponseAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new VideoMetadataResponse(
                (int)response.StatusCode,
                [],
                response.Content.Headers.ContentType?.MediaType,
                response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified,
                DateTimeOffset.UtcNow,
                true);
        }
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Metadata response exceeds its configured size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("Metadata response exceeds its configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new VideoMetadataResponse(
            (int)response.StatusCode,
            output.ToArray(),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified,
            DateTimeOffset.UtcNow,
            false);
    }

    private static TimeSpan? ResolveRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta)
            return delta;
        if (value?.Date is { } date)
            return date - DateTimeOffset.UtcNow is { } remaining && remaining > TimeSpan.Zero
                ? remaining
                : TimeSpan.Zero;
        return null;
    }

    private static void ValidateRequest(VideoMetadataRequest request)
    {
        if (!Policies.TryGetValue(request.ProviderId, out var policy))
            throw new ArgumentException("Unknown metadata provider.", nameof(request));
        if (request.Uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Metadata provider requests must use HTTPS.");
        if (!policy.AllowedHosts.Contains(request.Uri.IdnHost))
            throw new InvalidOperationException("Metadata provider request host is not allowlisted.");
        if (request.MaxResponseBytes is <= 0 or > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid metadata response size limit.");
    }
}
