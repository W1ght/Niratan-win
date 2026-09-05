using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Video;
using Niratan.Services.Storage;

namespace Niratan.Services.Video;

internal sealed class AniDbImportService : IAniDbImportService, IAsyncDisposable
{
    private readonly IVideoCatalogRepository _repository;
    private readonly IAniDbCatalogStore _store;
    private readonly IAniDbConfigurationProvider _configuration;
    private readonly IAniDbEd2kHasher _hasher;
    private readonly IAniDbUdpClient _udp;
    private readonly IAniDbHttpClient _http;
    private readonly VideoPlaybackHistoryStore _history;
    private readonly ILogger<AniDbImportService> _logger;
    private readonly ConcurrentDictionary<Guid, AniDbAssetIdentificationResult> _settledAssets = [];
    private readonly ConcurrentDictionary<int, AniDbEpisode> _udpEpisodeMetadata = [];
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _myListSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _udpMetadataFallbackGate = new(1, 1);
    private readonly SemaphoreSlim _scrapeResetGate = new(1, 1);
    private readonly object _activeImportGate = new();
    private readonly HashSet<TaskCompletionSource<bool>> _activeImports = [];
    private CancellationTokenSource _scrapeReset = new();
    private long _scrapeGeneration;
    private int _scrapeResetInProgress;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    public AniDbImportService(
        IVideoCatalogRepository repository,
        IAniDbCatalogStore store,
        IAniDbConfigurationProvider configuration,
        IAniDbEd2kHasher hasher,
        IAniDbUdpClient udp,
        IAniDbHttpClient http,
        VideoPlaybackHistoryStore history,
        ILogger<AniDbImportService> logger)
    {
        _repository = repository;
        _store = store;
        _configuration = configuration;
        _hasher = hasher;
        _udp = udp;
        _http = http;
        _history = history;
        _logger = logger;
        _udp.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
        _workers =
        [
            Task.Run(() => WorkAsync(_shutdown.Token)),
            Task.Run(() => WorkAsync(_shutdown.Token)),
            Task.Run(() => WorkMyListAsync(_shutdown.Token)),
        ];
    }

    public event EventHandler<AniDbClientStatus>? StatusChanged;
    public event EventHandler<AniDbAssetIdentificationSettledEventArgs>? AssetIdentificationSettled;
    public long ScrapeGeneration => Interlocked.Read(ref _scrapeGeneration);
    public AniDbClientStatus Status => _http.RetryAt is { } retryAt
        && (_udp.Status.RetryAt == null || retryAt > _udp.Status.RetryAt)
            ? new AniDbClientStatus(
                AniDbClientConnectionState.Banned,
                "AniDB HTTP requests are temporarily paused.",
                DateTimeOffset.UtcNow,
                retryAt)
            : _udp.Status;

    public AniDbScrapeAdmissionStamp CaptureScrapeAdmission()
    {
        // Read the generation first. If a reset begins between these reads, either the old
        // generation is retained or StartedDuringReset becomes true; neither can be admitted
        // into the replacement generation after the reset completes.
        var generation = ScrapeGeneration;
        var startedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        return new AniDbScrapeAdmissionStamp(generation, startedDuringReset);
    }

