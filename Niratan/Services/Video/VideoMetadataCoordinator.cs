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
    string? Error = null);

public interface IVideoMetadataCoordinator
{
    event EventHandler<VideoMetadataRefreshProgress>? ProgressChanged;
    event EventHandler<VideoMetadataBatchProgress>? BatchProgressChanged;
    IReadOnlyCollection<VideoMetadataBatchProgress> ActiveBatchProgress { get; }
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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeBatches = [];
    private readonly ConcurrentDictionary<Guid, VideoMetadataBatchProgress> _batchProgress = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _artworkDownloadGates =
        new(StringComparer.Ordinal);

    public event EventHandler<VideoMetadataRefreshProgress>? ProgressChanged;
    public event EventHandler<VideoMetadataBatchProgress>? BatchProgressChanged;
    public IReadOnlyCollection<VideoMetadataBatchProgress> ActiveBatchProgress => _batchProgress.Values.ToArray();

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
        IVideoArtworkCache? artworkCache)
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
    }

    public async Task<VideoMetadataRefreshResult> RefreshAssetAsync(
        Guid assetId,
        bool allowNetwork,
        CancellationToken ct = default)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var asset = snapshot.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new KeyNotFoundException("Video asset was not found.");
        if (IsAudio(asset.Location))
            return new VideoMetadataRefreshResult(assetId, false, false, null, null, []);

        var nodes = snapshot.Nodes.Where(node => asset.NodeIds.Contains(node.Id)).ToList();
        var route = ResolveRoute(snapshot, asset, nodes);
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
        var externalIds = identityNode?.ExternalIds ?? ImmutableDictionary<string, string>.Empty;
        var query = new VideoMetadataSearchQuery(
            identityNode?.PrimaryTitle ?? asset.Title,
            route.MediaKind.Value,
            identityNode?.Year,
            primaryNode?.SeasonNumber,
            primaryNode?.EpisodeNumber ?? asset.EpisodeStart,
            primaryNode?.AbsoluteEpisodeNumber,
            route.Language,
            route.Region,
            externalIds);
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
        var parsed = ToParsedIdentity(asset, primaryNode, externalIds) with
        {
            NormalizedTitle = identityNode?.PrimaryTitle ?? asset.Title,
            Year = identityNode?.Year ?? primaryNode?.Year,
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

        var primaryCandidate = SelectPrimaryDetailsCandidate(
            route.MediaKind.Value, accepted, scored);

        VideoMetadataDetails? details = null;
        if (_details.TryGetValue(primaryCandidate.ProviderId, out var detailsProvider))
        {
            Publish(assetId, VideoMetadataRefreshStage.Details, 0, 1, primaryCandidate.ProviderId);
            try
            {
                details = await detailsProvider.GetDetailsAsync(
                    primaryCandidate,
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
        await _repository.ApplyMetadataMatchAsync(
            assetId,
            primaryCandidate,
            details,
            accepted.IsIdentityLocked,
            ct);
        Publish(assetId, VideoMetadataRefreshStage.Artwork, 0, 1, primaryCandidate.ProviderId);
        try
        {
            await RefreshArtworkAsync(assetId, primaryCandidate, details, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video artwork refresh failed for {ProviderId}", primaryCandidate.ProviderId);
            errors.Add($"{primaryCandidate.ProviderId} artwork: {ex.Message}");
        }
        Publish(assetId, VideoMetadataRefreshStage.Completed, 1, 1, primaryCandidate.ProviderId);
        return new VideoMetadataRefreshResult(
            assetId,
            true,
            false,
            primaryCandidate.ProviderId,
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors),
            scored);
    }

    internal static VideoMetadataCandidate SelectPrimaryDetailsCandidate(
        VideoMetadataMediaKind routeKind,
        VideoMetadataMatchScore accepted,
        IReadOnlyList<VideoMetadataMatchScore> scored)
    {
        if (accepted.IsIdentityLocked || routeKind != VideoMetadataMediaKind.Anime)
            return accepted.Candidate;
        var detailPriority = new[] { "tmdb", "anilist", "bangumi", "tvmaze" };
        var richCandidate = scored
            .Where(result => detailPriority.Contains(
                                 result.Candidate.ProviderId,
                                 StringComparer.OrdinalIgnoreCase)
                             && !result.HasHardConflict
                             && result.TitleScore >= 0.999
                             && (!accepted.Candidate.Year.HasValue
                                 || !result.Candidate.Year.HasValue
                                 || accepted.Candidate.Year == result.Candidate.Year))
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
        var snapshot = await _repository.GetSnapshotAsync(ct);
        foreach (var source in snapshot.Sources)
        {
            ct.ThrowIfCancellationRequested();
            await QueueSourceRefreshAsync(source.Id, forceRefresh, ct);
        }
    }

    public async Task QueueSourceRefreshAsync(
        Guid sourceId,
        bool forceRefresh = false,
        CancellationToken ct = default)
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
        var jobId = await _repository.BeginMetadataRefreshAsync(sourceId, assets.Length, ct);
        var batchCts = new CancellationTokenSource();
        if (!_activeBatches.TryAdd(sourceId, batchCts))
        {
            batchCts.Dispose();
            await _repository.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Cancelled, 0, "Superseded by an active metadata refresh.", ct);
            return;
        }
        var initial = new VideoMetadataBatchProgress(
            jobId, sourceId, VideoCatalogJobState.Running, 0, assets.Length, 0, 0, null);
        PublishBatch(initial);
        _ = RunBatchAsync(initial, assets, batchCts);
    }

    public async Task CancelSourceRefreshAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (_activeBatches.TryGetValue(sourceId, out var active))
            active.Cancel();
        if (_batchProgress.TryGetValue(sourceId, out var progress))
        {
            await _repository.UpdateMetadataRefreshAsync(
                progress.JobId, VideoCatalogJobState.Cancelled, progress.ProcessedCount, progress.Error, ct);
        }
    }

    private async Task RunBatchAsync(
        VideoMetadataBatchProgress initial,
        IReadOnlyList<VideoCatalogAssetSnapshot> assets,
        CancellationTokenSource batchCts)
    {
        var processed = 0;
        var matched = 0;
        var needsReview = 0;
        var errors = new ConcurrentQueue<string>();
        try
        {
            await Parallel.ForEachAsync(
                assets,
                new ParallelOptions
                {
                    CancellationToken = batchCts.Token,
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
                            errors.Enqueue(result.Error);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Enqueue(ex.Message);
                    }
                    var current = Interlocked.Increment(ref processed);
                    var progress = initial with
                    {
                        ProcessedCount = current,
                        MatchedCount = Volatile.Read(ref matched),
                        NeedsReviewCount = Volatile.Read(ref needsReview),
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
                });
            var completed = initial with
            {
                State = VideoCatalogJobState.Completed,
                ProcessedCount = processed,
                MatchedCount = matched,
                NeedsReviewCount = needsReview,
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, completed.State, processed, completed.Error, CancellationToken.None);
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
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, cancelled.State, processed, cancelled.Error, CancellationToken.None);
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
                CurrentAssetId = null,
                Error = CompactErrors(errors),
            };
            await _repository.UpdateMetadataRefreshAsync(
                initial.JobId, failed.State, processed, failed.Error, CancellationToken.None);
            PublishBatch(failed);
        }
        finally
        {
            if (_activeBatches.TryRemove(initial.SourceId, out var active))
                active.Dispose();
        }
    }

    private static bool NeedsMetadata(
        VideoCatalogAssetSnapshot asset,
        IReadOnlyDictionary<Guid, VideoCatalogNodeSnapshot> nodesById,
        DateTimeOffset? lastCompletedRefresh)
    {
        var nodes = asset.NodeIds
            .Select(id => nodesById.GetValueOrDefault(id))
            .Where(node => node != null)
            .Select(node => node!)
            .ToArray();
        var unmatched = nodes.Length == 0
                        || nodes.All(node => node.Kind == VideoCatalogNodeKind.Unmatched
                                             || node.ExternalIds.Count == 0);
        if (unmatched)
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

    private static string? CompactErrors(IEnumerable<string> errors)
    {
        var values = errors.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(5).ToArray();
        return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
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
            "bangumi" => metadata?.BangumiEnabled != false,
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
        if (_transport == null || _artworkCache == null
            || !_artworkProviders.TryGetValue(candidate.ProviderId, out var provider))
            return;
        var configured = _settings?.Current.VideoSettings.Metadata.ArtworkEnabled;
        if (configured != null
            && configured.TryGetValue(candidate.ProviderId, out var enabled)
            && !enabled)
            return;
        var primaryArtwork = (await provider.GetArtworkAsync(candidate, ct))
            .Where(item => item.Kind is "poster" or "backdrop" or "thumb" or "logo")
            .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var peopleArtwork = details?.People.IsDefault == false
            ? details.People
                .Where(person => !string.IsNullOrWhiteSpace(person.ImageUrl))
                .Take(10)
                .Select(person => new VideoArtworkCandidate(
                    candidate.ProviderId,
                    person.ImageUrl!,
                    $"person:{person.ProviderPersonId}",
                    null, null, null, person.ImageUrl))
            : [];
        var relatedArtwork = details?.RelatedItems.IsDefault == false
            ? details.RelatedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.PosterUrl))
                .Take(8)
                .Select(item => new VideoArtworkCandidate(
                    candidate.ProviderId,
                    item.PosterUrl!,
                    $"related:{item.ProviderId}:{item.ProviderItemId}:poster",
                    null, null, null, item.SourceUrl))
            : [];
        var artworkCandidates = primaryArtwork
            .Concat(peopleArtwork)
            .Concat(relatedArtwork)
            .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps)
            .DistinctBy(item => item.Url, StringComparer.Ordinal)
            .ToArray();
        await Parallel.ForEachAsync(
            artworkCandidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            async (artwork, itemToken) =>
                await CacheAndApplyArtworkAsync(assetId, candidate, artwork, itemToken));
    }

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
            var existing = await _artworkCache!.GetAsync(artwork.Url, ct);
            VideoArtworkCacheEntry cached;
            if (existing != null)
            {
                cached = existing;
            }
            else
            {
                var response = await _transport!.SendAsync(new VideoMetadataRequest(
                    owner.ProviderId,
                    HttpMethod.Get,
                    new Uri(artwork.Url),
                    IsIdempotent: false,
                    MaxResponseBytes: 20L * 1024 * 1024), ct);
                if (response.StatusCode is < 200 or >= 300)
                    return;
                await using var stream = new MemoryStream(response.Content, writable: false);
                cached = await _artworkCache.StoreAsync(
                    artwork.Url, stream, response.ContentType, response.ETag, response.LastModified, ct);
            }
            await _repository.ApplyArtworkAsync(
                assetId,
                owner.MediaKind,
                owner.ProviderId,
                artwork.Kind,
                artwork.Url,
                cached.LocalPath,
                cached.ETag,
                cached.LastModified,
                ct);
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
            ct);
    }

    private static (VideoMetadataMediaKind? MediaKind, string Language, string Region, ImmutableArray<string> ProviderIds) ResolveRoute(
        VideoCatalogSnapshot snapshot,
        VideoCatalogAssetSnapshot asset,
        IReadOnlyList<VideoCatalogNodeSnapshot> nodes)
    {
        var source = asset.SourceIds.Select(id => snapshot.Sources.FirstOrDefault(item => item.Id == id))
            .Where(item => item != null)
            .OrderByDescending(item => item!.FolderPath.Length)
            .ThenBy(item => item!.CreatedAt)
            .FirstOrDefault();
        var type = source?.MediaType ?? VideoLibraryMediaType.Auto;
        var kind = type switch
        {
            VideoLibraryMediaType.Movie => VideoMetadataMediaKind.Movie,
            VideoLibraryMediaType.Anime => VideoMetadataMediaKind.Anime,
            VideoLibraryMediaType.JapaneseDramaTv => VideoMetadataMediaKind.Series,
            _ when nodes.Any(node => node.AbsoluteEpisodeNumber.HasValue) => VideoMetadataMediaKind.Anime,
            _ when asset.EpisodeStart.HasValue || nodes.Any(node => node.EpisodeNumber.HasValue) => VideoMetadataMediaKind.Series,
            _ when nodes.Any(node => node.Year.HasValue) => VideoMetadataMediaKind.Movie,
            _ => (VideoMetadataMediaKind?)null,
        };
        var defaults = type switch
        {
            VideoLibraryMediaType.Anime => new[] { "anilist", "anidb", "bangumi", "tmdb" },
            VideoLibraryMediaType.JapaneseDramaTv => new[] { "tmdb", "tvmaze", "bangumi" },
            VideoLibraryMediaType.Movie => new[] { "tmdb", "bangumi" },
            _ when kind == VideoMetadataMediaKind.Anime => new[] { "anilist", "anidb", "bangumi", "tmdb" },
            _ when kind == VideoMetadataMediaKind.Series => new[] { "tmdb", "tvmaze", "bangumi" },
            _ when kind == VideoMetadataMediaKind.Movie => new[] { "tmdb", "bangumi" },
            _ => Array.Empty<string>(),
        };
        return (
            kind,
            source?.Language ?? "ja-JP",
            source?.Region ?? "JP",
            source is { ProviderOrder.Length: > 0 } ? source.ProviderOrder : defaults.ToImmutableArray());
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
