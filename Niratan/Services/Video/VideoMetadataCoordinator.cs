using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Settings;

namespace Niratan.Services.Video;

public enum VideoMetadataRefreshStage
{
    Searching,
    Matching,
    Details,
    Artwork,
    Completed,
}

public sealed record VideoMetadataRefreshProgress(
    Guid AssetId,
    VideoMetadataRefreshStage Stage,
    int CompletedProviders,
    int TotalProviders,
    string? ProviderId = null);

public sealed record VideoMetadataBatchProgress(
    Guid JobId,
    Guid SourceId,
    VideoCatalogJobState State,
    int ProcessedCount,
    int TotalCount,
    int MatchedCount,
    int NeedsReviewCount,
    Guid? CurrentAssetId,
    string? Error = null,
    int FailedCount = 0);

public interface IVideoMetadataCoordinator
{
    event EventHandler<VideoMetadataRefreshProgress>? ProgressChanged;
    event EventHandler<VideoMetadataBatchProgress>? BatchProgressChanged;
    IReadOnlyCollection<VideoMetadataBatchProgress> ActiveBatchProgress { get; }
    Task<IReadOnlyList<VideoMetadataTaskSnapshot>> GetTaskHistoryAsync(
        int limit = 50,
        CancellationToken ct = default);
    Task<VideoMetadataRefreshResult> RefreshAssetAsync(
        Guid assetId,
        bool allowNetwork,
        CancellationToken ct = default);
    Task<VideoRematchPreview> PreviewRematchAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        CancellationToken ct = default);
    Task ConfirmRematchAsync(
        VideoRematchPreview preview,
        CancellationToken ct = default);
    Task QueueSourceRefreshAsync(
        Guid sourceId,
        bool forceRefresh = false,
        CancellationToken ct = default);
    Task QueueAllSourcesAsync(bool forceRefresh = false, CancellationToken ct = default);
    Task CancelSourceRefreshAsync(Guid sourceId, CancellationToken ct = default);
    Task CancelTaskAsync(Guid jobId, CancellationToken ct = default);
    Task RetryTaskAsync(Guid jobId, CancellationToken ct = default);
    Task RetryFailedTasksAsync(CancellationToken ct = default);
    Task ClearAllScrapeRecordsAsync(CancellationToken ct = default);
}

internal sealed class VideoMetadataBatchExecution(CancellationTokenSource cancellation)
{
    public CancellationTokenSource Cancellation { get; } = cancellation;
    public Task Task { get; set; } = Task.CompletedTask;
}

internal sealed class VideoMetadataBatchRegistry
{
    private readonly ConcurrentDictionary<Guid, VideoMetadataBatchExecution> _items = [];

    public bool ContainsKey(Guid sourceId) => _items.ContainsKey(sourceId);

    public bool TryAdd(Guid sourceId, VideoMetadataBatchExecution execution) =>
        _items.TryAdd(sourceId, execution);

    public bool TryGetValue(Guid sourceId, out VideoMetadataBatchExecution execution) =>
        _items.TryGetValue(sourceId, out execution!);

    public VideoMetadataBatchExecution[] Snapshot() => _items.Values.ToArray();

    public bool Remove(Guid sourceId, VideoMetadataBatchExecution execution) =>
        _items.TryRemove(new KeyValuePair<Guid, VideoMetadataBatchExecution>(sourceId, execution));
}

internal sealed class VideoMetadataCoordinator : IVideoMetadataCoordinator
{
    private readonly IVideoCatalogRepository _repository;
    private readonly IVideoMetadataMatcher _matcher;
    private readonly IReadOnlyDictionary<string, IVideoMetadataSearchProvider> _search;
    private readonly IReadOnlyDictionary<string, IVideoMetadataDetailsProvider> _details;
    private readonly ILogger<VideoMetadataCoordinator> _logger;
    private readonly ISettingsService? _settings;
    private readonly IReadOnlyDictionary<string, IVideoArtworkProvider> _artworkProviders;
    private readonly IVideoMetadataTransport? _transport;
    private readonly IVideoArtworkCache? _artworkCache;
    private readonly IAniDbImportService? _aniDbImportService;
    private readonly IAniDbCatalogStore? _aniDbCatalogStore;
    private readonly SemaphoreSlim _scrapeLifecycleGate = new(1, 1);
    private readonly VideoMetadataBatchRegistry _activeBatches = new();
    private readonly object _scrapeOperationGate = new();
    private readonly HashSet<TaskCompletionSource<bool>> _activeScrapeOperations = [];
    private CancellationTokenSource _scrapeGeneration = new();
    private int _scrapeResetInProgress;
    private readonly ConcurrentDictionary<Guid, VideoMetadataBatchProgress> _batchProgress = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artworkDownloadGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _aniDbSettledRefreshes = [];

