using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public interface IVideoDiscoveryProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<VideoDiscoveryFeed> Feeds { get; }

    Task<VideoDiscoveryPage> GetPageAsync(
        VideoDiscoveryRequest request,
        CancellationToken ct = default);
}

public interface IVideoDiscoveryService
{
    void ClearCache();

    IReadOnlyList<VideoDiscoveryFeed> GetFeeds(string providerId, VideoDiscoveryFeedKind kind);

    Task<Result<VideoDiscoveryPage>> GetPageAsync(
        string providerId,
        VideoDiscoveryRequest request,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryPage>> GetAggregatedPageAsync(
        IReadOnlyList<string> enabledProviderIds,
        VideoDiscoveryAggregateRequest request,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<VideoDiscoveryPage>>> GetAggregatedRecommendationsAsync(
        IReadOnlyList<string> enabledProviderIds,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryPage>> SearchAsync(
        string providerId,
        string query,
        VideoMetadataMediaKind mediaKind,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryPage>> SearchAggregatedAsync(
        IReadOnlyList<string> enabledProviderIds,
        string query,
        VideoDiscoverySearchCategory category,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryDetails>> GetDetailsAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryDetails>> GetDetailsByTitleAsync(
        IReadOnlyList<string> titles,
        VideoMetadataMediaKind mediaKind,
        int? year = null,
        CancellationToken ct = default);

    Task<string?> ResolveArtworkAsync(
        string? url,
        CancellationToken ct = default);
}

public interface IVideoResourceSearchService
{
    string BuildDefaultQuery(VideoMetadataCandidate identity);
    string BuildSubtitleQuery(VideoMetadataCandidate identity);

    Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchAsync(
        VideoResourceSearchRequest request,
        CancellationToken ct = default);
}

public interface IJimakuSubtitleService
{
    Task<Result<IReadOnlyList<JimakuSubtitleItem>>> SearchAsync(
        VideoSubtitleSearchRequest request,
        CancellationToken ct = default);

    Task<Result<string>> DownloadAsync(
        JimakuSubtitleItem item,
        string destinationPath,
        CancellationToken ct = default);
}

public sealed record NyaaSubscriptionArtwork(
    string? PosterUrl = null,
    string? PosterPath = null);

public interface INyaaSubscriptionService
{
    event EventHandler? SubscriptionsChanged;

    IReadOnlyList<NyaaVideoSubscription> GetSubscriptions();

    bool IsSubscribed(VideoMetadataCandidate identity);

    Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        CancellationToken ct = default);

    Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        int? startAfterEpisode,
        CancellationToken ct = default);

    Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        int? startAfterEpisode,
        NyaaSubscriptionArtwork? artwork,
        CancellationToken ct = default);

    Task UnsubscribeAsync(VideoMetadataCandidate identity, CancellationToken ct = default);

    Task SetEnabledAsync(string key, bool enabled, CancellationToken ct = default);

    Task RemoveAsync(string key, CancellationToken ct = default);

    Task RefreshArtworkAsync(string key, CancellationToken ct = default);

    Task<Result<int>> CheckOneAsync(string key, CancellationToken ct = default);

    Task CheckAllAsync(CancellationToken ct = default);
}
