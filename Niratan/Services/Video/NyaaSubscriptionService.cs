using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;

namespace Niratan.Services.Video;

internal sealed partial class NyaaSubscriptionService : INyaaSubscriptionService, IDisposable
{
    private static readonly HashSet<string> IgnoredTitleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "anime", "batch", "bd", "bluray", "complete", "dub", "episode",
        "hevc", "movie", "raw", "season", "sub", "subs", "the", "tv", "web", "webdl",
        "x264", "x265", "aac", "flac", "480p", "576p", "720p", "1080p", "2160p",
    };

    private readonly IVideoResourceSearchService _resources;
    private readonly IVideoDiscoveryService _discovery;
    private readonly Lazy<INyaaDownloadManager> _downloadManager;
    private readonly IQbittorrentDownloadCoordinator _downloadCoordinator;
    private readonly ISettingsService _settings;
    private readonly ILogger<NyaaSubscriptionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _downloadManagerSync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(30));
    private readonly Dictionary<string, PendingBuiltInDownload> _pendingBuiltInDownloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeChecks =
        new(StringComparer.OrdinalIgnoreCase);
    private INyaaDownloadManager? _resolvedDownloadManager;
    private Task<INyaaDownloadManager>? _downloadManagerTask;
    private bool _downloadManagerSubscribed;
    private bool _disposed;

    public event EventHandler? SubscriptionsChanged;

    public NyaaSubscriptionService(
        IVideoResourceSearchService resources,
        IVideoDiscoveryService discovery,
        Lazy<INyaaDownloadManager> downloadManager,
        IQbittorrentDownloadCoordinator downloadCoordinator,
        ISettingsService settings,
        ILogger<NyaaSubscriptionService> logger)
    {
        _resources = resources;
        _discovery = discovery;
        _downloadManager = downloadManager;
        _downloadCoordinator = downloadCoordinator;
        _settings = settings;
        _logger = logger;
        _ = RunPeriodicChecksAsync(_lifetime.Token);
    }

    public IReadOnlyList<NyaaVideoSubscription> GetSubscriptions()
    {
        var discovery = _settings.Current.DiscoverySettings;
        var subscriptions = (discovery.NyaaSubscriptions ?? [])
            .Select(subscription => subscription.Clone())
            .ToList();
        var modernKeys = subscriptions
            .Select(subscription => subscription.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in (discovery.SubscribedVideoKeys ?? [])
                     .Where(key => !string.IsNullOrWhiteSpace(key) && !modernKeys.Contains(key)))
        {
            var separator = key.IndexOf(':');
            subscriptions.Add(new NyaaVideoSubscription
            {
                Key = key,
                ProviderId = separator > 0 ? key[..separator] : "legacy",
                ProviderItemId = separator > 0 && separator < key.Length - 1
                    ? key[(separator + 1)..]
                    : key,
                Title = key,
                Enabled = false,
                CreatedAt = DateTimeOffset.MinValue,
            });
        }

        return subscriptions
            .OrderByDescending(subscription => subscription.CreatedAt)
            .ThenBy(subscription => subscription.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool IsSubscribed(VideoMetadataCandidate identity)
    {
        var key = SubscriptionKey(identity);
        return (_settings.Current.DiscoverySettings.NyaaSubscriptions ?? [])
            .Any(subscription => subscription.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(
            identity,
            query,
            categoryCode,
            selectedRelease,
            ParseEpisode(selectedRelease.Title) ?? identity.EpisodeNumber,
            new NyaaSubscriptionArtwork(identity.PosterUrl),
            ct);

    public Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        int? startAfterEpisode,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(
            identity,
            query,
            categoryCode,
            selectedRelease,
            startAfterEpisode,
            new NyaaSubscriptionArtwork(identity.PosterUrl),
            ct);

    public Task<Result<int>> SubscribeAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        int? startAfterEpisode,
        NyaaSubscriptionArtwork? artwork,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(identity, query, categoryCode, selectedRelease, startAfterEpisode, artwork, ct);

    private async Task<Result<int>> SubscribeCoreAsync(
        VideoMetadataCandidate identity,
        string query,
        string categoryCode,
        NyaaTorrentItem selectedRelease,
        int? startAfterEpisode,
        NyaaSubscriptionArtwork? artwork,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(selectedRelease);

        if (selectedRelease.IsRemake || IsBatchTitle(selectedRelease.Title))
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionSingleOriginalRequired",
                    "Choose a single-episode original release. Batch and remake releases cannot define a subscription."),
                SubscriptionTitle());
        }

        var releaseGroup = ParseReleaseGroup(selectedRelease.Title);
        var resolution = ParseResolution(selectedRelease.Title);
        if (releaseGroup is null || resolution is null)
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionReleaseRuleRequired",
                    "Choose a Nyaa release whose title contains both a release group and a resolution (for example [Group] and 1080p)."),
                SubscriptionTitle());
        }

        if (startAfterEpisode < 0)
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionNegativeEpisode",
                    "The starting episode cannot be negative."),
                SubscriptionTitle());
        }

        if (identity.MediaKind is not VideoMetadataMediaKind.Movie && startAfterEpisode is null)
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionEpisodeRequired",
                    "Choose a Nyaa release whose title contains a recognizable episode number, or provide a starting episode."),
                SubscriptionTitle());
        }

        var subscription = new NyaaVideoSubscription
        {
            Key = SubscriptionKey(identity),
            ProviderId = identity.ProviderId,
            ProviderItemId = identity.ProviderItemId,
            MediaKind = identity.MediaKind,
            Title = identity.Title,
            OriginalTitle = identity.OriginalTitle,
            PosterUrl = NormalizePosterUrl(FirstNonBlank(artwork?.PosterUrl, identity.PosterUrl)),
            PosterPath = NormalizePosterPath(FirstNonBlank(artwork?.PosterPath)),
            Year = identity.Year,
            SeasonNumber = identity.SeasonNumber,
            StartAfterEpisode = startAfterEpisode,
            Aliases = (identity.Aliases.IsDefault ? [] : identity.Aliases).ToList(),
            ExternalIds = identity.ExternalIds.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            Query = string.IsNullOrWhiteSpace(query) ? _resources.BuildDefaultQuery(identity) : query.Trim(),
            CategoryCode = string.IsNullOrWhiteSpace(categoryCode) ? "0_0" : categoryCode,
            ReleaseGroup = releaseGroup,
            Resolution = resolution,
            RequireTrusted = selectedRelease.IsTrusted,
            Trusted = selectedRelease.IsTrusted,
            SelectedCategory = selectedRelease.Category,
            Enabled = true,
            DownloadBackend = _settings.Current.DownloadBackend,
            SeenItemIds = [],
            ProcessedLogicalItemKeys = [],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (!MatchesIdentity(selectedRelease, subscription))
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionIdentityMismatch",
                    "The selected Nyaa release does not match this title or season."),
                SubscriptionTitle());
        }

        CancelActiveCheck(subscription.Key);
        await _gate.WaitAsync(ct);
        try
        {
            var queued = await QueueInitialReleaseAsync(subscription, selectedRelease, ct);
            if (!queued.IsSuccess)
                return queued;

            var discovery = _settings.Current.DiscoverySettings.Clone();
            discovery.NyaaSubscriptions.RemoveAll(value =>
                value.Key.Equals(subscription.Key, StringComparison.OrdinalIgnoreCase));
            discovery.SubscribedVideoKeys.RemoveAll(value =>
                value.Equals(subscription.Key, StringComparison.OrdinalIgnoreCase));
            discovery.NyaaSubscriptions.Add(subscription);
            await SaveAsync(discovery);
            return queued;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<int>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create Nyaa subscription {SubscriptionKey}", subscription.Key);
            return Result<int>.Failure(ex.Message, SubscriptionTitle());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<int>> QueueInitialReleaseAsync(
        NyaaVideoSubscription subscription,
        NyaaTorrentItem selectedRelease,
        CancellationToken ct)
    {
        var logicalItemKey = LogicalItemKey(selectedRelease, subscription);
        if (logicalItemKey is null)
        {
            return Result<int>.Failure(
                ResourceStringHelper.GetString(
                    "NyaaSubscriptionEpisodeRequired",
                    "Choose a Nyaa release whose title contains a recognizable episode number, or provide a starting episode."),
                SubscriptionTitle());
        }

        if (subscription.DownloadBackend == DownloadBackendKind.MonoTorrent)
        {
            var manager = await GetDownloadManagerAsync(ct).ConfigureAwait(false);
            var existing = manager.GetTasks()
                .Where(task => task.Item.Id.Equals(selectedRelease.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            if (existing?.State == NyaaDownloadTaskState.Completed)
            {
                MarkReleaseProcessed(subscription, selectedRelease.Id, logicalItemKey);
                if (subscription.MediaKind == VideoMetadataMediaKind.Movie)
                    subscription.Enabled = false;
                return Result<int>.Success(0);
            }

            if (existing?.State is NyaaDownloadTaskState.Queued
                or NyaaDownloadTaskState.Downloading
                or NyaaDownloadTaskState.Paused
                or NyaaDownloadTaskState.Importing)
            {
                _pendingBuiltInDownloads[existing.TaskId] = new PendingBuiltInDownload(
                    subscription.Key,
                    selectedRelease.Id,
                    logicalItemKey);
                return Result<int>.Success(0);
            }

            var taskId = manager.Enqueue(selectedRelease);
            _pendingBuiltInDownloads[taskId] = new PendingBuiltInDownload(
                subscription.Key,
                selectedRelease.Id,
                logicalItemKey);
            return Result<int>.Success(1);
        }

        var added = await _downloadCoordinator.AddAsync(selectedRelease, ct);
        if (added.IsCancelled)
            return Result<int>.Cancelled();
        if (!added.IsSuccess)
        {
            return Result<int>.Failure(
                added.Error ?? ResourceStringHelper.GetString(
                    "NyaaSubscriptionQbRejected",
                    "qBittorrent did not accept the subscription release."),
                SubscriptionTitle());
        }

        MarkReleaseProcessed(subscription, selectedRelease.Id, logicalItemKey);
        if (subscription.MediaKind == VideoMetadataMediaKind.Movie)
            subscription.Enabled = false;
        return Result<int>.Success(1);
    }

    public Task UnsubscribeAsync(VideoMetadataCandidate identity, CancellationToken ct = default) =>
        RemoveAsync(SubscriptionKey(identity), ct);

    public async Task SetEnabledAsync(string key, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!enabled)
            CancelActiveCheck(key);
        await _gate.WaitAsync(ct);
        try
        {
            var discovery = _settings.Current.DiscoverySettings.Clone();
            var subscription = discovery.NyaaSubscriptions.FirstOrDefault(value =>
                value.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (subscription is null || subscription.Enabled == enabled)
                return;

            subscription.Enabled = enabled;
            if (enabled)
                subscription.LastError = null;
            await SaveAsync(discovery);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        CancelActiveCheck(key);
        await _gate.WaitAsync(ct);
        try
        {
            var discovery = _settings.Current.DiscoverySettings.Clone();
            var removed = discovery.NyaaSubscriptions.RemoveAll(subscription =>
                subscription.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
            removed |= discovery.SubscribedVideoKeys.RemoveAll(value =>
                value.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
            foreach (var taskId in _pendingBuiltInDownloads
                         .Where(pair => pair.Value.SubscriptionKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _pendingBuiltInDownloads.Remove(taskId);
            }

            if (removed)
                await SaveAsync(discovery);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshArtworkAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        string? posterUrl;
        await _gate.WaitAsync(ct);
        try
        {
            var subscription = (_settings.Current.DiscoverySettings.NyaaSubscriptions ?? [])
                .FirstOrDefault(value => value.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (subscription is null || HasUsablePosterPath(subscription.PosterPath))
                return;
            posterUrl = subscription.PosterUrl;
        }
        finally
        {
            _gate.Release();
        }

        var resolvedPath = await _discovery.ResolveArtworkAsync(posterUrl, ct);
        resolvedPath = NormalizePosterPath(resolvedPath);
        if (resolvedPath is null)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var discovery = _settings.Current.DiscoverySettings.Clone();
            var subscription = discovery.NyaaSubscriptions.FirstOrDefault(value =>
                value.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (subscription is null
                || !string.Equals(subscription.PosterUrl, posterUrl, StringComparison.Ordinal)
                || string.Equals(subscription.PosterPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            subscription.PosterPath = resolvedPath;
            await SaveAsync(discovery);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        var keys = (_settings.Current.DiscoverySettings.NyaaSubscriptions ?? [])
            .Where(subscription => subscription.Enabled)
            .Select(subscription => subscription.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            var result = await CheckOneAsync(key, ct);
            if (!result.IsSuccess && !result.IsCancelled)
                _logger.LogWarning("Nyaa subscription {SubscriptionKey} failed: {Error}", key, result.Error);
        }
    }

    public async Task<Result<int>> CheckOneAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Result<int>.Success(0);

        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _lifetime.Token);
        if (!_activeChecks.TryAdd(key, checkCancellation))
            return Result<int>.Success(0);

        try
        {
            return await CheckOneCoreAsync(key, checkCancellation.Token);
        }
        finally
        {
            _activeChecks.TryRemove(
                new KeyValuePair<string, CancellationTokenSource>(key, checkCancellation));
        }
    }

    private async Task<Result<int>> CheckOneCoreAsync(string key, CancellationToken ct)
    {

        NyaaVideoSubscription? checkingSubscription = null;
        await _gate.WaitAsync(ct);
        try
        {
            var discovery = _settings.Current.DiscoverySettings.Clone();
            var subscription = discovery.NyaaSubscriptions.FirstOrDefault(value =>
                value.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (subscription is null || !subscription.Enabled)
                return Result<int>.Success(0);
            checkingSubscription = subscription;
            if (subscription.MediaKind == VideoMetadataMediaKind.Movie
                && _pendingBuiltInDownloads.Values.Any(value =>
                    value.SubscriptionKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return Result<int>.Success(0);
            }

            var identity = ToIdentity(subscription);
            var search = await _resources.SearchAsync(
                new VideoResourceSearchRequest(identity, subscription.Query, subscription.CategoryCode),
                ct);
            if (search.IsCancelled)
                return Result<int>.Cancelled();
            if (!search.IsSuccess || search.Value is null)
            {
                return await SaveFailureAsync(
                    subscription,
                    search.Error ?? ResourceStringHelper.GetString(
                        "NyaaSubscriptionSearchFailed",
                        "Nyaa search failed."));
            }

            var seen = new HashSet<string>(subscription.SeenItemIds ?? [], StringComparer.OrdinalIgnoreCase);
            var processed = new HashSet<string>(
                subscription.ProcessedLogicalItemKeys ?? [],
                StringComparer.OrdinalIgnoreCase);
            var candidates = SelectCandidates(search.Value, subscription, seen, processed);
            var queued = 0;
            var movieCompleted = false;

            if (subscription.DownloadBackend == DownloadBackendKind.MonoTorrent)
            {
                var manager = await GetDownloadManagerAsync(ct).ConfigureAwait(false);
                var tasks = manager.GetTasks();
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = tasks
                        .Where(task => task.Item.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(task => task.UpdatedAt)
                        .FirstOrDefault();
                    if (existing?.State == NyaaDownloadTaskState.Completed)
                    {
                        seen.Add(candidate.Id);
                        var logicalItemKey = LogicalItemKey(candidate, subscription);
                        if (logicalItemKey is not null)
                            processed.Add(logicalItemKey);
                        movieCompleted |= subscription.MediaKind == VideoMetadataMediaKind.Movie;
                        continue;
                    }

                    if (existing?.State is NyaaDownloadTaskState.Queued
                        or NyaaDownloadTaskState.Downloading
                        or NyaaDownloadTaskState.Paused
                        or NyaaDownloadTaskState.Importing)
                    {
                        _pendingBuiltInDownloads[existing.TaskId] = new PendingBuiltInDownload(
                            subscription.Key,
                            candidate.Id,
                            LogicalItemKey(candidate, subscription));
                        continue;
                    }

                    var taskId = manager.Enqueue(candidate);
                    _pendingBuiltInDownloads[taskId] = new PendingBuiltInDownload(
                        subscription.Key,
                        candidate.Id,
                        LogicalItemKey(candidate, subscription));
                    queued++;
                }
            }
            else
            {
                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var added = await _downloadCoordinator.AddAsync(candidate, ct);
                    if (added.IsCancelled)
                        return Result<int>.Cancelled();
                    if (!added.IsSuccess)
                    {
                        subscription.SeenItemIds = seen.TakeLast(5000).ToList();
                        return await SaveFailureAsync(
                            subscription,
                            added.Error ?? ResourceStringHelper.GetString(
                                "NyaaSubscriptionQbRejected",
                                "qBittorrent did not accept the subscription release."));
                    }

                    seen.Add(candidate.Id);
                    var logicalItemKey = LogicalItemKey(candidate, subscription);
                    if (logicalItemKey is not null)
                        processed.Add(logicalItemKey);
                    queued++;
                    if (subscription.MediaKind == VideoMetadataMediaKind.Movie)
                    {
                        movieCompleted = true;
                        break;
                    }
                }
            }

            subscription.SeenItemIds = seen.TakeLast(5000).ToList();
            subscription.ProcessedLogicalItemKeys = processed
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .TakeLast(5000)
                .ToList();
            subscription.LastCheckedAt = DateTimeOffset.UtcNow;
            subscription.LastError = null;
            if (movieCompleted)
                subscription.Enabled = false;
            await SaveCheckStateAsync(subscription, movieCompleted);
            return Result<int>.Success(queued);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<int>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nyaa subscription {SubscriptionKey} check failed", key);
            if (checkingSubscription is not null)
            {
                checkingSubscription.LastCheckedAt = DateTimeOffset.UtcNow;
                checkingSubscription.LastError = ex.Message;
                try
                {
                    await SaveCheckStateAsync(checkingSubscription, disable: false);
                }
                catch (Exception saveException)
                {
                    _logger.LogWarning(
                        saveException,
                        "Could not persist the Nyaa subscription {SubscriptionKey} error",
                        key);
                }
            }
            return Result<int>.Failure(ex.Message, SubscriptionTitle());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Result<int>> SaveFailureAsync(
        NyaaVideoSubscription subscription,
        string error)
    {
        subscription.LastCheckedAt = DateTimeOffset.UtcNow;
        subscription.LastError = error;
        await SaveCheckStateAsync(subscription, disable: false);
        return Result<int>.Failure(error, SubscriptionTitle());
    }

    private static string SubscriptionTitle() => ResourceStringHelper.GetString(
        "NyaaSubscriptionTitle",
        "Nyaa subscription");

    private async Task SaveCheckStateAsync(
        NyaaVideoSubscription checkedSubscription,
        bool disable)
    {
        // A provider/backend call may take long enough for another page to save
        // discovery preferences. Merge only check-owned fields into the newest
        // settings snapshot so that response completion cannot restore stale
        // provider order, feeds, rules, or subscriptions removed in the meantime.
        var current = _settings.Current.DiscoverySettings.Clone();
        var persisted = current.NyaaSubscriptions.FirstOrDefault(subscription =>
            subscription.Key.Equals(checkedSubscription.Key, StringComparison.OrdinalIgnoreCase));
        if (persisted is null)
            return;

        persisted.SeenItemIds = new List<string>(checkedSubscription.SeenItemIds ?? []);
        persisted.ProcessedLogicalItemKeys = new List<string>(
            checkedSubscription.ProcessedLogicalItemKeys ?? []);
        persisted.LastCheckedAt = checkedSubscription.LastCheckedAt;
        persisted.LastError = checkedSubscription.LastError;
        if (disable)
            persisted.Enabled = false;
        await SaveAsync(current);
    }

    private async Task SaveAsync(DiscoverySettings discovery)
    {
        _settings.Set(value => value.DiscoverySettings, discovery);
        await _settings.SaveAsync();
        SubscriptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<INyaaDownloadManager> GetDownloadManagerAsync(CancellationToken ct)
    {
        Task<INyaaDownloadManager> task;
        lock (_downloadManagerSync)
        {
            _downloadManagerTask ??= Task.Run(() => _downloadManager.Value);
            task = _downloadManagerTask;
        }

        var manager = await task.WaitAsync(ct).ConfigureAwait(false);
        _resolvedDownloadManager = manager;
        if (!_downloadManagerSubscribed)
        {
            manager.TasksChanged += OnBuiltInTasksChanged;
            _downloadManagerSubscribed = true;
        }
        return manager;
    }

    private void CancelActiveCheck(string key)
    {
        if (!_activeChecks.TryGetValue(key, out var cancellation))
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnBuiltInTasksChanged(object? sender, EventArgs e) =>
        _ = ReconcileBuiltInTasksAsync();

    private async Task ReconcileBuiltInTasksAsync()
    {
        try
        {
            await _gate.WaitAsync(_lifetime.Token);
            try
            {
                if (_pendingBuiltInDownloads.Count == 0 || _resolvedDownloadManager is null)
                    return;

                var tasks = _resolvedDownloadManager.GetTasks()
                    .ToDictionary(task => task.TaskId, StringComparer.OrdinalIgnoreCase);
                var discovery = _settings.Current.DiscoverySettings.Clone();
                var changed = false;
                foreach (var pending in _pendingBuiltInDownloads.ToList())
                {
                    if (!tasks.TryGetValue(pending.Key, out var task))
                    {
                        _pendingBuiltInDownloads.Remove(pending.Key);
                        continue;
                    }

                    if (task.State == NyaaDownloadTaskState.Completed)
                    {
                        var subscription = discovery.NyaaSubscriptions.FirstOrDefault(value =>
                            value.Key.Equals(
                                pending.Value.SubscriptionKey,
                                StringComparison.OrdinalIgnoreCase));
                        if (subscription is not null)
                        {
                            var seen = new HashSet<string>(
                                subscription.SeenItemIds ?? [],
                                StringComparer.OrdinalIgnoreCase);
                            changed |= seen.Add(pending.Value.ItemId);
                            subscription.SeenItemIds = seen.TakeLast(5000).ToList();
                            if (!string.IsNullOrWhiteSpace(pending.Value.LogicalItemKey))
                            {
                                var processed = new HashSet<string>(
                                    subscription.ProcessedLogicalItemKeys ?? [],
                                    StringComparer.OrdinalIgnoreCase);
                                changed |= processed.Add(pending.Value.LogicalItemKey);
                                subscription.ProcessedLogicalItemKeys = processed
                                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                    .TakeLast(5000)
                                    .ToList();
                            }
                            if (subscription.MediaKind == VideoMetadataMediaKind.Movie
                                && subscription.Enabled)
                            {
                                subscription.Enabled = false;
                                changed = true;
                            }
                        }
                        _pendingBuiltInDownloads.Remove(pending.Key);
                    }
                    else if (task.State is NyaaDownloadTaskState.Failed or NyaaDownloadTaskState.Cancelled)
                    {
                        // Failed and cancelled tasks remain unseen so a later check can retry them.
                        _pendingBuiltInDownloads.Remove(pending.Key);
                    }
                }

                if (changed)
                    await SaveAsync(discovery);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reconcile completed Nyaa subscription downloads");
        }
    }

    private async Task RunPeriodicChecksAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
                await CheckAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic Nyaa discovery subscription check stopped");
        }
    }

    private static IReadOnlyList<NyaaTorrentItem> SelectCandidates(
        IReadOnlyList<NyaaTorrentItem> items,
        NyaaVideoSubscription subscription,
        HashSet<string> seen,
        HashSet<string> processed)
    {
        var matches = items
            .Where(item => !item.IsRemake && !IsBatchTitle(item.Title))
            .Where(item => MatchesVersion(item, subscription))
            .Where(item => MatchesIdentity(item, subscription))
            .Where(item => !seen.Contains(item.Id))
            .Select(item => new
            {
                Item = item,
                LogicalItemKey = LogicalItemKey(item, subscription),
                Episode = ParseEpisode(item.Title),
            })
            .Where(value => value.LogicalItemKey is not null)
            .Where(value => !processed.Contains(value.LogicalItemKey!))
            .Where(value => subscription.MediaKind == VideoMetadataMediaKind.Movie
                || subscription.StartAfterEpisode is null
                || value.Episode >= subscription.StartAfterEpisode)
            .ToList();

        if (subscription.MediaKind == VideoMetadataMediaKind.Movie)
        {
            return matches
                .OrderByDescending(value => value.Item.IsTrusted)
                .ThenByDescending(value => value.Item.Seeders)
                .ThenByDescending(value => value.Item.PublishedAt)
                .Take(1)
                .Select(value => value.Item)
                .ToList();
        }

        return matches
            .Where(value => value.Episode is not null)
            .GroupBy(value => value.LogicalItemKey!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(value => value.Item.IsTrusted)
                .ThenByDescending(value => value.Item.Seeders)
                .ThenByDescending(value => value.Item.PublishedAt)
                .ThenBy(value => value.Item.Id, StringComparer.OrdinalIgnoreCase)
                .First().Item)
            .OrderBy(item => LogicalItemKey(item, subscription), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesVersion(NyaaTorrentItem item, NyaaVideoSubscription subscription)
    {
        var group = ParseReleaseGroup(item.Title);
        var resolution = ParseResolution(item.Title);
        return group is not null
            && resolution is not null
            && group.Equals(subscription.ReleaseGroup, StringComparison.OrdinalIgnoreCase)
            && resolution.Equals(subscription.Resolution, StringComparison.OrdinalIgnoreCase)
            && (subscription.Trusted is bool trusted
                ? item.IsTrusted == trusted
                : !subscription.RequireTrusted || item.IsTrusted)
            && (string.IsNullOrWhiteSpace(subscription.SelectedCategory)
                || item.Category.Equals(subscription.SelectedCategory, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesIdentity(NyaaTorrentItem item, NyaaVideoSubscription subscription)
    {
        var candidateTitle = item.Title;
        var candidateTokens = RawTitleTokens(candidateTitle).ToList();
        var identityTitles = new[] { subscription.Title, subscription.OriginalTitle }
            .Concat(subscription.Aliases ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var phraseMatch = identityTitles.Any(title => HasTitleEvidence(title, candidateTokens));
        if (!phraseMatch)
            return false;

        if (subscription.SeasonNumber is not int expectedSeason)
            return true;

        var actualSeason = ParseSeason(candidateTitle);
        if (actualSeason is not null)
            return actualSeason == expectedSeason;

        return expectedSeason <= 1;
    }

    private static bool HasTitleEvidence(string title, IReadOnlyList<string> candidateTokens)
    {
        var expectedTokens = RawTitleTokens(title).ToList();
        if (expectedTokens.Count == 0)
            return false;
        if (ContainsTokenSequence(candidateTokens, expectedTokens))
            return true;

        var significant = expectedTokens
            .Where(token => (token.Length >= 3 || token.Any(character => character > 127))
                && !IgnoredTitleTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return significant.Count >= 2
            && significant.All(expected => candidateTokens.Contains(
                expected,
                StringComparer.OrdinalIgnoreCase));
    }

    private static bool ContainsTokenSequence(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> expected)
    {
        if (expected.Count > candidate.Count)
            return false;
        for (var start = 0; start <= candidate.Count - expected.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < expected.Count; offset++)
            {
                if (!candidate[start + offset].Equals(
                        expected[offset],
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
                return true;
        }
        return false;
    }

    private static IEnumerable<string> RawTitleTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            yield break;
        foreach (Match match in TitleTokenRegex().Matches(title))
            yield return match.Value.ToLowerInvariant();
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NormalizePosterUrl(string? value)
    {
        if (value?.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static string? NormalizePosterPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var path = Path.GetFullPath(value);
            var cacheRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Niratan",
                    "Cache",
                    "VideoMetadataArtwork"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasUsablePosterPath(string? value)
    {
        var normalized = NormalizePosterPath(value);
        return normalized is not null && File.Exists(normalized);
    }

    private static string? LogicalItemKey(
        NyaaTorrentItem item,
        NyaaVideoSubscription subscription)
    {
        if (subscription.MediaKind == VideoMetadataMediaKind.Movie)
            return "movie";
        if (item.IsRemake || IsBatchTitle(item.Title))
            return null;
        var episode = ParseEpisode(item.Title);
        if (episode is null || episode <= 0)
            return null;
        var season = ParseSeason(item.Title) ?? subscription.SeasonNumber ?? 1;
        if (season <= 0
            || subscription.SeasonNumber is int expectedSeason && season != expectedSeason)
        {
            return null;
        }
        return $"S{season:00}E{episode:0000}";
    }

    private static void MarkReleaseProcessed(
        NyaaVideoSubscription subscription,
        string itemId,
        string logicalItemKey)
    {
        var seen = new HashSet<string>(
            subscription.SeenItemIds ?? [],
            StringComparer.OrdinalIgnoreCase)
        {
            itemId,
        };
        subscription.SeenItemIds = seen.TakeLast(5000).ToList();
        var processed = new HashSet<string>(
            subscription.ProcessedLogicalItemKeys ?? [],
            StringComparer.OrdinalIgnoreCase)
        {
            logicalItemKey,
        };
        subscription.ProcessedLogicalItemKeys = processed
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .TakeLast(5000)
            .ToList();
    }

    private static VideoMetadataCandidate ToIdentity(NyaaVideoSubscription subscription) => new(
        subscription.ProviderId,
        subscription.ProviderItemId,
        subscription.MediaKind,
        subscription.Title,
        subscription.OriginalTitle,
        subscription.Year,
        subscription.SeasonNumber,
        null,
        null,
        (subscription.Aliases ?? []).ToImmutableArray(),
        (subscription.ExternalIds ?? []).ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase),
        null,
        subscription.PosterUrl);

    internal static string? ParseReleaseGroup(string title)
    {
        var match = ReleaseGroupRegex().Match(title);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static string? ParseResolution(string title)
    {
        var match = ResolutionRegex().Match(title);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static int? ParseEpisode(string title)
    {
        var match = EpisodeRegex().Match(title);
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode)
            ? episode
            : null;
    }

    internal static bool IsBatchTitle(string title)
    {
        var normalized = title.ToLowerInvariant();
        return normalized.Contains("batch", StringComparison.Ordinal)
            || normalized.Contains("complete season", StringComparison.Ordinal)
            || normalized.Contains("season pack", StringComparison.Ordinal)
            || EpisodeRangeRegex().IsMatch(title);
    }

    internal static int? ParseSeason(string title)
    {
        var match = SeasonRegex().Match(title);
        if (!match.Success)
            return null;
        foreach (var groupName in new[] { "season", "ordinal", "jp" })
        {
            if (int.TryParse(match.Groups[groupName].Value, out var season))
                return season;
        }
        return null;
    }

    private static string SubscriptionKey(VideoMetadataCandidate identity) =>
        $"{identity.ProviderId}:{identity.ProviderItemId}";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_downloadManagerSubscribed && _resolvedDownloadManager is not null)
            _resolvedDownloadManager.TasksChanged -= OnBuiltInTasksChanged;
        _timer.Dispose();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private sealed record PendingBuiltInDownload(
        string SubscriptionKey,
        string ItemId,
        string? LogicalItemKey);

    [GeneratedRegex(@"^\s*\[([^\]]+)\]")]
    private static partial Regex ReleaseGroupRegex();
    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|480p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
    [GeneratedRegex(@"(?:S\d{1,2}E|\bE(?:P)?\s*|\s-\s)0*(\d{1,4})(?:\b|v\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();
    [GeneratedRegex(@"\b(?:E|EP)?\d{1,4}\s*[-~〜]\s*(?:E|EP)?\d{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRangeRegex();
    [GeneratedRegex(@"(?:\bS(?:eason\s*)?0*(?<season>\d{1,3})(?:E\d|\b))|(?:\b0*(?<ordinal>\d{1,3})(?:st|nd|rd|th)\s+Season\b)|(?:第\s*0*(?<jp>\d{1,3})\s*期)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonRegex();
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTokenRegex();
}
