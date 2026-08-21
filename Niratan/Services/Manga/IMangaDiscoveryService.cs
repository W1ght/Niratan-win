using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

public interface IMangaDiscoveryService
{
    void ClearCache();

    IReadOnlyList<MangaDiscoveryProvider> Providers { get; }

    IReadOnlyList<MangaDiscoveryFeed> GetFeeds(
        string providerId,
        MangaDiscoveryFeedKind kind);

    Task<MangaDiscoveryPage> GetPageAsync(
        string providerId,
        MangaDiscoveryRequest request,
        CancellationToken ct = default);

    Task<MangaDiscoveryPage> SearchAsync(
        string providerId,
        string query,
        int page = 1,
        CancellationToken ct = default);

    Task<string?> GetPosterPathAsync(
        MangaDiscoveryItem item,
        CancellationToken ct = default);
}

public interface IMangaDiscoveryBatchService
{
    Task<IReadOnlyList<MangaDiscoveryPage>> GetPagesAsync(
        string providerId,
        IReadOnlyList<MangaDiscoveryRequest> requests,
        CancellationToken ct = default);
}