    private sealed class ScrapeOperationLease(
        VideoMetadataCoordinator owner,
        TaskCompletionSource<bool> completion,
        CancellationTokenSource linkedCancellation) : IDisposable
    {
        private int _disposed;

        public CancellationToken Token => linkedCancellation.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            linkedCancellation.Dispose();
            owner.CompleteScrapeOperation(completion);
        }
    }

    private readonly record struct ScrapeRequestStamp(
        CancellationTokenSource Generation,
        bool RequestedDuringReset);

    private ScrapeRequestStamp CaptureScrapeRequest() =>
        new(
            Volatile.Read(ref _scrapeGeneration),
            Volatile.Read(ref _scrapeResetInProgress) != 0);

    private bool IsScrapeRequestSuperseded(ScrapeRequestStamp request) =>
        request.RequestedDuringReset
        || !ReferenceEquals(request.Generation, Volatile.Read(ref _scrapeGeneration));

    public event EventHandler<VideoMetadataRefreshProgress>? ProgressChanged;
    public event EventHandler<VideoMetadataBatchProgress>? BatchProgressChanged;
    public IReadOnlyCollection<VideoMetadataBatchProgress> ActiveBatchProgress => _batchProgress.Values.ToArray();

    public async Task<IReadOnlyList<VideoMetadataTaskSnapshot>> GetTaskHistoryAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var stale = snapshot.Jobs
            .Where(job => job.Kind == VideoCatalogJobKind.MetadataRefresh
                          && (job.State is VideoCatalogJobState.Queued
                              or VideoCatalogJobState.Running
                              or VideoCatalogJobState.Paused)
                          && !IsActiveJob(job.Id))
            .ToArray();
        foreach (var job in stale)
        {
            await _repository.UpdateMetadataRefreshAsync(
                job.Id,
                VideoCatalogJobState.Interrupted,
                job.ProcessedCount,
                "The application stopped before this scrape completed.",
                ct);
        }
        if (stale.Length > 0)
            snapshot = await _repository.GetSnapshotAsync(ct);

        return snapshot.Jobs
            .Where(job => job.Kind == VideoCatalogJobKind.MetadataRefresh)
            .OrderByDescending(job => job.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(ToTaskSnapshot)
            .ToArray();
    }

    public VideoMetadataCoordinator(
        IVideoCatalogRepository repository,
        IVideoMetadataMatcher matcher,
        IEnumerable<IVideoMetadataSearchProvider> searchProviders,
        IEnumerable<IVideoMetadataDetailsProvider> detailsProviders,
        ILogger<VideoMetadataCoordinator> logger)
        : this(repository, matcher, searchProviders, detailsProviders, logger, null)
    {
    }

    public VideoMetadataCoordinator(
        IVideoCatalogRepository repository,
        IVideoMetadataMatcher matcher,
        IEnumerable<IVideoMetadataSearchProvider> searchProviders,
        IEnumerable<IVideoMetadataDetailsProvider> detailsProviders,
        ILogger<VideoMetadataCoordinator> logger,
        ISettingsService? settings)
        : this(repository, matcher, searchProviders, detailsProviders, logger, settings, [], null, null)
    {
    }

    public VideoMetadataCoordinator(
        IVideoCatalogRepository repository,
        IVideoMetadataMatcher matcher,
        IEnumerable<IVideoMetadataSearchProvider> searchProviders,
        IEnumerable<IVideoMetadataDetailsProvider> detailsProviders,
        ILogger<VideoMetadataCoordinator> logger,
        ISettingsService? settings,
        IEnumerable<IVideoArtworkProvider> artworkProviders,
        IVideoMetadataTransport? transport,
        IVideoArtworkCache? artworkCache,
        IAniDbImportService? aniDbImportService = null,
        IAniDbCatalogStore? aniDbCatalogStore = null)
    {
        _repository = repository;
        _matcher = matcher;
        _search = searchProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _details = detailsProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
        _settings = settings;
        _artworkProviders = artworkProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _transport = transport;
        _artworkCache = artworkCache;
        _aniDbImportService = aniDbImportService;
        _aniDbCatalogStore = aniDbCatalogStore;
        if (_aniDbImportService != null)
        {
            _aniDbImportService.AssetIdentificationSettled += OnAniDbIdentificationSettled;
            if (_aniDbCatalogStore != null)
                _ = ReplayPersistedAniDbIdentificationsAsync(CaptureScrapeRequest());
        }
    }

    public async Task<VideoMetadataRefreshResult> RefreshAssetAsync(
        Guid assetId,
        bool allowNetwork,
        CancellationToken ct = default)
    {
        using var operation = await BeginScrapeOperationAsync(ct);
        return await RefreshAssetCoreAsync(assetId, allowNetwork, operation.Token);
    }

    private async Task<ScrapeOperationLease> BeginScrapeOperationAsync(CancellationToken ct)
    {
        return await BeginScrapeOperationAsync(CaptureScrapeRequest(), ct);
    }

    private async Task<ScrapeOperationLease> BeginScrapeOperationAsync(
        ScrapeRequestStamp request,
        CancellationToken ct)
    {
        if (request.RequestedDuringReset)
            throw new OperationCanceledException(
                "The metadata operation was rejected while scrape records were being reset.");
        await _scrapeLifecycleGate.WaitAsync(ct);
        try
        {
            if (IsScrapeRequestSuperseded(request))
                throw new OperationCanceledException(
                    "The metadata operation was superseded by a scrape reset.");
            TaskCompletionSource<bool> completion;
            CancellationTokenSource linkedCancellation;
            lock (_scrapeOperationGate)
            {
                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    request.Generation.Token);
                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _activeScrapeOperations.Add(completion);
            }
            return new ScrapeOperationLease(this, completion, linkedCancellation);
        }
        finally
        {
            _scrapeLifecycleGate.Release();
        }
    }

    private void CompleteScrapeOperation(TaskCompletionSource<bool> completion)
    {
        lock (_scrapeOperationGate)
            _activeScrapeOperations.Remove(completion);
        completion.TrySetResult(true);
    }

    private async Task<VideoMetadataRefreshResult> RefreshAssetCoreAsync(
        Guid assetId,
        bool allowNetwork,
        CancellationToken ct)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var asset = snapshot.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new KeyNotFoundException("Video asset was not found.");
        if (IsAudio(asset.Location))
            return new VideoMetadataRefreshResult(assetId, false, false, null, null, []);

        var nodesById = snapshot.Nodes.ToDictionary(node => node.Id);
        var nodes = asset.NodeIds
            .Select(id => nodesById.GetValueOrDefault(id))
            .Where(node => node != null)
            .Select(node => node!)
            .ToList();
        var routeSource = ResolveSource(snapshot, asset);
        var route = ResolveRoute(snapshot, asset, nodes);
        AniDbAssetSnapshot? aniDbAsset = null;
        AniDbFileMatch? aniDbFileMatch = null;
        var sourceType = routeSource?.MediaType ?? VideoLibraryMediaType.Auto;
        var metadataSettings = _settings?.Current.VideoSettings.Metadata;
        var aniDbAdmissionEnabled = _aniDbCatalogStore != null
                                    && asset.Kind == VideoMediaAssetKind.LocalFile
                                    && asset.Availability == VideoMediaAvailability.Available
                                    && sourceType is VideoLibraryMediaType.Auto or VideoLibraryMediaType.Anime
                                    && (metadataSettings == null
                                        || metadataSettings.AniDbEnabled
                                        && metadataSettings.AniDbHashMatchingEnabled);
        if (aniDbAdmissionEnabled)
        {
            aniDbAsset = await _aniDbCatalogStore!.GetAssetAsync(assetId, ct);
            aniDbFileMatch = aniDbAsset?.FileMatch;
            if (aniDbFileMatch == null)
            {
                AniDbReleaseState? release = null;
                if (aniDbAsset is { Ed2k: not null, FileSize: var fileSize })
                {
                    release = await _aniDbCatalogStore.GetReleaseStateAsync(
                        aniDbAsset.Ed2k,
                        fileSize,
                        ct);
                }
                var now = DateTimeOffset.UtcNow;
                var lookupDue = release?.IsAutomaticLookupDue(now) == true;
                var definitivelyNotMatched = release?.Status == AniDbReleaseStatus.Ignored
                                             || release?.Status == AniDbReleaseStatus.Unrecognized
                                             && !lookupDue;
                var shouldQueue = allowNetwork
                                  && _aniDbImportService != null
                                  && (aniDbAsset?.Ed2k == null
                                      || release == null
                                      || release.Status == AniDbReleaseStatus.Never
                                      || release.Match != null
                                      || lookupDue);
                if (shouldQueue)
                    await _aniDbImportService!.QueueAssetAsync(assetId, ct);
                if (!definitivelyNotMatched || sourceType == VideoLibraryMediaType.Anime)
                {
                    return new VideoMetadataRefreshResult(
                        assetId,
                        false,
                        true,
                        "anidb",
                        definitivelyNotMatched
                            ? "AniDB did not recognize this anime release. Link it manually or rescan the release."
                            : "AniDB file identification is still pending.",
                        []);
                }
            }
        }
        if (aniDbFileMatch != null)
        {
            // An AniDB FILE match is exact release identity. It must immediately
            // close the automatic provider route to AniDB -> TMDB, even while the
            // richer Anime XML import is still pending or retrying.
            route = (
                VideoMetadataMediaKind.Anime,
                route.Language,
                route.Region,
                ResolveProviderOrder(
                    VideoLibraryMediaType.Anime,
                    VideoMetadataMediaKind.Anime,
                    []));
        }
        if (route.MediaKind == null)
        {
            return new VideoMetadataRefreshResult(
                assetId, false, true, null,
                "Auto could not reliably classify this asset.", []);
        }
        if (!allowNetwork)
        {
            return new VideoMetadataRefreshResult(
                assetId, false, true, null,
                "Online metadata consent is required before sending a parsed query.", []);
        }

        var primaryNode = nodes.FirstOrDefault();
        var identityNode = route.MediaKind is VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Anime
            ? FindAncestor(snapshot, primaryNode, VideoCatalogNodeKind.Series) ?? primaryNode
            : primaryNode;
        var queryExternalIds = identityNode?.ExternalIds ?? ImmutableDictionary<string, string>.Empty;
        if (aniDbFileMatch != null)
        {
            queryExternalIds = queryExternalIds.SetItem(
                "anidb",
                aniDbFileMatch.AnimeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Provider-discovered IDs are useful query hints, but only individually locked IDs are
        // explicit identity evidence. A node-level lock must not promote every provider ID on
        // that node into a permanent identity lock.
        var trustedIdentityExternalIds = identityNode == null
            ? ImmutableDictionary<string, string>.Empty
            : queryExternalIds
                .Where(pair => identityNode.IdentityLockedProviders.Contains(pair.Key))
                .ToImmutableDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
        if (aniDbFileMatch != null)
        {
            trustedIdentityExternalIds = trustedIdentityExternalIds.SetItem(
                "anidb",
                aniDbFileMatch.AnimeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var isSeasonScopedRoute = route.MediaKind is VideoMetadataMediaKind.Anime or VideoMetadataMediaKind.Series
                                  && (primaryNode?.SeasonNumber.HasValue == true
                                      || primaryNode?.EpisodeNumber.HasValue == true
                                      || asset.EpisodeStart.HasValue);
        var queryYear = isSeasonScopedRoute ? null : identityNode?.Year ?? primaryNode?.Year;
        var query = new VideoMetadataSearchQuery(
            identityNode?.PrimaryTitle ?? asset.Title,
            route.MediaKind.Value,
            queryYear,
            primaryNode?.SeasonNumber,
            primaryNode?.EpisodeNumber ?? asset.EpisodeStart,
            primaryNode?.AbsoluteEpisodeNumber,
            route.Language,
            route.Region,
            aniDbFileMatch != null
                ? trustedIdentityExternalIds
                : queryExternalIds);
        var candidates = new List<VideoMetadataCandidate>();
        var errors = new List<string>();
        var searchable = route.ProviderIds
            .Select((providerId, index) => new { ProviderId = providerId, Index = index })
            .Where(item => IsEnabled(item.ProviderId)
                           && _search.TryGetValue(item.ProviderId, out var provider)
                           && provider is not TvDbLicenseGatedProvider)
            .Select(item => new
            {
                item.ProviderId,
                item.Index,
                Provider = _search[item.ProviderId],
            })
            .ToArray();
        Publish(assetId, VideoMetadataRefreshStage.Searching, 0, searchable.Length);
        var searchTasks = searchable.Select(async item =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await item.Provider.SearchAsync(query, ct);
                return (item.Index, item.ProviderId, item.Provider.DisplayName,
                    Candidates: result, Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Video metadata provider {ProviderId} search failed", item.ProviderId);
                return (item.Index, item.ProviderId, item.Provider.DisplayName,
                    Candidates: (IReadOnlyList<VideoMetadataCandidate>)[], Error: ex);
            }
        }).ToArray();
        var completedSearches = 0;
        while (searchTasks.Length > 0 && searchTasks.Any(task => !task.IsCompleted))
        {
            var pending = searchTasks.Where(task => !task.IsCompleted).ToArray();
            await Task.WhenAny(pending);
            completedSearches = searchTasks.Count(task => task.IsCompleted);
            Publish(assetId, VideoMetadataRefreshStage.Searching,
                completedSearches, searchTasks.Length);
        }
        var searchResults = await Task.WhenAll(searchTasks);
        foreach (var result in searchResults.OrderBy(item => item.Index))
        {
            candidates.AddRange(result.Candidates);
            if (result.Error != null)
                errors.Add($"{result.DisplayName}: {result.Error.Message}");
        }

        Publish(assetId, VideoMetadataRefreshStage.Matching, searchable.Length, searchable.Length);
        var parsed = ToParsedIdentity(asset, primaryNode, trustedIdentityExternalIds) with
        {
            NormalizedTitle = identityNode?.PrimaryTitle ?? asset.Title,
            Year = queryYear,
        };
        var scored = _matcher.Score(parsed, route.MediaKind.Value, candidates).ToImmutableArray();
        await _repository.ReplaceMatchCandidatesAsync(
            assetId,
            scored.Select(score => new VideoMatchCandidateSnapshot(
                Guid.NewGuid(),
                assetId,
                score.Candidate.ProviderId,
                score.Candidate.ProviderItemId,
                score.Candidate.Title,
                score.Candidate.Year,
                score.Score,
                score.TitleScore,
                score.Evidence,
                score.HasHardConflict,
                DateTimeOffset.UtcNow)).ToList(), ct);
        var accepted = scored.FirstOrDefault(score => score.IsAccepted);
        if (accepted == null)
        {
            Publish(assetId, VideoMetadataRefreshStage.Completed, searchable.Length, searchable.Length);
            return new VideoMetadataRefreshResult(
                assetId,
                false,
                true,
                null,
                errors.Count == 0 ? null : string.Join(Environment.NewLine, errors),
                scored);
        }

        var detailsCandidate = SelectPrimaryDetailsCandidate(
            route.MediaKind.Value, accepted, scored);

        VideoMetadataDetails? details = null;
        if (_details.TryGetValue(detailsCandidate.ProviderId, out var detailsProvider))
        {
            Publish(assetId, VideoMetadataRefreshStage.Details, 0, 1, detailsCandidate.ProviderId);
            try
            {
                details = await detailsProvider.GetDetailsAsync(
                    detailsCandidate,
                    route.Language,
                    route.Region,
                    ct);
                details = details?.WithInitializedCollections();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Video metadata details refresh failed for {ProviderId}", accepted.Candidate.ProviderId);
                errors.Add($"{detailsProvider.DisplayName}: {ex.Message}");
            }
        }
        if (aniDbFileMatch != null
            && detailsCandidate.ProviderId.Equals("anidb", StringComparison.OrdinalIgnoreCase)
            && details == null)
        {
            // Do not fabricate a partial Series from only AID plus a filename.
            // Shoko creates the series/episodes after a valid Anime XML entity;
            // the persistent AniDB import job retains and retries this exact match.
            Publish(assetId, VideoMetadataRefreshStage.Completed, 1, 1, detailsCandidate.ProviderId);
            return new VideoMetadataRefreshResult(
                assetId,
                false,
                true,
                detailsCandidate.ProviderId,
                errors.Count == 0
                    ? "AniDB anime metadata is still pending."
                    : string.Join(Environment.NewLine, errors),
                scored);
        }
        var applied = await _repository.ApplyMetadataMatchAsync(
            assetId,
            accepted.Candidate,
            details,
            accepted.IsIdentityLocked,
            preserveExistingHierarchy: true,
            ct);
        if (!applied)
        {
            Publish(assetId, VideoMetadataRefreshStage.Completed, 1, 1, detailsCandidate.ProviderId);
            return new VideoMetadataRefreshResult(
                assetId,
                false,
                true,
                null,
                errors.Count == 0 ? null : string.Join(Environment.NewLine, errors),
                scored);
        }
        Publish(assetId, VideoMetadataRefreshStage.Artwork, 0, 1, detailsCandidate.ProviderId);
        try
        {
            await RefreshArtworkAsync(assetId, detailsCandidate, details, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video artwork refresh failed for {ProviderId}", detailsCandidate.ProviderId);
            errors.Add($"{detailsCandidate.ProviderId} artwork: {ex.Message}");
        }
        Publish(assetId, VideoMetadataRefreshStage.Completed, 1, 1, detailsCandidate.ProviderId);
        var degradedAniDb = aniDbFileMatch != null
                            && _aniDbCatalogStore != null
                            && (await _aniDbCatalogStore.GetAnimeAsync(
                                aniDbFileMatch.AnimeId,
                                ct))?.IsDegraded == true;
        return new VideoMetadataRefreshResult(
            assetId,
            !degradedAniDb,
            degradedAniDb,
            detailsCandidate.ProviderId,
            degradedAniDb
                ? CompactErrors(errors.Prepend(
                    "AniDB core metadata was loaded through the reduced UDP fallback; complete Anime XML metadata still requires a registered HTTP API client ID/version."))
                : errors.Count == 0 ? null : string.Join(Environment.NewLine, errors),
            scored);
    }

    internal static VideoMetadataCandidate SelectPrimaryDetailsCandidate(
        VideoMetadataMediaKind routeKind,
        VideoMetadataMatchScore accepted,
        IReadOnlyList<VideoMetadataMatchScore> scored)
    {
        if (routeKind is not (VideoMetadataMediaKind.Anime or VideoMetadataMediaKind.Series))
            return accepted.Candidate;
        var detailPriority = routeKind == VideoMetadataMediaKind.Anime
            ? new[] { "tmdb" }
            : new[] { "tmdb", "tvmaze" };
        var richCandidate = scored
            .Where(result => detailPriority.Contains(
                                 result.Candidate.ProviderId,
                                 StringComparer.OrdinalIgnoreCase)
                             && !result.HasHardConflict
                             && result.TitleScore >= 0.84
                             && (!accepted.Candidate.Year.HasValue
                                 || !result.Candidate.Year.HasValue
                                 || accepted.Candidate.Year == result.Candidate.Year
                                 || result.Candidate.SeasonNumber.HasValue
                                 || result.Candidate.EpisodeNumber.HasValue))
            .OrderBy(result => Array.FindIndex(
                detailPriority,
                providerId => providerId.Equals(
                    result.Candidate.ProviderId,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(result => result.Candidate)
            .FirstOrDefault();
        return richCandidate == null
            ? accepted.Candidate
            : richCandidate with
            {
                ExternalIds = richCandidate.ExternalIds.SetItems(accepted.Candidate.ExternalIds),
            };
    }

    public async Task QueueAllSourcesAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var requestedGeneration = Volatile.Read(ref _scrapeGeneration);
        var requestedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return;
        await _scrapeLifecycleGate.WaitAsync(ct);
        try
        {
            if (requestedDuringReset
                || !ReferenceEquals(requestedGeneration, Volatile.Read(ref _scrapeGeneration)))
                return;
            var snapshot = await _repository.GetSnapshotAsync(ct);
            if (forceRefresh)
            {
                foreach (var source in snapshot.Sources)
                {
                    ct.ThrowIfCancellationRequested();
                    await _repository.ClearRemoteMetadataAsync(source.Id, ct);
                }
            }
            foreach (var source in snapshot.Sources)
            {
                ct.ThrowIfCancellationRequested();
                await QueueSourceRefreshCoreAsync(
                    source.Id,
                    forceRefresh,
                    clearRemoteMetadata: false,
                    ct);
            }
        }
        finally
        {
            _scrapeLifecycleGate.Release();
        }
    }

    private void OnAniDbIdentificationSettled(
        object? sender,
        AniDbAssetIdentificationSettledEventArgs args)
    {
        _ = RefreshAfterAniDbIdentificationAsync(args.AssetId);
    }

    private async Task ReplayPersistedAniDbIdentificationsAsync(ScrapeRequestStamp request)
    {
        try
        {
            var metadata = _settings?.Current.VideoSettings.Metadata;
            if (metadata is not
                {
                    OnlineConsentAccepted: true,
                    AniDbEnabled: true,
                    AniDbHashMatchingEnabled: true,
                })
            {
                return;
            }
            var assets = await _aniDbCatalogStore!.GetAssetsAsync(CancellationToken.None);
            if (assets.IsDefaultOrEmpty)
                return;
            var availableAnimeIds = new HashSet<int>();
            foreach (var animeId in assets
                         .Where(asset => asset.FileMatch != null)
                         .Select(asset => asset.FileMatch!.AnimeId)
                         .Where(animeId => animeId > 0)
                         .Distinct())
            {
                if (await _aniDbCatalogStore.GetAnimeAsync(animeId, CancellationToken.None) != null)
                    availableAnimeIds.Add(animeId);
            }
            foreach (var asset in assets.Where(asset =>
                         asset.FileMatch != null
                         && availableAnimeIds.Contains(asset.FileMatch.AnimeId)))
            {
                if (IsScrapeRequestSuperseded(request))
                    return;
                if (!_aniDbSettledRefreshes.TryAdd(asset.AssetId, 0))
                    continue;
                try
                {
                    using var operation = await BeginScrapeOperationAsync(
                        request,
                        CancellationToken.None);
                    await RefreshAssetCoreAsync(
                        asset.AssetId,
                        allowNetwork: true,
                        operation.Token);
                }
                finally
                {
                    _aniDbSettledRefreshes.TryRemove(asset.AssetId, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A scrape reset invalidated the startup replay generation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisted AniDB metadata replay failed");
        }
    }

    private async Task RefreshAfterAniDbIdentificationAsync(Guid assetId)
    {
        if (!_aniDbSettledRefreshes.TryAdd(assetId, 0))
            return;
        try
        {
            await RefreshAssetAsync(assetId, allowNetwork: true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // A scrape reset or application shutdown owns cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Metadata enrichment after AniDB identification failed for {AssetId}",
                assetId);
        }
        finally
        {
            _aniDbSettledRefreshes.TryRemove(assetId, out _);
        }
    }

    public async Task QueueSourceRefreshAsync(
        Guid sourceId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var request = CaptureScrapeRequest();
        await QueueSourceRefreshWithStampAsync(sourceId, forceRefresh, request, ct);
    }

    private async Task QueueSourceRefreshWithStampAsync(
        Guid sourceId,
        bool forceRefresh,
        ScrapeRequestStamp request,
        CancellationToken ct)
    {
        if (request.RequestedDuringReset)
            return;
        await _scrapeLifecycleGate.WaitAsync(ct);
        try
        {
            if (IsScrapeRequestSuperseded(request))
                return;
            await QueueSourceRefreshCoreAsync(
                sourceId,
                forceRefresh,
                clearRemoteMetadata: forceRefresh,
                ct);
        }
        finally
        {
            _scrapeLifecycleGate.Release();
        }
    }

    private async Task QueueSourceRefreshCoreAsync(
        Guid sourceId,
        bool forceRefresh,
        bool clearRemoteMetadata,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_activeBatches.ContainsKey(sourceId))
            return;
        var snapshot = await _repository.GetSnapshotAsync(ct);
        if (!snapshot.Sources.Any(source => source.Id == sourceId))
            throw new KeyNotFoundException("Video source was not found.");
        var nodesById = snapshot.Nodes.ToDictionary(node => node.Id);
        var lastCompletedRefresh = snapshot.Jobs
            .Where(job => job.SourceId == sourceId
                          && job.Kind == VideoCatalogJobKind.MetadataRefresh
                          && job.State == VideoCatalogJobState.Completed)
            .OrderByDescending(job => job.UpdatedAt)
            .Select(job => (DateTimeOffset?)job.UpdatedAt)
            .FirstOrDefault();
        var assets = snapshot.Assets
            .Where(asset => asset.SourceIds.Contains(sourceId)
                            && asset.Availability == VideoMediaAvailability.Available
                            && !IsAudio(asset.Location))
            .Where(asset => forceRefresh || NeedsMetadata(asset, nodesById, lastCompletedRefresh))
            .OrderBy(asset => asset.Location, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (assets.Length == 0)
            return;
        if (clearRemoteMetadata)
            await _repository.ClearRemoteMetadataAsync(sourceId, ct);
        if (forceRefresh && _aniDbImportService != null)
            await _aniDbImportService.QueueSourceAsync(sourceId, ct);
        var jobId = await _repository.BeginMetadataRefreshAsync(sourceId, assets.Length, ct);
        var batchCts = new CancellationTokenSource();
        var execution = new VideoMetadataBatchExecution(batchCts);
        if (!_activeBatches.TryAdd(sourceId, execution))
        {
            batchCts.Dispose();
            await _repository.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Cancelled, 0, "Superseded by an active metadata refresh.", ct);
            return;
        }
        var initial = new VideoMetadataBatchProgress(
            jobId, sourceId, VideoCatalogJobState.Running, 0, assets.Length, 0, 0, null);
        PublishBatch(initial);
        execution.Task = RunBatchAsync(initial, assets, execution);
    }

    public async Task ClearAllScrapeRecordsAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _scrapeResetInProgress);
        var lifecycleGateHeld = false;
        var irreversibleCleanupStarted = false;
        CancellationTokenSource? priorGeneration = null;
        try
        {
            await _scrapeLifecycleGate.WaitAsync(ct);
            lifecycleGateHeld = true;
            Task[] directOperations;
            lock (_scrapeOperationGate)
            {
                priorGeneration = _scrapeGeneration;
                _scrapeGeneration = new CancellationTokenSource();
                directOperations = _activeScrapeOperations.Select(item => item.Task).ToArray();
            }
            priorGeneration.Cancel();

            var activeBatches = _activeBatches.Snapshot();
            foreach (var active in activeBatches)
                active.Cancellation.Cancel();
            var running = activeBatches.Select(item => item.Task)
                .Concat(directOperations)
                .ToArray();
            if (running.Length > 0)
                await Task.WhenAll(running).WaitAsync(ct);

            irreversibleCleanupStarted = true;
            if (_aniDbImportService != null)
            {
                await _aniDbImportService.ClearScrapingRecordsAsync(
                    async (manualAniDbAssets, resetToken) =>
                    {
                        await _repository.ClearAllScrapeRecordsAsync(
                            manualAniDbAssets, resetToken);
                        if (_artworkCache != null)
                            await _artworkCache.ClearAsync(resetToken);
                    },
                    ct);
            }
            else
            {
                await _repository.ClearAllScrapeRecordsAsync(ct);
                if (_artworkCache != null)
                    await _artworkCache.ClearAsync(ct);
            }
        }
        finally
        {
            if (irreversibleCleanupStarted)
                _batchProgress.Clear();
            priorGeneration?.Dispose();
            if (lifecycleGateHeld)
                _scrapeLifecycleGate.Release();
            Interlocked.Decrement(ref _scrapeResetInProgress);
        }
    }

    public async Task CancelSourceRefreshAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (_activeBatches.TryGetValue(sourceId, out var active))
            active.Cancellation.Cancel();
        if (_batchProgress.TryGetValue(sourceId, out var progress))
        {
            await _repository.UpdateMetadataRefreshAsync(
                progress.JobId, VideoCatalogJobState.Cancelled, progress.ProcessedCount, progress.Error, ct);
            await _repository.UpdateMetadataRefreshCountsAsync(
                progress.JobId, progress.MatchedCount, progress.NeedsReviewCount, ct, progress.FailedCount);
        }
    }

    public async Task CancelTaskAsync(Guid jobId, CancellationToken ct = default)
    {
        var live = _batchProgress.Values.FirstOrDefault(progress => progress.JobId == jobId);
        if (live != null && _activeBatches.ContainsKey(live.SourceId))
        {
            await CancelSourceRefreshAsync(live.SourceId, ct);
            return;
        }

        var snapshot = await _repository.GetSnapshotAsync(ct);
        var job = snapshot.Jobs.FirstOrDefault(candidate =>
            candidate.Id == jobId && candidate.Kind == VideoCatalogJobKind.MetadataRefresh);
        if (job == null || job.State is not (VideoCatalogJobState.Queued
            or VideoCatalogJobState.Running
            or VideoCatalogJobState.Paused))
            return;

        await _repository.UpdateMetadataRefreshAsync(
            job.Id, VideoCatalogJobState.Cancelled, job.ProcessedCount, job.Error, ct);
        await _repository.UpdateMetadataRefreshCountsAsync(
            job.Id, job.MatchedCount, job.NeedsReviewCount, ct, job.FailedCount);
    }

    public async Task RetryTaskAsync(Guid jobId, CancellationToken ct = default)
    {
        var request = CaptureScrapeRequest();
        if (request.RequestedDuringReset)
            return;
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var job = snapshot.Jobs.FirstOrDefault(candidate =>
            candidate.Id == jobId && candidate.Kind == VideoCatalogJobKind.MetadataRefresh);
        if (job?.SourceId is not Guid sourceId
            || job.State is not (VideoCatalogJobState.Failed
                or VideoCatalogJobState.Cancelled
                or VideoCatalogJobState.Interrupted))
            return;

        await QueueSourceRefreshWithStampAsync(sourceId, forceRefresh: true, request, ct);
    }

    public async Task RetryFailedTasksAsync(CancellationToken ct = default)
    {
        var request = CaptureScrapeRequest();
        if (request.RequestedDuringReset)
            return;
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var sourceIds = snapshot.Jobs
            .Where(job => job.Kind == VideoCatalogJobKind.MetadataRefresh
                          && job.SourceId is not null
                          && (job.State is VideoCatalogJobState.Failed
                              or VideoCatalogJobState.Interrupted))
            .Select(job => job.SourceId!.Value)
            .Distinct()
            .Where(sourceId => snapshot.Sources.Any(source => source.Id == sourceId))
            .ToArray();
        foreach (var sourceId in sourceIds)
        {
            ct.ThrowIfCancellationRequested();
            await QueueSourceRefreshWithStampAsync(sourceId, forceRefresh: true, request, ct);
        }
    }

    private async Task RunBatchAsync(
        VideoMetadataBatchProgress initial,
        IReadOnlyList<VideoCatalogAssetSnapshot> assets,
        VideoMetadataBatchExecution execution)
    {
        var processed = 0;
        var matched = 0;
        var needsReview = 0;
        var failedCount = 0;
        var errors = new ConcurrentQueue<string>();
        try
        {
            await Parallel.ForEachAsync(
                assets,
                new ParallelOptions
                {
                    CancellationToken = execution.Cancellation.Token,
                    MaxDegreeOfParallelism = 2,
                },
                async (asset, token) =>
                {
                    VideoMetadataRefreshResult result;
                    try
                    {
                        result = await RefreshAssetAsync(asset.Id, allowNetwork: true, token);
                        if (result.Matched)
                            Interlocked.Increment(ref matched);
                        if (result.NeedsReview)
                            Interlocked.Increment(ref needsReview);
                        if (!string.IsNullOrWhiteSpace(result.Error))
                        {
                            errors.Enqueue(result.Error);
                            Interlocked.Increment(ref failedCount);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Enqueue(ex.Message);
                        Interlocked.Increment(ref failedCount);
                    }
                    var current = Interlocked.Increment(ref processed);
                    var progress = initial with
                    {
                        ProcessedCount = current,
                        MatchedCount = Volatile.Read(ref matched),
                        NeedsReviewCount = Volatile.Read(ref needsReview),
                        FailedCount = Volatile.Read(ref failedCount),
                        CurrentAssetId = asset.Id,
                        Error = CompactErrors(errors),
                    };
                    PublishBatch(progress);
                    await _repository.UpdateMetadataRefreshAsync(
                        initial.JobId,
                        VideoCatalogJobState.Running,
                        current,
                        progress.Error,
                        CancellationToken.None);
                    await _repository.UpdateMetadataRefreshCountsAsync(
                        initial.JobId,
                        progress.MatchedCount,
                        progress.NeedsReviewCount,
                        CancellationToken.None,
                        progress.FailedCount);
                });
            var completed = initial with
            {
                State = VideoCatalogJobState.Completed,
                ProcessedCount = processed,
                MatchedCount = matched,
                NeedsReviewCount = needsReview,
                FailedCount = failedCount,
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, completed.State, processed, completed.Error, CancellationToken.None);
            await _repository.UpdateMetadataRefreshCountsAsync(
                initial.JobId, matched, needsReview, CancellationToken.None, failedCount);
            PublishBatch(completed);
        }
        catch (OperationCanceledException)
        {
            var cancelled = initial with
            {
                State = VideoCatalogJobState.Cancelled,
                ProcessedCount = processed,
                MatchedCount = matched,
                NeedsReviewCount = needsReview,
                FailedCount = failedCount,
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, cancelled.State, processed, cancelled.Error, CancellationToken.None);
            await _repository.UpdateMetadataRefreshCountsAsync(
                initial.JobId, matched, needsReview, CancellationToken.None, failedCount);
            PublishBatch(cancelled);
        }
        catch (Exception ex)
        {
            errors.Enqueue(ex.Message);
            var failed = initial with
            {
                State = VideoCatalogJobState.Failed,
                ProcessedCount = processed,
                MatchedCount = matched,
                NeedsReviewCount = needsReview,
                FailedCount = failedCount,
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, failed.State, processed, failed.Error, CancellationToken.None);
            await _repository.UpdateMetadataRefreshCountsAsync(
                initial.JobId, matched, needsReview, CancellationToken.None, failedCount);
            PublishBatch(failed);
        }
        finally
        {
            _activeBatches.Remove(initial.SourceId, execution);
            execution.Cancellation.Dispose();
        }
    }

    internal static bool NeedsMetadata(
        VideoCatalogAssetSnapshot asset,
        IReadOnlyDictionary<Guid, VideoCatalogNodeSnapshot> nodesById,
        DateTimeOffset? lastCompletedRefresh)
    {
        if (asset.CatalogResetPending)
            return false;
        var directNodes = asset.NodeIds
            .Select(id => nodesById.GetValueOrDefault(id))
            .Where(node => node != null)
            .Select(node => node!)
            .ToArray();
        var nodes = CollectAncestry(directNodes, nodesById);
        var hasMetadataSnapshot = nodes.Any(node => node.MetadataExpiresAt.HasValue);
        if (!hasMetadataSnapshot)
        {
            // A completed source job is also the negative-result cache. Do not search
            // unchanged unresolved assets again on every Video navigation; a new or
            // modified asset, or an explicit force refresh, opens a new attempt.
            return !lastCompletedRefresh.HasValue
                   || asset.ImportedAt > lastCompletedRefresh.Value
                   || asset.ModifiedAt is { } modified && modified > lastCompletedRefresh.Value;
        }
        return nodes.Any(node => node.MetadataExpiresAt is { } expiresAt
                                 && expiresAt <= DateTimeOffset.UtcNow);
    }

    private static ImmutableArray<VideoCatalogNodeSnapshot> CollectAncestry(
        IEnumerable<VideoCatalogNodeSnapshot> directNodes,
        IReadOnlyDictionary<Guid, VideoCatalogNodeSnapshot> nodesById)
    {
        var result = ImmutableArray.CreateBuilder<VideoCatalogNodeSnapshot>();
        var seen = new HashSet<Guid>();
        foreach (var directNode in directNodes)
        {
            VideoCatalogNodeSnapshot? current = directNode;
            while (current != null && seen.Add(current.Id))
            {
                result.Add(current);
                current = current.ParentId is { } parentId
                    ? nodesById.GetValueOrDefault(parentId)
                    : null;
            }
        }
        return result.ToImmutable();
    }

    private static string? CompactErrors(IEnumerable<string> errors)
    {
        var values = errors.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(5).ToArray();
        return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
    }

    private bool IsActiveJob(Guid jobId) =>
        _batchProgress.Values.Any(progress => progress.JobId == jobId
                                              && _activeBatches.ContainsKey(progress.SourceId));

    private VideoMetadataTaskSnapshot ToTaskSnapshot(VideoCatalogJobSnapshot job)
    {
        var live = _batchProgress.Values.FirstOrDefault(progress => progress.JobId == job.Id);
        return new VideoMetadataTaskSnapshot(
            job.Id,
            job.SourceId,
            live?.State ?? job.State,
            live?.ProcessedCount ?? job.ProcessedCount,
            live?.TotalCount ?? job.TotalCount,
            live?.MatchedCount ?? job.MatchedCount,
            live?.NeedsReviewCount ?? job.NeedsReviewCount,
            live?.Error ?? job.Error,
            job.CreatedAt,
            job.UpdatedAt,
            live?.FailedCount ?? job.FailedCount);
    }

    private void PublishBatch(VideoMetadataBatchProgress progress)
    {
        _batchProgress[progress.SourceId] = progress;
        BatchProgressChanged?.Invoke(this, progress);
    }

    private void Publish(
        Guid assetId,
        VideoMetadataRefreshStage stage,
        int completedProviders,
        int totalProviders,
        string? providerId = null) =>
        ProgressChanged?.Invoke(this, new VideoMetadataRefreshProgress(
            assetId, stage, completedProviders, totalProviders, providerId));

    private bool IsEnabled(string providerId)
    {
        var metadata = _settings?.Current.VideoSettings.Metadata;
        return providerId.ToLowerInvariant() switch
        {
            "tmdb" => metadata?.TmdbEnabled != false,
            "tvmaze" => metadata?.TvMazeEnabled != false,
            "anilist" => metadata?.AniListEnabled != false,
            "anidb" => metadata?.AniDbEnabled != false,
            "tvdb" => metadata?.TvDbEnabled == true,
            _ => true,
        };
    }

    private async Task RefreshArtworkAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        VideoMetadataDetails? details,
        CancellationToken ct)
    {
        if (_transport == null || _artworkCache == null)
            return;

        var primaryArtwork = new List<VideoArtworkCandidate>();
        foreach (var identity in LinkedArtworkIdentities(candidate))
        {
            if (!_artworkProviders.TryGetValue(identity.ProviderId, out var provider)
                || !IsArtworkEnabled(identity.ProviderId))
                continue;
            try
            {
                var providerArtwork = await provider.GetArtworkAsync(identity, ct);
                primaryArtwork.AddRange(providerArtwork
                    .Where(item => item.Kind is "poster" or "backdrop" or "thumb" or "logo")
                    .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.Take(PrimaryArtworkLimit(group.Key)))
                    .Select(item => item with
                    {
                        OwnerKind = RootArtworkOwner(candidate.MediaKind),
                    }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Video artwork candidate lookup failed for {ProviderId}",
                    identity.ProviderId);
            }
        }

        var rootOwner = RootArtworkOwner(candidate.MediaKind);
        var peopleArtwork = details?.People.IsDefault == false
            ? details.People
                .Where(person => !string.IsNullOrWhiteSpace(person.ImageUrl))
                .Take(12)
                .Select(person => new VideoArtworkCandidate(
                    details.ProviderId,
                    person.ImageUrl!,
                    $"person:{person.ProviderPersonId}",
                    null, null, null, person.ImageUrl)
                {
                    OwnerKind = rootOwner,
                })
            : [];
        var relatedArtwork = details?.RelatedItems.IsDefault == false
            ? details.RelatedItems
                .Take(8)
                .SelectMany(item => new[]
                {
                    string.IsNullOrWhiteSpace(item.PosterUrl)
                        ? null
                        : new VideoArtworkCandidate(
                            item.ProviderId,
                            item.PosterUrl!,
                            $"related:{item.ProviderId}:{item.ProviderItemId}:poster",
                            null, null, null, item.SourceUrl)
                        {
                            OwnerKind = rootOwner,
                        },
                    string.IsNullOrWhiteSpace(item.BackdropUrl)
                        ? null
                        : new VideoArtworkCandidate(
                            item.ProviderId,
                            item.BackdropUrl!,
                            $"related:{item.ProviderId}:{item.ProviderItemId}:backdrop",
                            null, null, null, item.SourceUrl)
                        {
                            OwnerKind = rootOwner,
                        },
                })
                .Where(item => item != null)
                .Select(item => item!)
            : [];

        var seasonArtwork = details?.Seasons.IsDefault == false
            ? details.Seasons
                .Where(season => !string.IsNullOrWhiteSpace(season.PosterUrl))
                .OrderBy(season => season.SeasonNumber == candidate.SeasonNumber ? 0 : 1)
                .ThenBy(season => season.SeasonNumber)
                .Take(12)
                .Select(season => new VideoArtworkCandidate(
                    details.ProviderId,
                    season.PosterUrl!,
                    "poster",
                    null, null, null, details.SourceUrl)
                {
                    OwnerKind = VideoMetadataMediaKind.Season,
                    SeasonNumber = season.SeasonNumber,
                })
            : [];
        var episodeArtwork = details?.Seasons.IsDefault == false
            ? details.Seasons
                .SelectMany(season => season.Episodes.IsDefault
                    ? []
                    : season.Episodes.Select(episode => (Season: season, Episode: episode)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Episode.ThumbnailUrl))
                .OrderBy(item => item.Season.SeasonNumber == candidate.SeasonNumber
                                 && item.Episode.EpisodeNumber == candidate.EpisodeNumber ? 0 : 1)
                .ThenBy(item => item.Season.SeasonNumber)
                .ThenBy(item => item.Episode.EpisodeNumber)
                .Take(24)
                .Select(item => new VideoArtworkCandidate(
                    details.ProviderId,
                    item.Episode.ThumbnailUrl!,
                    "thumb",
                    null, null, null, item.Episode.SourceUrl)
                {
                    OwnerKind = VideoMetadataMediaKind.Episode,
                    SeasonNumber = item.Season.SeasonNumber,
                    EpisodeNumber = item.Episode.EpisodeNumber,
                })
            : [];
        var rawCandidates = primaryArtwork
            .Concat(peopleArtwork)
            .Concat(relatedArtwork)
            .Concat(seasonArtwork)
            .Concat(episodeArtwork)
            .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps)
            .DistinctBy(
                item => $"{item.ProviderId}\0{item.OwnerKind}\0{item.SeasonNumber}\0{item.EpisodeNumber}\0{item.Kind}\0{item.Url}",
                StringComparer.Ordinal)
            .ToArray();
        var artworkCandidates = rawCandidates
            .GroupBy(item => new
            {
                Owner = item.OwnerKind ?? rootOwner,
                item.SeasonNumber,
                item.EpisodeNumber,
                Kind = item.Kind.ToLowerInvariant(),
            })
            .SelectMany(group => group.Select((item, ordinal) => item with
            {
                Ordinal = ordinal,
                IsPreferred = ordinal == 0,
            }))
            .ToArray();

        // Persist candidate ordering before any download starts. The catalog only
        // assigns a default when a kind has no existing/user preference, so network
        // completion order can never replace a stable choice.
        foreach (var artwork in artworkCandidates)
        {
            await _repository.UpsertArtworkCandidateAsync(
                assetId, candidate.MediaKind, artwork, ct: ct);
        }
        await Parallel.ForEachAsync(
            artworkCandidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            async (artwork, itemToken) =>
                await CacheAndApplyArtworkAsync(assetId, candidate, artwork, itemToken));
    }

    private bool IsArtworkEnabled(string providerId)
    {
        var configured = _settings?.Current.VideoSettings.Metadata.ArtworkEnabled;
        return configured == null
               || !configured.TryGetValue(providerId, out var enabled)
               || enabled;
    }

    private static IEnumerable<VideoMetadataCandidate> LinkedArtworkIdentities(
        VideoMetadataCandidate candidate)
    {
        var providerIds = candidate.MediaKind == VideoMetadataMediaKind.Anime
            ? new[] { "anidb", "tmdb" }
            : new[] { candidate.ProviderId };
        foreach (var providerId in providerIds)
        {
            var providerItemId = providerId.Equals(candidate.ProviderId, StringComparison.OrdinalIgnoreCase)
                ? candidate.ProviderItemId
                : candidate.ExternalIds.GetValueOrDefault(providerId);
            if (string.IsNullOrWhiteSpace(providerItemId))
                continue;
            yield return candidate with
            {
                ProviderId = providerId,
                ProviderItemId = providerItemId,
                SourceUrl = providerId.Equals(candidate.ProviderId, StringComparison.OrdinalIgnoreCase)
                    ? candidate.SourceUrl
                    : null,
            };
        }
    }

    private static VideoMetadataMediaKind RootArtworkOwner(VideoMetadataMediaKind kind) => kind switch
    {
        VideoMetadataMediaKind.Movie => VideoMetadataMediaKind.Movie,
        VideoMetadataMediaKind.Anime => VideoMetadataMediaKind.Anime,
        _ => VideoMetadataMediaKind.Series,
    };

    private static int PrimaryArtworkLimit(string kind) => kind.ToLowerInvariant() switch
    {
        "poster" => 8,
        "backdrop" => 8,
        "logo" => 4,
        "thumb" => 4,
        _ => 0,
    };

    private async Task CacheAndApplyArtworkAsync(
        Guid assetId,
        VideoMetadataCandidate owner,
        VideoArtworkCandidate artwork,
        CancellationToken ct)
    {
        var gate = _artworkDownloadGates.GetOrAdd(artwork.Url, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var attemptedDownload = false;
            try
            {
                var existing = await _artworkCache!.GetAsync(artwork.Url, ct);
                VideoArtworkCacheEntry cached;
                if (existing != null)
                {
                    cached = existing;
                }
                else
                {
                    attemptedDownload = true;
                    var response = await _transport!.SendAsync(new VideoMetadataRequest(
                        artwork.ProviderId,
                        HttpMethod.Get,
                        new Uri(artwork.Url),
                        IsIdempotent: false,
                        MaxResponseBytes: 20L * 1024 * 1024), ct);
                    if (response.StatusCode is < 200 or >= 300)
                    {
                        await _repository.UpsertArtworkCandidateAsync(
                            assetId,
                            owner.MediaKind,
                            artwork,
                            downloadAttempted: true,
                            lastError: $"HTTP {response.StatusCode}",
                            ct: ct);
                        return;
                    }
                    await using var stream = new MemoryStream(response.Content, writable: false);
                    cached = await _artworkCache.StoreAsync(
                        artwork.Url, stream, response.ContentType, response.ETag, response.LastModified, ct);
                }
                await _repository.UpsertArtworkCandidateAsync(
                    assetId,
                    owner.MediaKind,
                    artwork,
                    cached.LocalPath,
                    cached.ETag,
                    cached.LastModified,
                    attemptedDownload,
                    lastError: null,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _repository.UpsertArtworkCandidateAsync(
                    assetId,
                    owner.MediaKind,
                    artwork,
                    downloadAttempted: attemptedDownload,
                    lastError: ex.Message,
                    ct: CancellationToken.None);
                _logger.LogWarning(
                    ex,
                    "Video artwork download failed for {ProviderId} {ArtworkUrl}",
                    artwork.ProviderId,
                    artwork.Url);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<VideoRematchPreview> PreviewRematchAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        CancellationToken ct = default)
    {
        using var operation = await BeginScrapeOperationAsync(ct);
        return await PreviewRematchCoreAsync(assetId, candidate, operation.Token);
    }

    private async Task<VideoRematchPreview> PreviewRematchCoreAsync(
        Guid assetId,
        VideoMetadataCandidate candidate,
        CancellationToken ct)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var asset = snapshot.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new KeyNotFoundException("Video asset was not found.");
        var currentNodes = snapshot.Nodes.Where(node => asset.NodeIds.Contains(node.Id)).ToList();
        VideoMetadataDetails? details = null;
        if (_details.TryGetValue(candidate.ProviderId, out var provider)
            && provider is not TvDbLicenseGatedProvider)
        {
            details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);
            details = details?.WithInitializedCollections();
        }
        var proposed = details ?? new VideoMetadataDetails(
            candidate.ProviderId, candidate.ProviderItemId, candidate.MediaKind,
            candidate.Title, candidate.OriginalTitle, null, null, candidate.Year,
            candidate.SeasonNumber, candidate.EpisodeNumber, candidate.AbsoluteEpisodeNumber,
            candidate.Aliases, [], [], candidate.ExternalIds, candidate.SourceUrl,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        var current = currentNodes.FirstOrDefault();
        var changes = new[]
        {
            new VideoRematchFieldChange("title", current?.PrimaryTitle, proposed.Title, candidate.ProviderId),
            new VideoRematchFieldChange("originalTitle", current?.OriginalTitle, proposed.OriginalTitle, candidate.ProviderId),
            new VideoRematchFieldChange("year", current?.Year?.ToString(), proposed.Year?.ToString(), candidate.ProviderId),
            new VideoRematchFieldChange("season", current?.SeasonNumber?.ToString(), proposed.SeasonNumber?.ToString(), candidate.ProviderId),
            new VideoRematchFieldChange("episode", current?.EpisodeNumber?.ToString(), proposed.EpisodeNumber?.ToString(), candidate.ProviderId),
        }.Where(change => !string.Equals(change.CurrentValue, change.ProposedValue, StringComparison.Ordinal)).ToImmutableArray();
        var crossSeason = asset.EpisodeStart.HasValue
                          && asset.EpisodeEnd.HasValue
                          && asset.EpisodeStart != asset.EpisodeEnd
                          && currentNodes.Select(node => node.SeasonNumber).Where(value => value.HasValue).Distinct().Count() > 1;
        return new VideoRematchPreview(
            assetId,
            asset.NodeIds,
            candidate,
            changes,
            proposed.MediaKind == VideoMetadataMediaKind.Movie
                ? "Movie"
                : $"Series / Season {proposed.SeasonNumber?.ToString() ?? "?"} / Episode {proposed.EpisodeNumber?.ToString() ?? "?"}",
            crossSeason);
    }

    public async Task ConfirmRematchAsync(VideoRematchPreview preview, CancellationToken ct = default)
    {
        using var operation = await BeginScrapeOperationAsync(ct);
        await ConfirmRematchCoreAsync(preview, operation.Token);
    }

    private async Task ConfirmRematchCoreAsync(VideoRematchPreview preview, CancellationToken ct)
    {
        VideoMetadataDetails? details = null;
        if (_details.TryGetValue(preview.Candidate.ProviderId, out var provider)
            && provider is not TvDbLicenseGatedProvider)
        {
            details = await provider.GetDetailsAsync(preview.Candidate, "ja-JP", "JP", ct);
            details = details?.WithInitializedCollections();
        }
        await _repository.ApplyMetadataMatchAsync(
            preview.AssetId,
            preview.Candidate,
            details,
            lockIdentity: true,
            preserveExistingHierarchy: false,
            ct);
    }

    private static (VideoMetadataMediaKind? MediaKind, string Language, string Region, ImmutableArray<string> ProviderIds) ResolveRoute(
        VideoCatalogSnapshot snapshot,
        VideoCatalogAssetSnapshot asset,
        IReadOnlyList<VideoCatalogNodeSnapshot> nodes)
    {
        var source = ResolveSource(snapshot, asset);
        var type = source?.MediaType ?? VideoLibraryMediaType.Auto;
        var evidenceNodes = CollectRouteEvidenceNodes(snapshot, nodes);
        var hasAnimeIdentity = evidenceNodes
            .SelectMany(node => node.ExternalIds.Keys)
            .Any(providerId => providerId.Equals("anidb", StringComparison.OrdinalIgnoreCase)
                               || providerId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
                               || providerId.Equals("mal", StringComparison.OrdinalIgnoreCase));
        var kind = ResolveMediaKind(
            type,
            hasAnimeIdentity,
            evidenceNodes.Any(node => node.AbsoluteEpisodeNumber.HasValue),
            evidenceNodes.Any(node => node.Kind is VideoCatalogNodeKind.Series or VideoCatalogNodeKind.Season),
            asset.EpisodeStart.HasValue || evidenceNodes.Any(node => node.EpisodeNumber.HasValue),
            evidenceNodes.Any(node => node.Year.HasValue));
        return (
            kind,
            source?.Language ?? "ja-JP",
            source?.Region ?? "JP",
            ResolveProviderOrder(type, kind, source?.ProviderOrder ?? []));
    }

    private static VideoCatalogSourceSnapshot? ResolveSource(
        VideoCatalogSnapshot snapshot,
        VideoCatalogAssetSnapshot asset) =>
        asset.SourceIds.Select(id => snapshot.Sources.FirstOrDefault(item => item.Id == id))
            .Where(item => item != null)
            .OrderByDescending(item => item!.FolderPath.Length)
            .ThenBy(item => item!.CreatedAt)
            .FirstOrDefault();

    internal static ImmutableArray<string> ResolveProviderOrder(
        VideoLibraryMediaType sourceType,
        VideoMetadataMediaKind? mediaKind,
        IReadOnlyList<string> configuredOrder)
    {
        // Shoko's anime identity chain is deliberately closed: AniDB owns the
        // file/anime identity and TMDB may enrich that identity through a cross-ref.
        // A legacy custom source route must not re-introduce AniList, Bangumi or
        // TVmaze into automatic anime scraping.
        if (sourceType == VideoLibraryMediaType.Anime || mediaKind == VideoMetadataMediaKind.Anime)
            return ["anidb", "tmdb"];

        var defaults = sourceType switch
        {
            VideoLibraryMediaType.JapaneseDramaTv => new[] { "tmdb", "tvmaze" },
            VideoLibraryMediaType.Movie => new[] { "tmdb" },
            _ when mediaKind == VideoMetadataMediaKind.Series => new[] { "tmdb", "tvmaze" },
            _ when mediaKind == VideoMetadataMediaKind.Movie => new[] { "tmdb" },
            _ => Array.Empty<string>(),
        };
        var sanitized = configuredOrder
            .Where(providerId => !providerId.Equals("bangumi", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return sanitized.Length > 0 ? sanitized : defaults.ToImmutableArray();
    }

    internal static VideoMetadataMediaKind? ResolveMediaKind(
        VideoLibraryMediaType sourceType,
        bool hasAnimeExternalIdentity,
        bool hasAbsoluteEpisodeEvidence,
        bool hasSeriesHierarchyEvidence,
        bool hasEpisodeEvidence,
        bool hasYearEvidence) => sourceType switch
    {
        VideoLibraryMediaType.Movie => VideoMetadataMediaKind.Movie,
        VideoLibraryMediaType.Anime => VideoMetadataMediaKind.Anime,
        VideoLibraryMediaType.JapaneseDramaTv => VideoMetadataMediaKind.Series,
        _ when hasAnimeExternalIdentity => VideoMetadataMediaKind.Anime,
        _ when hasAbsoluteEpisodeEvidence => VideoMetadataMediaKind.Anime,
        _ when hasSeriesHierarchyEvidence => VideoMetadataMediaKind.Series,
        _ when hasEpisodeEvidence => VideoMetadataMediaKind.Series,
        _ when hasYearEvidence => VideoMetadataMediaKind.Movie,
        _ => null,
    };

    internal static ImmutableArray<VideoCatalogNodeSnapshot> CollectRouteEvidenceNodes(
        VideoCatalogSnapshot snapshot,
        IReadOnlyList<VideoCatalogNodeSnapshot> directNodes)
    {
        var byId = snapshot.Nodes.ToDictionary(node => node.Id);
        var evidence = ImmutableArray.CreateBuilder<VideoCatalogNodeSnapshot>();
        var seen = new HashSet<Guid>();
        foreach (var directNode in directNodes)
        {
            VideoCatalogNodeSnapshot? current = directNode;
            while (current != null && seen.Add(current.Id))
            {
                evidence.Add(current);
                current = current.ParentId is { } parentId
                    ? byId.GetValueOrDefault(parentId)
                    : null;
            }
        }
        return evidence.ToImmutable();
    }

    private static ParsedVideoIdentity ToParsedIdentity(
        VideoCatalogAssetSnapshot asset,
        VideoCatalogNodeSnapshot? node,
        ImmutableDictionary<string, string> externalIds) =>
        new(
            asset.Title,
            node?.PrimaryTitle ?? asset.Title,
            asset.ParentFolder,
            node?.Year,
            node?.SeasonNumber,
            node?.EpisodeNumber ?? asset.EpisodeStart,
            asset.EpisodeEnd,
            node?.AbsoluteEpisodeNumber,
            null,
            null,
            node?.IsSpecial == true ? ParsedVideoSpecialKind.Special : ParsedVideoSpecialKind.None,
            asset.EpisodeStart.HasValue && asset.EpisodeEnd.HasValue && asset.EpisodeStart != asset.EpisodeEnd,
            asset.EpisodeStart.HasValue || node?.EpisodeNumber.HasValue == true,
            externalIds,
            []);

    private static VideoCatalogNodeSnapshot? FindAncestor(
        VideoCatalogSnapshot snapshot,
        VideoCatalogNodeSnapshot? node,
        VideoCatalogNodeKind kind)
    {
        var current = node;
        while (current != null)
        {
            if (current.Kind == kind)
                return current;
            current = current.ParentId.HasValue
                ? snapshot.Nodes.FirstOrDefault(candidate => candidate.Id == current.ParentId.Value)
                : null;
        }
        return null;
    }

    private static bool IsAudio(string path) => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma",
    }.Contains(Path.GetExtension(path));
}
