using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using Niratan.Models.Video;
using Niratan.Services.Storage;

namespace Niratan.Services.Video;

public enum VideoLibraryScanStage
{
    Enumerating,
    Analyzing,
    Committing,
    Completed,
}

public sealed record VideoLibraryScanProgress(
    Guid SourceId,
    long Generation,
    VideoCatalogJobState State,
    VideoLibraryScanStage Stage,
    int ProcessedCount,
    int? TotalCount,
    int ChangedCount,
    double ItemsPerSecond,
    string? CurrentPath,
    string? Error);

public interface IVideoLibraryScanCoordinator
{
    event EventHandler<VideoLibraryScanProgress>? ProgressChanged;
    Task ScanSourceAsync(Guid sourceId, bool fullScan = false, CancellationToken ct = default);
    Task ScanAllAsync(bool fullScan = false, CancellationToken ct = default);
    Task CancelAsync(Guid sourceId, CancellationToken ct = default);
    Task PauseAsync(Guid sourceId, CancellationToken ct = default);
    Task ResumeAsync(Guid sourceId, CancellationToken ct = default);
}

internal sealed class VideoLibraryScanCoordinator : IVideoLibraryScanCoordinator
{
    private const int CommitBatchSize = 100;
    private const int MaxAnalysisConcurrency = 4;
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(150);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts",
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma",
    };

    private readonly IVideoCatalogRepository _repository;
    private readonly IVideoFileNameParser _parser;
    private readonly ILocalVideoMetadataProvider _localMetadata;
    private readonly ILogger<VideoLibraryScanCoordinator> _logger;
    private readonly IAniDbImportService? _aniDb;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _startGates = new();
    private readonly ConcurrentDictionary<Guid, PauseState> _pauseStates = new();

    private sealed class PauseState
    {
        private readonly object _sync = new();
        private TaskCompletionSource<bool>? _resume;
        public void Pause()
        {
            lock (_sync)
                _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void Resume()
        {
            TaskCompletionSource<bool>? resume;
            lock (_sync)
            {
                resume = _resume;
                _resume = null;
            }
            resume?.TrySetResult(true);
        }
        public Task WaitAsync(CancellationToken ct)
        {
            lock (_sync)
                return _resume?.Task.WaitAsync(ct) ?? Task.CompletedTask;
        }
    }

    public VideoLibraryScanCoordinator(
        IVideoCatalogRepository repository,
        IVideoFileNameParser parser,
        ILocalVideoMetadataProvider localMetadata,
        ILogger<VideoLibraryScanCoordinator> logger,
        IAniDbImportService? aniDb = null)
    {
        _repository = repository;
        _parser = parser;
        _localMetadata = localMetadata;
        _logger = logger;
        _aniDb = aniDb;
    }

    public event EventHandler<VideoLibraryScanProgress>? ProgressChanged;

    public async Task ScanAllAsync(bool fullScan = false, CancellationToken ct = default)
    {
        var aniDbAdmission = _aniDb?.CaptureScrapeAdmission();
        var snapshot = await _repository.GetSnapshotAsync(ct);
        foreach (var source in snapshot.Sources)
        {
            ct.ThrowIfCancellationRequested();
            await ScanSourceCoreAsync(source.Id, fullScan, aniDbAdmission, ct);
        }
    }

    public Task ScanSourceAsync(Guid sourceId, bool fullScan = false, CancellationToken ct = default) =>
        ScanSourceCoreAsync(sourceId, fullScan, _aniDb?.CaptureScrapeAdmission(), ct);

    private async Task ScanSourceCoreAsync(
        Guid sourceId,
        bool fullScan,
        AniDbScrapeAdmissionStamp? aniDbAdmission,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.StartNew();
        var processed = 0;
        var changedCount = 0;
        var generation = 0L;
        var scanStarted = false;
        CancellationTokenSource? linked = null;

        try
        {
            VideoCatalogSnapshot snapshot;
            VideoCatalogSourceSnapshot source;
            var startGate = _startGates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
            await startGate.WaitAsync(ct);
            try
            {
                snapshot = await _repository.GetSnapshotAsync(ct);
                source = snapshot.Sources.FirstOrDefault(item => item.Id == sourceId)
                    ?? throw new KeyNotFoundException("Video source was not found.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var startedGeneration = await _repository.TryBeginSourceScanAsync(
                    sourceId,
                    fullScan ? VideoCatalogJobKind.FullScan : VideoCatalogJobKind.IncrementalScan,
                    source.ScanGeneration,
                    linked.Token);
                if (!startedGeneration.HasValue)
                    throw new OperationCanceledException(
                        "Video scan was superseded before it could enter the catalog generation.",
                        linked.Token);
                generation = startedGeneration.Value;
                scanStarted = true;

                var previous = ReplaceActiveScan(sourceId, linked);
                if (previous != null)
                {
                    try
                    {
                        previous.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The superseded owner completed between the compare-and-swap and cancel.
                    }
                }
            }
            finally
            {
                startGate.Release();
            }

            var token = linked!.Token;
            var pauseState = _pauseStates.GetOrAdd(sourceId, _ => new PauseState());
            pauseState.Resume();
            Publish(sourceId, generation, VideoCatalogJobState.Running, VideoLibraryScanStage.Enumerating,
                0, null, 0, 0, null, null);
            var existingByIdentity = snapshot.Assets.ToDictionary(
                asset => asset.IdentityKey,
                StringComparer.OrdinalIgnoreCase);
            var unmatchedNodeIds = snapshot.Nodes
                .Where(node => node.Kind == VideoCatalogNodeKind.Unmatched)
                .Select(node => node.Id)
                .ToHashSet();
            var nodesById = snapshot.Nodes.ToDictionary(node => node.Id);
            var lastEnumerationPublish = TimeSpan.Zero;
            var (paths, enumerationError) = await Task.Run(
                () => EnumerateSourceFiles(source.FolderPath, token, (count, path) =>
                {
                    if (startedAt.Elapsed - lastEnumerationPublish < ProgressPublishInterval)
                        return;
                    lastEnumerationPublish = startedAt.Elapsed;
                    Publish(sourceId, generation, VideoCatalogJobState.Running,
                        VideoLibraryScanStage.Enumerating, count, null, 0,
                        CalculateRate(count, startedAt.Elapsed), path, null);
                }),
                token);
            Publish(sourceId, generation, VideoCatalogJobState.Running,
                VideoLibraryScanStage.Analyzing, 0, paths.Count, 0, 0, null, enumerationError);
            var parsedIdentities = VideoScanBundleClassifier.Parse(
                paths, source.FolderPath, source.MediaType, _parser);

            for (var offset = 0; offset < paths.Count; offset += CommitBatchSize)
            {
                token.ThrowIfCancellationRequested();
                await pauseState.WaitAsync(token);
                var count = Math.Min(CommitBatchSize, paths.Count - offset);
                var analyzed = new VideoScanAsset[count];
                var publishLock = new object();
                var lastAnalysisPublish = TimeSpan.Zero;
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, count),
                    new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = MaxAnalysisConcurrency,
                    },
                    async (index, itemToken) =>
                    {
                        await pauseState.WaitAsync(itemToken);
                        var result = await AnalyzePathAsync(
                            paths[offset + index], source, existingByIdentity, unmatchedNodeIds,
                            nodesById, parsedIdentities, fullScan, itemToken);
                        analyzed[index] = result.Asset;
                        if (result.Changed)
                            Interlocked.Increment(ref changedCount);
                        var current = Interlocked.Increment(ref processed);
                        lock (publishLock)
                        {
                            if (startedAt.Elapsed - lastAnalysisPublish < ProgressPublishInterval
                                && current != paths.Count)
                                return;
                            lastAnalysisPublish = startedAt.Elapsed;
                        }
                        Publish(sourceId, generation, VideoCatalogJobState.Running,
                            VideoLibraryScanStage.Analyzing, current, paths.Count,
                            Volatile.Read(ref changedCount), CalculateRate(current, startedAt.Elapsed),
                            paths[offset + index], enumerationError);
                    });

                Publish(sourceId, generation, VideoCatalogJobState.Running,
                    VideoLibraryScanStage.Committing, processed, paths.Count, changedCount,
                    CalculateRate(processed, startedAt.Elapsed), null, enumerationError);
                if (!await _repository.ApplyScanBatchAsync(new VideoScanBatch(
                        sourceId, generation, DateTimeOffset.UtcNow, analyzed, false,
                        IsFinal: false, TotalCount: paths.Count), token))
                    throw new OperationCanceledException("Video scan was superseded.");
            }

            var completed = enumerationError == null;
            if (!await _repository.ApplyScanBatchAsync(new VideoScanBatch(
                    sourceId,
                    generation,
                    DateTimeOffset.UtcNow,
                    [],
                    completed,
                    enumerationError,
                    IsFinal: true,
                    TotalCount: paths.Count), token))
                throw new OperationCanceledException("Video scan was superseded.");
            Publish(
                sourceId,
                generation,
                completed ? VideoCatalogJobState.Completed : VideoCatalogJobState.Failed,
                VideoLibraryScanStage.Completed,
                processed,
                paths.Count,
                changedCount,
                CalculateRate(processed, startedAt.Elapsed),
                null,
                enumerationError);
            if (completed && _aniDb != null && aniDbAdmission.HasValue)
                await _aniDb.QueueSourceAsync(sourceId, aniDbAdmission.Value, token);
        }
        catch (OperationCanceledException)
        {
            if (scanStarted)
            {
                await _repository.CancelSourceScanAsync(
                    sourceId, generation, CancellationToken.None);
                Publish(sourceId, generation, VideoCatalogJobState.Cancelled,
                    VideoLibraryScanStage.Completed, processed, null, changedCount,
                    CalculateRate(processed, startedAt.Elapsed), null, null);
            }
            throw;
        }
        finally
        {
            if (linked != null)
            {
                ((ICollection<KeyValuePair<Guid, CancellationTokenSource>>)_active).Remove(
                    new KeyValuePair<Guid, CancellationTokenSource>(sourceId, linked));
                linked.Dispose();
            }
        }
    }

    private CancellationTokenSource? ReplaceActiveScan(
        Guid sourceId,
        CancellationTokenSource replacement)
    {
        while (true)
        {
            if (!_active.TryGetValue(sourceId, out var current))
            {
                if (_active.TryAdd(sourceId, replacement))
                    return null;
                continue;
            }

            if (_active.TryUpdate(sourceId, replacement, current))
                return current;
        }
    }

    public async Task CancelAsync(Guid sourceId, CancellationToken ct = default)
    {
        var startGate = _startGates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await startGate.WaitAsync(ct);
        try
        {
            var snapshot = await _repository.GetSnapshotAsync(ct);
            var source = snapshot.Sources.FirstOrDefault(item => item.Id == sourceId)
                ?? throw new KeyNotFoundException("Video source was not found.");
            if (_active.TryGetValue(sourceId, out var active))
            {
                try
                {
                    active.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The scan completed after the lookup; the generation-aware repository
                    // cancellation below is still authoritative.
                }
            }
            await _repository.CancelSourceScanAsync(sourceId, source.ScanGeneration, ct);
        }
        finally
        {
            startGate.Release();
        }
    }

    public async Task PauseAsync(Guid sourceId, CancellationToken ct = default)
    {
        _pauseStates.GetOrAdd(sourceId, _ => new PauseState()).Pause();
        await _repository.SetSourceScanPausedAsync(sourceId, true, ct);
    }

    public async Task ResumeAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (_pauseStates.TryGetValue(sourceId, out var state))
            state.Resume();
        await _repository.SetSourceScanPausedAsync(sourceId, false, ct);
    }

    private static (IReadOnlyList<string> Paths, string? Error) EnumerateSourceFiles(
        string sourceRoot,
        CancellationToken ct,
        Action<int, string?>? progress = null)
    {
        var files = new List<string>();
        var errors = new List<string>();
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
            return (files, "Video source folder is no longer available.");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    {
                        files.Add(Path.GetFullPath(file));
                        progress?.Invoke(files.Count, file);
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                        pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{directory}: {ex.Message}");
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (files, errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }

    private static string ResolveParentFolder(string path, string root)
    {
        var directory = Path.GetDirectoryName(path) ?? root;
        var relative = Path.GetRelativePath(root, directory);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault(segment => segment is not "." && !string.IsNullOrWhiteSpace(segment));
        return first ?? new DirectoryInfo(root).Name;
    }

    private static string? FindSidecarSubtitle(string videoPath) => VideoLibraryService.FindSidecarSubtitle(videoPath);

    private async Task<(VideoScanAsset Asset, bool Changed)> AnalyzePathAsync(
        string path,
        VideoCatalogSourceSnapshot source,
        IReadOnlyDictionary<string, VideoCatalogAssetSnapshot> existingByIdentity,
        IReadOnlySet<Guid> unmatchedNodeIds,
        IReadOnlyDictionary<Guid, VideoCatalogNodeSnapshot> nodesById,
        IReadOnlyDictionary<string, ParsedVideoIdentity> parsedIdentities,
        bool fullScan,
        CancellationToken token)
    {
        var info = new FileInfo(path);
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        existingByIdentity.TryGetValue(identity, out var existing);
        var parsed = parsedIdentities[identity];
        var hasUnmatchedBinding = existing?.NodeIds.Any(unmatchedNodeIds.Contains) == true;
        var hasCompatibilityReparseSignal = existing is
        {
            Kind: VideoMediaAssetKind.LocalFile,
            ModifiedAt: null,
        };
        var fileChanged = fullScan
                          || existing is null
                          || existing.FileSize != info.Length
                          || existing.ModifiedAt != modified;
        var parentFolder = ResolveParentFolder(identity, source.FolderPath);
        LocalVideoMetadata? local = null;
        if (fileChanged)
        {
            try
            {
                local = await _localMetadata.ReadAsync(identity, source.FolderPath, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                _logger.LogWarning(ex, "Ignored invalid local metadata for video asset");
            }
        }
        // Compatibility repairs clear modified_at as a one-shot reparse signal. Rebuild episodic
        // ownership even when its old Episode fields happen to look valid: earlier metadata matching
        // could still have attached that Episode to a release-name Series. Local NFO numbering is
        // valid episode evidence even when the filename itself has no number.
        var hasEffectiveEpisodeEvidence = parsed.HasEpisodeEvidence
                                          || local?.EpisodeNumber.HasValue == true
                                          || local?.AbsoluteEpisodeNumber.HasValue == true;
        var hasLegacyEpisodicBinding = existing?.NodeIds.Any(id =>
            nodesById.GetValueOrDefault(id)?.Kind is VideoCatalogNodeKind.Series
                or VideoCatalogNodeKind.Season
                or VideoCatalogNodeKind.Episode) == true;
        var needsMovieHierarchyRepair = source.MediaType == VideoLibraryMediaType.Movie
                                        && hasCompatibilityReparseSignal
                                        && hasLegacyEpisodicBinding;
        var needsCatalogRepair = hasUnmatchedBinding
                                 || hasCompatibilityReparseSignal && hasEffectiveEpisodeEvidence
                                 || needsMovieHierarchyRepair;
        var hierarchyChanged = existing != null
                               && HasHierarchyChanged(existing, parsed, local, nodesById);
        var changed = fileChanged || needsCatalogRepair || hierarchyChanged;

        return (new VideoScanAsset(
            new VideoCatalogAssetUpsert(
                identity,
                VideoMediaAssetKind.LocalFile,
                identity,
                parsed.NormalizedTitle,
                parentFolder,
                info.Length,
                modified,
                existing?.ImportedAt ?? DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                VideoMediaAvailability.Available,
                source.Id,
                parsed.EpisodeStart,
                parsed.EpisodeEnd,
                BoundSubtitlePath: FindSidecarSubtitle(identity),
                PosterPath: local?.PreferredAssetArtworkPath(
                    source.MediaType == VideoLibraryMediaType.Movie)),
            parsed,
            local,
            SkipMetadataProcessing: !changed,
            RebuildHierarchy: needsCatalogRepair || hierarchyChanged), changed);
    }

    private static bool HasHierarchyChanged(
        VideoCatalogAssetSnapshot existing,
        ParsedVideoIdentity parsed,
        LocalVideoMetadata? local,
        IReadOnlyDictionary<Guid, VideoCatalogNodeSnapshot> nodesById)
    {
        if (!string.Equals(existing.Title, parsed.NormalizedTitle, StringComparison.Ordinal)
            || existing.EpisodeStart != parsed.EpisodeStart
            || existing.EpisodeEnd != parsed.EpisodeEnd)
            return true;
        if (!parsed.HasEpisodeEvidence
            && local?.EpisodeNumber.HasValue != true
            && local?.AbsoluteEpisodeNumber.HasValue != true)
            return false;

        var episodeNodes = existing.NodeIds
            .Select(id => nodesById.GetValueOrDefault(id))
            .Where(node => node?.Kind == VideoCatalogNodeKind.Episode)
            .Cast<VideoCatalogNodeSnapshot>()
            .ToList();
        if (episodeNodes.Count != 1)
            return true;
        // An unchanged incremental scan intentionally does not reread NFO. Do not treat a
        // persisted local season/episode override as parser drift when that local evidence is
        // unavailable; filename/asset identity changes above still trigger lightweight repair.
        if (local == null)
            return false;

        var isSpecial = parsed.SpecialKind != ParsedVideoSpecialKind.None;
        var season = isSpecial ? 0 : local.SeasonNumber ?? parsed.SeasonNumber;
        var episode = local.EpisodeNumber
                      ?? local.AbsoluteEpisodeNumber
                      ?? parsed.EpisodeStart
                      ?? parsed.AbsoluteEpisodeNumber;
        var episodeNode = episodeNodes[0];
        return episodeNode.IsSpecial != isSpecial
               || (season.HasValue && episodeNode.SeasonNumber != season)
               || episodeNode.EpisodeNumber != episode;
    }

    private static double CalculateRate(int count, TimeSpan elapsed) =>
        count <= 0 || elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;

    private void Publish(
        Guid sourceId,
        long generation,
        VideoCatalogJobState state,
        VideoLibraryScanStage stage,
        int count,
        int? totalCount,
        int changedCount,
        double itemsPerSecond,
        string? path,
        string? error) =>
        ProgressChanged?.Invoke(this, new VideoLibraryScanProgress(
            sourceId, generation, state, stage, count, totalCount, changedCount,
            itemsPerSecond, path, error));
}
