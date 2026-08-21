using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
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

    Task<Result<VideoDiscoveryPage>> SearchAsync(
        string providerId,
        string query,
        VideoMetadataMediaKind mediaKind,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryDetails>> GetDetailsAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default);

    Task<Result<VideoDiscoveryDetails>> GetDetailsByTitleAsync(
        IReadOnlyList<string> titles,
        VideoMetadataMediaKind mediaKind,
        int? year = null,
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
