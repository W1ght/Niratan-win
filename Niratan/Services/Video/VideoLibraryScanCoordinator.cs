using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
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
        ILogger<VideoLibraryScanCoordinator> logger)
    {
        _repository = repository;
        _parser = parser;
        _localMetadata = localMetadata;
        _logger = logger;
    }

    public event EventHandler<VideoLibraryScanProgress>? ProgressChanged;

    public async Task ScanAllAsync(bool fullScan = false, CancellationToken ct = default)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        foreach (var source in snapshot.Sources)
        {
            ct.ThrowIfCancellationRequested();
            await ScanSourceAsync(source.Id, fullScan, ct);
        }
    }

    public async Task ScanSourceAsync(Guid sourceId, bool fullScan = false, CancellationToken ct = default)
    {
        var snapshot = await _repository.GetSnapshotAsync(ct);
        var source = snapshot.Sources.FirstOrDefault(item => item.Id == sourceId)
            ?? throw new KeyNotFoundException("Video source was not found.");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_active.TryGetValue(sourceId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _active[sourceId] = linked;
        var token = linked.Token;
        var pauseState = _pauseStates.GetOrAdd(sourceId, _ => new PauseState());
        pauseState.Resume();
        var generation = await _repository.BeginSourceScanAsync(
            sourceId,
            fullScan ? VideoCatalogJobKind.FullScan : VideoCatalogJobKind.IncrementalScan,
            token);
        var startedAt = Stopwatch.StartNew();
        var processed = 0;
        var changedCount = 0;
        Publish(sourceId, generation, VideoCatalogJobState.Running, VideoLibraryScanStage.Enumerating,
            0, null, 0, 0, null, null);

        try
        {
            var existingByIdentity = snapshot.Assets.ToDictionary(
                asset => asset.IdentityKey,
                StringComparer.OrdinalIgnoreCase);
            var unmatchedNodeIds = snapshot.Nodes
                .Where(node => node.Kind == VideoCatalogNodeKind.Unmatched)
                .Select(node => node.Id)
                .ToHashSet();
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
                            fullScan, itemToken);
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
        }
        catch (OperationCanceledException)
        {
            await _repository.CancelSourceScanAsync(sourceId, CancellationToken.None);
            Publish(sourceId, generation, VideoCatalogJobState.Cancelled,
                VideoLibraryScanStage.Completed, processed, null, changedCount,
                CalculateRate(processed, startedAt.Elapsed), null, null);
            throw;
        }
        finally
        {
            if (_active.TryRemove(sourceId, out var current))
                current.Dispose();
        }
    }

    public async Task CancelAsync(Guid sourceId, CancellationToken ct = default)
    {
        if (_active.TryGetValue(sourceId, out var active))
            active.Cancel();
        await _repository.CancelSourceScanAsync(sourceId, ct);
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
        bool fullScan,
        CancellationToken token)
    {
        var info = new FileInfo(path);
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        existingByIdentity.TryGetValue(identity, out var existing);
        // Catalog v1 initially stored parsed episodic assets under unmatched nodes. Repair those
        // records once during the next incremental scan even when the media file itself is unchanged.
        var needsCatalogRepair = existing?.NodeIds.Any(unmatchedNodeIds.Contains) == true;
        var changed = fullScan
                      || needsCatalogRepair
                      || existing is null
                      || existing.FileSize != info.Length
                      || existing.ModifiedAt != modified;
        var parentFolder = ResolveParentFolder(identity, source.FolderPath);
        ParsedVideoIdentity parsed;
        LocalVideoMetadata? local = null;
        if (changed)
        {
            parsed = _parser.Parse(identity, source.FolderPath, source.MediaType);
            try
            {
                local = await _localMetadata.ReadAsync(identity, source.FolderPath, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                _logger.LogWarning(ex, "Ignored invalid local metadata for video asset");
            }
        }
        else
        {
            parsed = new ParsedVideoIdentity(
                existing!.Title,
                existing.Title,
                parentFolder,
                null,
                null,
                existing.EpisodeStart,
                existing.EpisodeEnd,
                null,
                null,
                null,
                ParsedVideoSpecialKind.None,
                existing.EpisodeStart != existing.EpisodeEnd,
                existing.EpisodeStart.HasValue,
                ImmutableDictionary<string, string>.Empty,
                []);
        }

        return (new VideoScanAsset(
            new VideoCatalogAssetUpsert(
                identity,
                VideoMediaAssetKind.LocalFile,
                identity,
                changed ? parsed.NormalizedTitle : existing!.Title,
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
                PosterPath: local?.ArtworkPaths.FirstOrDefault(pathValue =>
                    !Path.GetFileNameWithoutExtension(pathValue).Contains("fanart", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileNameWithoutExtension(pathValue).Contains("backdrop", StringComparison.OrdinalIgnoreCase))),
            parsed,
            local,
            SkipMetadataProcessing: !changed), changed);
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
