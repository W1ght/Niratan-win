using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Video;

namespace Niratan.Services.Storage;

public interface IVideoCatalogRepository
{
    Task<VideoCatalogInitializationResult> InitializeAsync(CancellationToken ct = default);
    Task<VideoCatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    Task UpsertAssetAsync(VideoCatalogAssetUpsert asset, CancellationToken ct = default);
    Task UpdateAssetUserDataAsync(VideoCatalogUserDataUpdate update, CancellationToken ct = default);
    Task SetAssetHiddenAsync(string identityKey, bool hidden, CancellationToken ct = default);
    Task MarkAssetsUnavailableAsync(IReadOnlyList<string> identityKeys, CancellationToken ct = default);

    Task UpsertSourceAsync(VideoLibrarySource source, CancellationToken ct = default);
    Task UpdateSourceScanStateAsync(
        Guid sourceId,
        DateTimeOffset? lastScannedAt,
        string? error,
        CancellationToken ct = default);
    Task<long> BeginSourceScanAsync(Guid sourceId, VideoCatalogJobKind kind, CancellationToken ct = default);
    Task<bool> ApplyScanBatchAsync(VideoScanBatch batch, CancellationToken ct = default);
    Task CancelSourceScanAsync(Guid sourceId, CancellationToken ct = default);
    Task SetSourceScanPausedAsync(Guid sourceId, bool paused, CancellationToken ct = default);
    Task RemoveSourceAsync(Guid sourceId, CancellationToken ct = default);
    Task<Guid> BeginMetadataRefreshAsync(Guid sourceId, int totalCount, CancellationToken ct = default);
    Task UpdateMetadataRefreshAsync(
        Guid jobId,
        VideoCatalogJobState state,
        int processedCount,
        string? error,
        CancellationToken ct = default);

    Task UpsertCollectionAsync(VideoCollection collection, CancellationToken ct = default);
    Task DeleteCollectionAsync(Guid collectionId, CancellationToken ct = default);
    Task SetCollectionAssetsAsync(
        Guid collectionId,
        IReadOnlyList<string> identityKeys,
        CancellationToken ct = default);

    Task ReplaceMatchCandidatesAsync(
        Guid assetId,
        IReadOnlyList<VideoMatchCandidateSnapshot> candidates,
        CancellationToken ct = default);

    Task<bool> ApplyMetadataMatchAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        VideoMetadataDetails? details,
        bool lockIdentity,
        bool preserveExistingHierarchy,
        CancellationToken ct = default);

    Task<VideoProviderCacheEntry?> GetProviderCacheAsync(string cacheKey, CancellationToken ct = default);
    Task UpsertProviderCacheAsync(VideoProviderCacheEntry entry, CancellationToken ct = default);
    Task ApplyArtworkAsync(
        Guid assetId,
        VideoMetadataMediaKind ownerKind,
        string providerId,
        string kind,
        string remoteUrl,
        string localPath,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct = default);
}
