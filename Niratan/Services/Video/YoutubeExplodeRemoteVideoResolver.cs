using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

internal sealed class YoutubeExplodeRemoteVideoResolver : IRemoteVideoResolver, IDisposable
{
    private readonly IYoutubeExplodeClientAdapter _client;
    private readonly ConcurrentDictionary<string, ResolvedRemoteVideoSource> _cache = new();
    private readonly TimeProvider _timeProvider;

    public YoutubeExplodeRemoteVideoResolver()
        : this(new YoutubeExplodeClientAdapter(), TimeProvider.System)
    {
    }

    internal YoutubeExplodeRemoteVideoResolver(IYoutubeExplodeClientAdapter client, TimeProvider timeProvider)
    {
        _client = client;
        _timeProvider = timeProvider;
    }

    public async Task<ResolvedRemoteVideoSource> ResolveAsync(
        string url,
        string? preferredSubtitleLanguage = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (!YouTubeUrlParser.TryParse(url, out var videoId, out var canonicalUrl, out var requestedStartPosition))
            throw new RemoteVideoResolverException(RemoteVideoResolverError.UnsupportedUrl);

        var cacheKey = $"remote://youtube/{videoId}";
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh
            && _cache.TryGetValue(cacheKey, out var cached)
            && !cached.IsExpired(now))
        {
            return ApplyRequestContext(
                cached,
                url,
                canonicalUrl,
                requestedStartPosition,
                preferredSubtitleLanguage);
        }

        try
        {
            var metadataTask = _client.GetMetadataAsync(videoId, ct);
            var streamsTask = _client.GetStreamsAsync(videoId, ct);
            var captionsTask = _client.GetSubtitlesAsync(videoId, ct);

            await Task.WhenAll(metadataTask, streamsTask, captionsTask);
            var metadata = await metadataTask;
            var descriptors = await streamsTask;
            var selection = YouTubeStreamSelector.Select(descriptors);
            var subtitles = FilterPublisherSubtitles(await captionsTask);

            var identity = new RemoteVideoIdentity(
                YouTubeUrlParser.ProviderId,
                videoId,
                url.Trim(),
                canonicalUrl,
                string.IsNullOrWhiteSpace(metadata.Title) ? "YouTube Video" : metadata.Title,
                metadata.ThumbnailUrl,
                metadata.Duration);
            var resolvedAt = _timeProvider.GetUtcNow();
            var source = new ResolvedRemoteVideoSource(
                identity,
                selection.Playback,
                selection.ExternalAudio,
                selection.MuxedFallback,
                selection.Mining,
                subtitles,
                null,
                resolvedAt,
                ResolveExpiry(descriptors, resolvedAt),
                selection.QualityOptions,
                requestedStartPosition);
            source = ApplyPreferredSubtitle(source, preferredSubtitleLanguage);
            var cacheEntry = source with
            {
                Identity = identity with { OriginalUrl = canonicalUrl },
                RequestedStartPosition = null,
            };
            _cache[identity.PersistenceKey] = cacheEntry;
            _cache[$"url:{canonicalUrl}"] = cacheEntry;
            return source;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new RemoteVideoResolverException(RemoteVideoResolverError.TimedOut, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new RemoteVideoResolverException(RemoteVideoResolverError.TimedOut, ex);
        }
        catch (RemoteVideoResolverException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RemoteVideoResolverException(RemoteVideoResolverError.ResolutionFailed, ex);
        }
    }

    public Task<ResolvedRemoteVideoSource> ResolveAsync(
        RemoteVideoIdentity identity,
        string? preferredSubtitleLanguage = null,
        bool forceRefresh = false,
        CancellationToken ct = default) =>
        ResolveAsync(
            string.IsNullOrWhiteSpace(identity.CanonicalUrl) ? identity.OriginalUrl : identity.CanonicalUrl,
            preferredSubtitleLanguage,
            forceRefresh,
            ct);

    public async Task<string> DownloadSubtitleAsync(
        RemoteVideoSubtitleOption option,
        string outputPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (option.IsAutomatic)
            throw new InvalidOperationException("Automatic YouTube captions are not supported.");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await _client.DownloadSubtitleAsync(option, outputPath, ct);
        return outputPath;
    }

    public void Dispose() => _client.Dispose();

    private static ResolvedRemoteVideoSource ApplyPreferredSubtitle(
        ResolvedRemoteVideoSource source,
        string? language)
    {
        var selected = source.PreferredSubtitle([language]);
        return string.Equals(
            source.SelectedSubtitleLanguage,
            selected?.Language,
            StringComparison.OrdinalIgnoreCase)
            ? source
            : source with { SelectedSubtitleLanguage = selected?.Language };
    }

    private static ResolvedRemoteVideoSource ApplyRequestContext(
        ResolvedRemoteVideoSource source,
        string originalUrl,
        string canonicalUrl,
        TimeSpan? requestedStartPosition,
        string? preferredSubtitleLanguage) =>
        ApplyPreferredSubtitle(
            source with
            {
                Identity = source.Identity with
                {
                    OriginalUrl = originalUrl.Trim(),
                    CanonicalUrl = canonicalUrl,
                },
                RequestedStartPosition = requestedStartPosition,
            },
            preferredSubtitleLanguage);

    internal static DateTimeOffset ResolveExpiry(
        IReadOnlyList<RemoteVideoStream> streams,
        DateTimeOffset resolvedAt)
    {
        var expiries = streams
            .Select(stream => ParseQueryValue(stream.Url, "expire"))
            .Where(value => long.TryParse(value, out _))
            .Select(value => DateTimeOffset.FromUnixTimeSeconds(long.Parse(value!)))
            .Where(value => value > resolvedAt)
            .ToList();
        return expiries.Count == 0
            ? resolvedAt.AddHours(4)
            : expiries.Min().AddMinutes(-5);
    }

    internal static IReadOnlyList<RemoteVideoSubtitleOption> FilterPublisherSubtitles(
        IEnumerable<RemoteVideoSubtitleOption> subtitles) =>
        subtitles.Where(option => !option.IsAutomatic).ToList();

    private static string? ParseQueryValue(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? item : item[..separator]);
            if (!name.Equals(key, StringComparison.Ordinal))
                continue;
            return Uri.UnescapeDataString(separator < 0 ? string.Empty : item[(separator + 1)..]);
        }

        return null;
    }

}