    public Task QueueSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        return QueueSourceAsync(sourceId, CaptureScrapeAdmission(), ct);
    }

    public Task QueueSourceAsync(
        Guid sourceId,
        long expectedScrapeGeneration,
        CancellationToken ct = default)
    {
        var requestedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return Task.CompletedTask;
        return QueueSourceCoreAsync(
            sourceId, expectedScrapeGeneration, requestedDuringReset, ct);
    }

    public Task QueueSourceAsync(
        Guid sourceId,
        AniDbScrapeAdmissionStamp admission,
        CancellationToken ct = default)
    {
        var requestedDuringReset = admission.StartedDuringReset
                                   || Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return Task.CompletedTask;
        return QueueSourceCoreAsync(
            sourceId, admission.Generation, requestedDuringReset, ct);
    }

    private async Task QueueSourceCoreAsync(
        Guid sourceId,
        long? expectedScrapeGeneration,
        bool requestedDuringReset,
        CancellationToken ct)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            if (requestedDuringReset
                || expectedScrapeGeneration.HasValue
                && expectedScrapeGeneration.Value != ScrapeGeneration)
                return;
            var configuration = await _configuration.GetAsync(ct);
            if (configuration is not { HashMatchingEnabled: true }) return;
            var snapshot = await _repository.GetSnapshotAsync(ct);
            var assetIds = snapshot.Assets.Where(asset =>
                    asset.Kind == VideoMediaAssetKind.LocalFile
                    && asset.Availability == VideoMediaAvailability.Available
                    && !asset.CatalogResetPending
                    && asset.SourceIds.Contains(sourceId))
                .Select(asset => asset.Id)
                .ToArray();
            foreach (var assetId in assetIds)
            {
                await _store.EnqueueImportJobAsync(assetId, ct);
                _signal.Release();
            }
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public Task QueueAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        var expectedScrapeGeneration = ScrapeGeneration;
        var requestedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return Task.CompletedTask;
        return QueueAssetAsync(assetId, expectedScrapeGeneration, ct);
    }

    internal async Task QueueAssetAsync(
        Guid assetId,
        long expectedScrapeGeneration,
        CancellationToken ct = default)
    {
        var requestedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return;
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            if (requestedDuringReset || expectedScrapeGeneration != ScrapeGeneration)
                return;
            if (await IsCatalogResetPendingAsync(assetId, ct))
                return;
            await QueueAssetCoreAsync(assetId, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    private async Task QueueAssetCoreAsync(Guid assetId, CancellationToken ct)
    {
        await _store.EnqueueImportJobAsync(assetId, ct);
        _signal.Release();
    }

    public async Task QueueMyListStateAsync(string identityKey, bool watched, CancellationToken ct = default)
    {
        var scrapeGeneration = ScrapeGeneration;
        var requestedDuringReset = Volatile.Read(ref _scrapeResetInProgress) != 0;
        if (requestedDuringReset)
            return;
        var configuration = await _configuration.GetAsync(ct);
        if (configuration is not { MyListSyncEnabled: true }
            || watched && !configuration.MyListSetWatched
            || !watched && !configuration.MyListSetUnwatched)
            return;
        var catalog = await _repository.GetSnapshotAsync(ct);
        var asset = catalog.Assets.FirstOrDefault(item =>
            item.IdentityKey.Equals(identityKey, StringComparison.OrdinalIgnoreCase));
        if (asset == null) return;
        var state = await _store.GetAssetAsync(asset.Id, ct);
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            if (requestedDuringReset || scrapeGeneration != ScrapeGeneration)
                return;
            if (state?.Ed2k == null)
            {
                if (asset.CatalogResetPending)
                    return;
                await QueueAssetCoreAsync(asset.Id, ct);
            }
            await _store.EnqueueMyListJobAsync(asset.Id, watched, ct);
            _myListSignal.Release();
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public async Task SyncMyListAsync(CancellationToken ct = default)
    {
        var configuration = await _configuration.GetAsync(ct);
        if (configuration is not { MyListSyncEnabled: true }) return;
        var remoteEntries = await _http.GetMyListAsync(ct);
        await _store.ReplaceRemoteMyListAsync(remoteEntries, DateTimeOffset.UtcNow, ct);
        var remoteByFile = remoteEntries
            .Where(entry => entry.FileId is > 0)
            .GroupBy(entry => entry.FileId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.UpdatedAt ?? DateTimeOffset.MinValue)
                    .ThenByDescending(entry => entry.MyListId ?? 0)
                    .First());
        var catalog = await _repository.GetSnapshotAsync(ct);
        var failures = new List<Exception>();
        foreach (var asset in (await _store.GetAssetsAsync(ct)).Where(item =>
                     item.FileMatch != null && !string.IsNullOrWhiteSpace(item.Ed2k)))
        {
            remoteByFile.TryGetValue(asset.FileMatch!.FileId, out var entry);
            try
            {
                await _store.UpsertMyListAsync(asset.AssetId, entry, null, ct);
                await ReconcileMyListStateAsync(configuration, catalog, asset, entry, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
                await _store.UpsertMyListAsync(asset.AssetId, asset.MyList, ex.Message, ct);
                _logger.LogWarning(ex, "AniDB MyList pull failed for {AssetId}", asset.AssetId);
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"AniDB MyList reconciliation failed for {failures.Count} local file(s).",
                failures[0]);
    }

    public async Task<bool> TestLoginAsync(CancellationToken ct = default)
    {
        if (!await _udp.TestLoginAsync(ct))
            return false;

        // A successful UDP AUTH only proves the account and UDP client identity.
        // Shoko also depends on the separately registered HTTP client for the
        // Anime XML that creates Series/Episode entities, so validate both here.
        // A user-triggered validation must make one real request even when this
        // process cached an earlier 302 for the same identity. Registration may
        // have been corrected on AniDB's side without changing local settings.
        var probe = await _http.ProbeAnimeAsync(1, ct);
        if (probe == null)
            throw new InvalidDataException("AniDB HTTP API validation returned no anime entity.");

        // Successful full-client validation is the activation boundary for a
        // corrected HTTP identity. Requeue every already FILE-matched asset so
        // both configuration-blocked jobs and legacy false-completed jobs resume
        // at the cached hash/release without touching the media file again.
        var generation = ScrapeGeneration;
        foreach (var asset in (await _store.GetAssetsAsync(ct)).Where(item => item.FileMatch != null))
            await QueueAssetAsync(asset.AssetId, generation, ct);
        return true;
    }

    public Task<AniDbReleaseState> GetReleaseStateAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => _store.GetReleaseStateAsync(ed2k, fileSize, ct);

    public async Task LinkManualReleaseAsync(
        string ed2k,
        long fileSize,
        AniDbManualReleaseLink link,
        CancellationToken ct = default)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            await _store.LinkManualReleaseAsync(ed2k, fileSize, link, ct);
            await QueueReleaseAssetsWithResetGateHeldAsync(ed2k, fileSize, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public async Task UnlinkReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            await _store.UnlinkReleaseAsync(ed2k, fileSize, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public async Task IgnoreReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            await _store.IgnoreReleaseAsync(ed2k, fileSize, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public async Task ClearReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            await _store.ClearReleaseAsync(ed2k, fileSize, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public async Task RescanReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default)
    {
        await _scrapeResetGate.WaitAsync(ct);
        try
        {
            await _store.ResetReleaseForRescanAsync(ed2k, fileSize, ct);
            await QueueReleaseAssetsWithResetGateHeldAsync(ed2k, fileSize, ct);
        }
        finally
        {
            _scrapeResetGate.Release();
        }
    }

    public Task ClearScrapingRecordsAsync(CancellationToken ct = default) =>
        ClearScrapingRecordsCoreAsync(synchronizedCleanup: null, ct);

    public Task ClearScrapingRecordsAsync(
        Func<IReadOnlyCollection<VideoManualAniDbIdentity>, CancellationToken, Task> synchronizedCleanup,
        CancellationToken ct = default) =>
        ClearScrapingRecordsCoreAsync(
            synchronizedCleanup ?? throw new ArgumentNullException(nameof(synchronizedCleanup)),
            ct);

    private async Task ClearScrapingRecordsCoreAsync(
        Func<IReadOnlyCollection<VideoManualAniDbIdentity>, CancellationToken, Task>? synchronizedCleanup,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _scrapeResetInProgress);
        var resetGateHeld = false;
        CancellationTokenSource? priorReset = null;
        try
        {
            await _scrapeResetGate.WaitAsync(ct);
            resetGateHeld = true;
            Interlocked.Increment(ref _scrapeGeneration);
            Task[] active;
            lock (_activeImportGate)
            {
                priorReset = _scrapeReset;
                _scrapeReset = new CancellationTokenSource();
                priorReset.Cancel();
                active = _activeImports.Select(item => item.Task).ToArray();
            }

            if (active.Length > 0)
                await Task.WhenAll(active).WaitAsync(ct);
            var manualCatalogIdentities = synchronizedCleanup == null
                ? Array.Empty<VideoManualAniDbIdentity>()
                : await _store.GetManualCatalogIdentitiesAsync(ct);
            if (synchronizedCleanup != null)
                await synchronizedCleanup(manualCatalogIdentities, ct);
            await _store.ClearScrapingRecordsAsync(ct);
            _settledAssets.Clear();
            _udpEpisodeMetadata.Clear();
        }
        finally
        {
            priorReset?.Dispose();
            if (resetGateHeld)
                _scrapeResetGate.Release();
            Interlocked.Decrement(ref _scrapeResetInProgress);
        }
    }

    private async Task WorkMyListAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AniDbMyListJob? job;
            try
            {
                job = await _store.ClaimMyListJobAsync(DateTimeOffset.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            if (job == null)
            {
                try
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wake.CancelAfter(TimeSpan.FromSeconds(30));
                    await _myListSignal.WaitAsync(wake.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Poll scheduled retries even when no new signal arrives.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            try
            {
                var configuration = await _configuration.GetAsync(ct)
                    ?? throw new InvalidOperationException("AniDB client configuration is incomplete.");
                var state = await _store.GetAssetAsync(job.AssetId, ct);
                if (state?.Ed2k == null)
                    throw new InvalidOperationException("AniDB hash is not ready for the MyList update.");
                var entry = await _udp.AddOrUpdateMyListAsync(
                    state.Ed2k,
                    state.FileSize,
                    configuration.DefaultMyListState,
                    job.Watched,
                    job.Watched ? DateTimeOffset.UtcNow : null,
                    ct);
                await _store.UpsertMyListAsync(job.AssetId, entry, null, ct);
                await _store.CompleteMyListJobAsync(job.AssetId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _store.RetryMyListJobAsync(
                    job.AssetId,
                    job.Attempts,
                    DateTimeOffset.UtcNow,
                    "Application shutdown interrupted the AniDB MyList update.",
                    terminal: false,
                    CancellationToken.None);
                break;
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                var providerRetryAt = _udp.Status.RetryAt is { } gate && gate > now
                    ? gate
                    : (DateTimeOffset?)null;
                var permanentClientConfigurationError =
                    ex is AniDbHttpApiException { IsClientConfigurationError: true };
                var attempts = providerRetryAt.HasValue ? job.Attempts : job.Attempts + 1;
                var retryAt = providerRetryAt ?? now.AddSeconds(
                    Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, job.Attempts))));
                var terminal = permanentClientConfigurationError
                               || !providerRetryAt.HasValue && attempts >= 8;
                await _store.RetryMyListJobAsync(
                    job.AssetId,
                    attempts,
                    retryAt,
                    ex.Message,
                    terminal,
                    CancellationToken.None);
                _logger.LogWarning(ex,
                    "AniDB MyList update failed for {AssetId}; attempt {Attempt}",
                    job.AssetId,
                    attempts);
            }
        }
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            AniDbImportJob? job;
            TaskCompletionSource<bool>? activity = null;
            CancellationToken resetToken = default;
            try
            {
                await _scrapeResetGate.WaitAsync(ct);
                try
                {
                    job = await _store.ClaimImportJobAsync(DateTimeOffset.UtcNow, ct);
                    if (job != null)
                    {
                        activity = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        lock (_activeImportGate)
                        {
                            _activeImports.Add(activity);
                            resetToken = _scrapeReset.Token;
                        }
                    }
                }
                finally
                {
                    _scrapeResetGate.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            if (job == null)
            {
                try
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wake.CancelAfter(TimeSpan.FromSeconds(30));
                    await _signal.WaitAsync(wake.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Periodically poll the persistent queue for scheduled retries.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, resetToken);
            try
            {
                await ImportAsync(job.AssetId, operation.Token);
                await _store.CompleteImportJobAsync(job.AssetId, operation.Token);
            }
            catch (OperationCanceledException) when (resetToken.IsCancellationRequested
                                                     && !ct.IsCancellationRequested)
            {
                // A global scrape reset owns the persistent cleanup. Do not turn the
                // cancelled old generation back into a retry after it has been drained.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _store.RetryImportJobAsync(
                    job.AssetId,
                    job.Stage,
                    job.Attempts,
                    DateTimeOffset.UtcNow,
                    "Application shutdown interrupted the AniDB import.",
                    terminal: false,
                    CancellationToken.None);
                break;
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                var persisted = (await _store.GetImportJobsAsync(CancellationToken.None))
                    .FirstOrDefault(item => item.AssetId == job.AssetId);
                var retryStage = persisted?.Stage ?? job.Stage;
                var providerRetryAt = new[] { _udp.Status.RetryAt, _http.RetryAt }
                    .Where(value => value.HasValue && value.Value > now)
                    .Max();
                // A provider-enforced gate is not an import failure. Keep the attempt
                // budget intact so a 12-hour HTTP ban cannot poison a persistent job.
                var permanentClientConfigurationError =
                    ex is AniDbHttpApiException { IsClientConfigurationError: true };
                var attempts = providerRetryAt.HasValue ? job.Attempts : job.Attempts + 1;
                var exponentialDelay = TimeSpan.FromSeconds(
                    Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, job.Attempts))));
                var retryAt = providerRetryAt ?? now.Add(exponentialDelay);
                var terminal = permanentClientConfigurationError
                               || !providerRetryAt.HasValue && attempts >= 8;
                await _store.RetryImportJobAsync(
                    job.AssetId,
                    retryStage,
                    attempts,
                    retryAt,
                    ex.Message,
                    terminal,
                    CancellationToken.None);
                _logger.LogWarning(ex,
                    "AniDB import failed for {AssetId}; attempt {Attempt} is {State}",
                    job.AssetId,
                    attempts,
                    terminal ? "terminal" : "scheduled for retry");
            }
            finally
            {
                if (activity != null)
                {
                    lock (_activeImportGate)
                        _activeImports.Remove(activity);
                    activity.TrySetResult(true);
                }
            }
        }
    }

    private async Task ImportAsync(Guid assetId, CancellationToken ct)
    {
        var configuration = await _configuration.GetAsync(ct)
            ?? throw new InvalidOperationException("AniDB client configuration is incomplete.");
        if (!configuration.HashMatchingEnabled)
            throw new InvalidOperationException("AniDB hash matching is disabled.");
        var catalog = await _repository.GetSnapshotAsync(ct);
        var asset = catalog.Assets.FirstOrDefault(item => item.Id == assetId);
        if (asset is not { Kind: VideoMediaAssetKind.LocalFile, Availability: VideoMediaAvailability.Available }
            || asset.CatalogResetPending
            || !File.Exists(asset.Location)) return;

        await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.Hashing, ct);
        var prior = await _store.GetAssetAsync(assetId, ct);
        AniDbEd2kHash hash;
        if (prior is { Ed2k: not null, FileSize: var size, ModifiedAt: var modified }
            && size == asset.FileSize && modified == asset.ModifiedAt
            && prior.Crc32 != null && prior.Md5 != null && prior.Sha1 != null)
            hash = new AniDbEd2kHash(prior.Ed2k, size, modified!.Value, prior.HashedAt ?? DateTimeOffset.UtcNow)
            {
                Crc32 = prior.Crc32,
                Md5 = prior.Md5,
                Sha1 = prior.Sha1,
            };
        else
        {
            hash = await _hasher.HashAsync(asset.Location, ct);
            await _store.UpsertHashAsync(asset.Id, asset.IdentityKey, hash, ct);
        }

        await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.FileLookup, ct);
        var releaseState = await _store.GetReleaseStateAsync(hash.Value, hash.FileSize, ct);
        if (!releaseState.IsAutomaticLookupDue(DateTimeOffset.UtcNow)
            && releaseState.Status is AniDbReleaseStatus.Unrecognized or AniDbReleaseStatus.Ignored)
        {
            PublishIdentificationSettled(assetId, AniDbAssetIdentificationResult.Unrecognized);
            return;
        }

        var match = releaseState.Match;
        if (releaseState.IsAutomaticLookupDue(DateTimeOffset.UtcNow))
        {
            var attemptStarted = DateTimeOffset.UtcNow;
            try
            {
                match = await _udp.GetFileAsync(hash.Value, hash.FileSize, ct);
                await _store.RecordMatchAttemptAsync(new AniDbReleaseMatchAttempt(
                    Guid.NewGuid(), assetId, "anidb", attemptStarted, DateTimeOffset.UtcNow,
                    match == null ? "unrecognized" : "matched", null)
                {
                    Ed2k = hash.Value,
                    FileSize = hash.FileSize,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _store.RecordMatchAttemptAsync(new AniDbReleaseMatchAttempt(
                    Guid.NewGuid(), assetId, "anidb", attemptStarted, DateTimeOffset.UtcNow,
                    "failed", ex.Message)
                {
                    Ed2k = hash.Value,
                    FileSize = hash.FileSize,
                }, CancellationToken.None);
                throw;
            }
        }
        await _store.UpsertFileMatchAsync(asset.Id, match, null, ct);
        if (match == null)
        {
            PublishIdentificationSettled(assetId, AniDbAssetIdentificationResult.Unrecognized);
            return;
        }

        await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.AnimeMetadata, ct);
        var anime = await GetAnimeGraphAsync(
            match.AnimeId, configuration.RelationDepth, configuration, ct);
        if (anime == null)
        {
            throw new InvalidDataException(
                $"AniDB anime {match.AnimeId} metadata was unavailable after the file was identified.");
        }
        match = await EnsureEpisodeOwnersAsync(
            match, configuration.RelationDepth, configuration, ct);
        await _store.UpsertFileMatchAsync(asset.Id, match, null, ct);
        await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.Grouping, ct);
        var group = await _store.MaterializeGroupAsync(anime.AnimeId, ct);
        await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.CatalogProjection, ct);
        await ProjectGroupAsync(group, ct);
        PublishIdentificationSettled(
            assetId,
            anime.IsDegraded
                ? AniDbAssetIdentificationResult.ProjectedDegraded
                : AniDbAssetIdentificationResult.ProjectedComplete);

        if (configuration.MyListSyncEnabled)
        {
            await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.MyList, ct);
            var entry = await _udp.GetMyListAsync(hash.Value, hash.FileSize, ct);
            if (entry == null && configuration.AutoAddToMyList)
                entry = await _udp.AddOrUpdateMyListAsync(hash.Value, hash.FileSize,
                    configuration.DefaultMyListState, false, null, ct);
            await _store.UpsertMyListAsync(asset.Id, entry, null, ct);
            var refreshedCatalog = await _repository.GetSnapshotAsync(ct);
            await ReconcileMyListStateAsync(configuration, refreshedCatalog,
                (await _store.GetAssetAsync(asset.Id, ct))!, entry, ct);
        }

        if (anime.IsDegraded)
        {
            // Reduced UDP metadata is useful immediately, but it does not contain
            // the complete Anime XML graph used by Shoko. Keep the durable job at
            // the explicit HTTP blocker so the UI never reports a full scrape and
            // a corrected registered identity can resume from the cached FILE hit.
            // MyList is an independent UDP capability, so allow it to complete
            // before recording the HTTP-only metadata blocker.
            await _store.AdvanceImportJobAsync(assetId, AniDbImportJobStage.AnimeMetadata, ct);
            throw new AniDbHttpApiException(302, "client version missing or invalid");
        }
    }

    private void PublishIdentificationSettled(
        Guid assetId,
        AniDbAssetIdentificationResult result)
    {
        var shouldPublish = false;
        _settledAssets.AddOrUpdate(
            assetId,
            _ =>
            {
                shouldPublish = true;
                return result;
            },
            (_, existing) =>
            {
                shouldPublish = existing != result;
                return result;
            });
        if (shouldPublish)
        {
            AssetIdentificationSettled?.Invoke(
                this,
                new AniDbAssetIdentificationSettledEventArgs(assetId, result));
        }
    }

    internal async Task ProjectGroupAsync(AniDbAnimeGroup group, CancellationToken ct)
    {
        var catalog = await _repository.GetSnapshotAsync(ct);
        var catalogAssets = catalog.Assets.ToDictionary(item => item.Id);
        var states = await _store.GetAssetsAsync(ct);
        var displaySeasonOrdinalsByGroup = new Dictionary<Guid, ImmutableDictionary<int, int>>();

        async Task<ImmutableDictionary<int, int>> GetDisplaySeasonOrdinalsAsync(
            AniDbAnimeGroup animeGroup)
        {
            if (displaySeasonOrdinalsByGroup.TryGetValue(animeGroup.GroupId, out var cached))
                return cached;

            var members = new List<(int AnimeId, int MemberIndex, DateOnly? StartDate)>();
            var memberIndex = 0;
            foreach (var animeId in animeGroup.AnimeIds.Distinct())
            {
                var member = await _store.GetAnimeAsync(animeId, ct);
                members.Add((
                    animeId,
                    memberIndex++,
                    DateOnly.TryParse(member?.StartDate, out var startDate) ? startDate : null));
            }

            // AnimeIds is already a persisted, stable group-member sequence. Known
            // start dates improve sequel ordering; the main AID and persisted member
            // position provide deterministic fallbacks when dates are absent/equal.
            var ordinals = members
                .OrderBy(member => member.StartDate ?? DateOnly.MaxValue)
                .ThenBy(member => member.AnimeId == animeGroup.MainAnimeId ? 0 : 1)
                .ThenBy(member => member.MemberIndex)
                .ThenBy(member => member.AnimeId)
                .Select((member, index) => (member.AnimeId, SeasonNumber: index + 1))
                .ToImmutableDictionary(member => member.AnimeId, member => member.SeasonNumber);
            displaySeasonOrdinalsByGroup[animeGroup.GroupId] = ordinals;
            return ordinals;
        }

        var groupDisplaySeasonOrdinals = await GetDisplaySeasonOrdinalsAsync(group);
        foreach (var state in states.Where(item =>
                     item.FileMatch != null
                     && group.AnimeIds.Contains(item.FileMatch.AnimeId)
                     && catalogAssets.TryGetValue(item.AssetId, out var asset)
                     && !asset.CatalogResetPending))
        {
            var match = state.FileMatch!;
            var anime = await _store.GetAnimeAsync(match.AnimeId, ct);
            if (anime == null)
                continue;
            var animeDisplaySeason = groupDisplaySeasonOrdinals.GetValueOrDefault(
                anime.AnimeId,
                1);
            var projections = ImmutableArray.CreateBuilder<VideoAniDbEpisodeProjection>();
            foreach (var link in match.Episodes.OrderBy(item => item.Ordinal))
            {
                var owner = await _store.GetAnimeByEpisodeAsync(link.EpisodeId, ct);
                if (owner == null)
                    continue;
                var episode = (owner.Episodes.IsDefault ? [] : owner.Episodes)
                    .FirstOrDefault(item => item.EpisodeId == link.EpisodeId);
                if (episode == null)
                    continue;
                var ownerGroup = owner.AnimeId == group.MainAnimeId || group.AnimeIds.Contains(owner.AnimeId)
                    ? group
                    : await _store.MaterializeGroupAsync(owner.AnimeId, ct);
                var ownerDisplaySeasons = await GetDisplaySeasonOrdinalsAsync(ownerGroup);
                var ownerDisplaySeason = ownerDisplaySeasons.GetValueOrDefault(owner.AnimeId, 1);
                var titles = episode.Titles.IsDefault ? [] : episode.Titles;
                var title = titles.FirstOrDefault(value => value.Language == "en")?.Value
                            ?? titles.FirstOrDefault(value => value.Language == "x-jat")?.Value
                            ?? titles.FirstOrDefault()?.Value
                            ?? episode.RawNumber;
                var originalTitle = titles.FirstOrDefault(value => value.Language == "ja")?.Value;
                projections.Add(new VideoAniDbEpisodeProjection(
                    episode.EpisodeId,
                    episode.Type == AniDbEpisodeType.Regular ? ownerDisplaySeason : 0,
                    episode.Number,
                    title,
                    originalTitle,
                    episode.Overview,
                    link.Ordinal,
                    link.Percentage,
                    link.IsOther,
                    DateOnly.TryParse(episode.AirDate, out var airDate) ? airDate : null)
                {
                    AnimeId = owner.AnimeId,
                    AnimeGroupId = ownerGroup.GroupId.ToString("D"),
                    AnimeMetadata = ToDisplaySeasonDetails(owner, ownerDisplaySeason),
                });
            }
            await _repository.ApplyAniDbIdentityAsync(
                state.AssetId,
                new VideoAniDbIdentityProjection(
                    anime.AnimeId,
                    match.FileId,
                    group.GroupId.ToString("D"),
                    ToDisplaySeasonDetails(anime, animeDisplaySeason),
                    projections.ToImmutable()),
                ct);
        }
    }

    private static VideoMetadataDetails ToDisplaySeasonDetails(
        AniDbAnime anime,
        int displaySeasonNumber)
    {
        var details = ToDetails(anime);
        var seasonNumber = Math.Max(1, displaySeasonNumber);
        return details with
        {
            SeasonNumber = seasonNumber,
            Seasons = details.Seasons
                .Select(season => season.SeasonNumber == 0
                    ? season
                    : season with
                    {
                        SeasonNumber = seasonNumber,
                        Title = anime.Title,
                    })
                .ToImmutableArray(),
        };
    }

    private async Task<AniDbFileMatch> EnsureEpisodeOwnersAsync(
        AniDbFileMatch match,
        int relationDepth,
        AniDbClientConfiguration configuration,
        CancellationToken ct)
    {
        var enriched = ImmutableArray.CreateBuilder<AniDbFileEpisodeLink>(match.Episodes.Length);
        foreach (var link in match.Episodes.OrderBy(item => item.Ordinal))
        {
            var owner = await _store.GetAnimeByEpisodeAsync(link.EpisodeId, ct);
            if (owner == null)
            {
                if (link.AnimeId > 0)
                    owner = await GetAnimeGraphAsync(
                        link.AnimeId, relationDepth, configuration, ct);
                else
                {
                    var episode = await GetUdpEpisodeMetadataAsync(link.EpisodeId, ct);
                    if (episode != null)
                    {
                        owner = await GetAnimeGraphAsync(
                            episode.AnimeId, relationDepth, configuration, ct);
                        var ownerEpisodes = owner == null || owner.Episodes.IsDefault
                            ? []
                            : owner.Episodes;
                        if (owner != null
                            && !ownerEpisodes.Any(item => item.EpisodeId == episode.EpisodeId))
                        {
                            owner = owner with
                            {
                                Episodes = ownerEpisodes
                                    .Add(episode)
                                    .OrderBy(item => item.Type)
                                    .ThenBy(item => item.Number)
                                    .ThenBy(item => item.EpisodeId)
                                    .ToImmutableArray(),
                            };
                            await _store.UpsertAnimeAsync(owner, ct);
                        }
                    }
                }
            }
            enriched.Add(link with
            {
                AnimeId = owner?.AnimeId ?? (link.AnimeId > 0 ? link.AnimeId : match.AnimeId),
            });
        }
        return match with { Episodes = enriched.ToImmutable() };
    }

    private async Task QueueReleaseAssetsWithResetGateHeldAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct)
    {
        var normalized = ed2k.Trim();
        foreach (var asset in (await _store.GetAssetsAsync(ct)).Where(item =>
                     item.FileSize == fileSize
                     && item.Ed2k != null
                     && item.Ed2k.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            await QueueAssetCoreAsync(asset.AssetId, ct);
    }

    private async Task<bool> IsCatalogResetPendingAsync(Guid assetId, CancellationToken ct)
    {
        var catalog = await _repository.GetSnapshotAsync(ct);
        return catalog.Assets.FirstOrDefault(item => item.Id == assetId)?.CatalogResetPending == true;
    }

    private async Task ReconcileMyListStateAsync(
        AniDbClientConfiguration configuration,
        VideoCatalogSnapshot catalog,
        AniDbAssetSnapshot asset,
        AniDbMyListEntry? remote,
        CancellationToken ct)
    {
        var localAsset = catalog.Assets.FirstOrDefault(item => item.Id == asset.AssetId);
        if (localAsset == null)
            return;
        var local = await _history.GetAsync(localAsset.IdentityKey, ct);
        if (remote == null)
        {
            if (configuration.AutoAddToMyList)
            {
                await _store.EnqueueMyListJobAsync(asset.AssetId, local.IsFinished, ct);
                _myListSignal.Release();
            }
            return;
        }

        // Preserve Shoko's conflict order: importing remote state wins when enabled;
        // only otherwise is the local state sent back to AniDB.
        if (configuration.MyListReadWatched && remote.Watched && !local.IsFinished)
        {
            await _history.MarkWatchedAsync(
                localAsset.IdentityKey,
                remote.WatchedAt ?? remote.UpdatedAt ?? DateTimeOffset.UtcNow,
                ct);
        }
        else if (configuration.MyListReadUnwatched && !remote.Watched && local.IsFinished)
        {
            await _history.ClearProgressAsync(localAsset.IdentityKey, ct);
        }
        else if (configuration.MyListSetUnwatched && remote.Watched && !local.IsFinished)
        {
            await _store.EnqueueMyListJobAsync(asset.AssetId, false, ct);
            _myListSignal.Release();
        }
        else if (configuration.MyListSetWatched && !remote.Watched && local.IsFinished)
        {
            await _store.EnqueueMyListJobAsync(asset.AssetId, true, ct);
            _myListSignal.Release();
        }
    }

    private async Task<AniDbAnime?> GetAnimeGraphAsync(
        int animeId,
        int depth,
        AniDbClientConfiguration configuration,
        CancellationToken ct)
    {
        var visited = new HashSet<int>();
        return await VisitAsync(animeId, depth);
        async Task<AniDbAnime?> VisitAsync(int aid, int remaining)
        {
            if (!visited.Add(aid)) return await _store.GetAnimeAsync(aid, ct);
            var cached = await _store.GetAnimeAsync(aid, ct);
            AniDbAnime? anime;
            if (cached is { IsDegraded: false, ExpiresAt: var expiry }
                && expiry > DateTimeOffset.UtcNow)
            {
                anime = cached;
            }
            else if (!configuration.HasExplicitHttpClientIdentity)
            {
                // HTTP and UDP client registrations are separate in AniDB. When
                // the dedicated HTTP identity is absent, do not stall every file
                // behind three doomed HTTP attempts; project the authenticated UDP
                // entity immediately and leave the durable job at the HTTP blocker.
                anime = cached is { IsDegraded: false }
                    ? cached
                    : await GetUdpMetadataFallbackAsync(aid, ct);
            }
            else
            {
                try
                {
                    anime = await _http.GetAnimeAsync(aid, ct);
                }
                catch (AniDbHttpApiException ex) when (ex.IsClientConfigurationError)
                {
                    // The HTTP and UDP APIs have separately registered client identities.
                    // Keep the FILE/AID/EID result useful while the HTTP registration is
                    // corrected by building a bounded, cacheable entity from authenticated
                    // UDP ANIME plus only the EPISODE ids present in the local library.
                    // This never turns a title match into identity and never enumerates AniDB.
                    anime = cached is { IsDegraded: false }
                        ? cached
                        : await GetUdpMetadataFallbackAsync(aid, ct);
                    if (anime != null)
                    {
                        _logger.LogWarning(
                            "AniDB HTTP client configuration was rejected; using reduced UDP metadata for anime {AnimeId}",
                            aid);
                    }
                }
            }
            if (anime == null) return null;
            await _store.UpsertAnimeAsync(anime, ct);
            if (remaining > 0)
                foreach (var relation in anime.Relations)
                    await VisitAsync(relation.RelatedAnimeId, remaining - 1);
            return anime;
        }
    }

    private async Task<AniDbAnime?> GetUdpMetadataFallbackAsync(
        int animeId,
        CancellationToken ct)
    {
        await _udpMetadataFallbackGate.WaitAsync(ct);
        try
        {
            var cached = await _store.GetAnimeAsync(animeId, ct);
            if (cached is { IsDegraded: false })
                return cached;

            var anime = cached ?? await _udp.GetAnimeMetadataAsync(animeId, ct);
            if (anime == null)
                return null;
            var episodeIds = (await _store.GetAssetsAsync(ct))
                .Where(asset => asset.FileMatch?.AnimeId == animeId)
                .SelectMany(asset => asset.FileMatch!.Episodes.IsDefault
                    ? []
                    : asset.FileMatch.Episodes.Where(link =>
                        link.AnimeId <= 0 || link.AnimeId == animeId))
                .Select(link => link.EpisodeId)
                .Where(episodeId => episodeId > 0)
                .Distinct()
                .OrderBy(episodeId => episodeId)
                .ToArray();
            var episodes = ImmutableArray.CreateBuilder<AniDbEpisode>();
            if (cached?.Episodes.IsDefaultOrEmpty == false)
                episodes.AddRange(cached.Episodes);
            var existingEpisodeIds = episodes.Select(episode => episode.EpisodeId).ToHashSet();
            foreach (var episodeId in episodeIds)
            {
                if (existingEpisodeIds.Contains(episodeId))
                    continue;
                var episode = await GetUdpEpisodeMetadataAsync(episodeId, ct);
                if (episode?.AnimeId == animeId)
                {
                    episodes.Add(episode);
                    existingEpisodeIds.Add(episodeId);
                }
            }
            var reduced = anime with
            {
                IsDegraded = true,
                Episodes = episodes
                    .OrderBy(episode => episode.Type)
                    .ThenBy(episode => episode.Number)
                    .ThenBy(episode => episode.EpisodeId)
                    .ToImmutableArray(),
            };
            await _store.UpsertAnimeAsync(reduced, ct);
            return reduced;
        }
        finally
        {
            _udpMetadataFallbackGate.Release();
        }
    }

    private async Task<AniDbEpisode?> GetUdpEpisodeMetadataAsync(
        int episodeId,
        CancellationToken ct)
    {
        if (_udpEpisodeMetadata.TryGetValue(episodeId, out var cached))
            return cached;
        var episode = await _udp.GetEpisodeMetadataAsync(episodeId, ct);
        if (episode != null)
            _udpEpisodeMetadata.TryAdd(episodeId, episode);
        return episode;
    }

    internal static VideoMetadataDetails ToDetails(AniDbAnime anime)
    {
        var tags = anime.Tags.IsDefault ? [] : anime.Tags;
        var creators = anime.Creators.IsDefault ? [] : anime.Creators;
        var characters = anime.Characters.IsDefault ? [] : anime.Characters;
        var resources = anime.Resources.IsDefault ? [] : anime.Resources;
        var relations = anime.Relations.IsDefault ? [] : anime.Relations;
        var similar = anime.SimilarAnime.IsDefault ? [] : anime.SimilarAnime;
        var externalIds = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        externalIds["anidb"] = anime.AnimeId.ToString();
        foreach (var resource in resources)
        {
            var provider = resource.Type switch
            {
                1 => "ann",
                2 => "mal",
                8 => "syoboi",
                9 => "allcinema",
                10 => "anison",
                11 => "dotlain",
                14 => "vndb",
                28 => "crunchyroll",
                32 => "amazon",
                34 => "funimation",
                38 => "bangumi",
                42 => "hidive",
                _ => null,
            };
            if (provider != null && !externalIds.ContainsKey(provider))
                externalIds[provider] = resource.Identifier;
        }
        var seasons = anime.Episodes.GroupBy(ep => ep.Type == AniDbEpisodeType.Regular ? 1 : 0)
            .OrderBy(group => group.Key).Select(group => new VideoMetadataSeason(
                group.Key, group.Key == 0 ? "Specials" : "Season 1", null, anime.StartDate,
                group.Count(), null, group.OrderBy(ep => ep.Type).ThenBy(ep => ep.Number)
                    .ThenBy(ep => ep.EpisodeId).Select(ep => new VideoMetadataEpisode(
                    ep.Number, ep.Titles.FirstOrDefault(title => title.Language == "en")?.Value
                               ?? ep.Titles.FirstOrDefault()?.Value ?? ep.RawNumber,
                    ep.Titles.FirstOrDefault(title => title.Language == "ja")?.Value,
                    ep.Overview, ep.AirDate, ep.RuntimeMinutes, null,
                    $"https://anidb.net/episode/{ep.EpisodeId}",
                    ep.RawNumber)).ToImmutableArray())).ToImmutableArray();
        var voiceActors = characters.SelectMany(character => character.VoiceActors.IsDefault
                ? [] : character.VoiceActors.Select(actor => (Character: character, Actor: actor)))
            .GroupBy(item => item.Actor.CreatorId)
            .Select(group => group.First())
            .ToArray();
        var people = creators.Select(creator => new VideoPersonCredit(
                creator.CreatorId.ToString(), creator.Name, creator.Role, "Creator", null))
            .Concat(voiceActors.Select(item => new VideoPersonCredit(
                item.Actor.CreatorId.ToString(), item.Actor.Name, item.Character.Name, "Actor",
                AniDbTitleIndexProvider.AniDbImageUrl(item.Actor.Picture))))
            .ToImmutableArray();
        var related = relations.Select(relation => new VideoRelatedItem(
                "anidb", relation.RelatedAnimeId.ToString(), relation.Title ?? $"AniDB {relation.RelatedAnimeId}",
                null, null, null, null, $"https://anidb.net/anime/{relation.RelatedAnimeId}"))
            .Concat(similar.Select(item => new VideoRelatedItem(
                "anidb", item.AnimeId.ToString(), $"AniDB {item.AnimeId}",
                null, null, null, null, $"https://anidb.net/anime/{item.AnimeId}")))
            .GroupBy(item => item.ProviderItemId)
            .Select(group => group.First())
            .ToImmutableArray();
        return new VideoMetadataDetails(
            "anidb", anime.AnimeId.ToString(), VideoMetadataMediaKind.Anime, anime.Title,
            anime.OriginalTitle, null, anime.Overview, Year(anime.StartDate), 1, null, null,
            anime.Titles.Select(title => title.Value).Distinct().ToImmutableArray(),
            tags.Where(tag => tag.Weight >= 200 && !tag.GlobalSpoiler).Select(tag => tag.Name).ToImmutableArray(),
            voiceActors.Select(item => item.Actor.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            externalIds.ToImmutable(),
            anime.Url ?? $"https://anidb.net/anime/{anime.AnimeId}", anime.FetchedAt, anime.ExpiresAt,
            CommunityRating: anime.Rating,
            EndYear: Year(anime.EndDate),
            Status: string.IsNullOrWhiteSpace(anime.EndDate) ? null : "Ended",
            Tags: tags.Where(tag => !tag.GlobalSpoiler).Select(tag => tag.Name).ToImmutableArray(),
            Studios: creators.Where(creator => creator.Role.Contains("Animation Work", StringComparison.OrdinalIgnoreCase))
                .Select(creator => creator.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            People: people,
            RelatedItems: related,
            Seasons: seasons).WithInitializedCollections();
    }

    private static int? Year(string? value) => DateOnly.TryParse(value, out var date) ? date.Year : null;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { }
        _signal.Dispose();
        _myListSignal.Dispose();
        _udpMetadataFallbackGate.Dispose();
        _scrapeReset.Dispose();
        _scrapeResetGate.Dispose();
        _shutdown.Dispose();
    }
}
