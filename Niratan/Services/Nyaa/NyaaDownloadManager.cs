using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Niratan.Messages;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public sealed class NyaaDownloadManager : INyaaDownloadManager, IDisposable
{
    private readonly ITorrentDownloadService _downloadService;
    private readonly IResourcePackageImportService _importService;
    private readonly IMessenger _messenger;
    private readonly ILogger<NyaaDownloadManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskEntry> _tasks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler? TasksChanged;

    public NyaaDownloadManager(
        ITorrentDownloadService downloadService,
        IResourcePackageImportService importService,
        IMessenger messenger,
        ILogger<NyaaDownloadManager> logger)
    {
        _downloadService = downloadService;
        _importService = importService;
        _messenger = messenger;
        _logger = logger;
    }

    public IReadOnlyList<NyaaDownloadTaskSnapshot> GetTasks()
    {
        lock (_gate)
        {
            return _tasks.Values
                .Select(entry => entry.Snapshot)
                .OrderByDescending(task => task.CreatedAt)
                .ToList();
        }
    }

    public string Enqueue(NyaaTorrentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);

        TaskEntry entry;
        lock (_gate)
        {
            var existing = _tasks.Values.FirstOrDefault(candidate =>
                candidate.Snapshot.Item.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
                && candidate.Snapshot.State is NyaaDownloadTaskState.Queued
                    or NyaaDownloadTaskState.Downloading
                    or NyaaDownloadTaskState.Paused
                    or NyaaDownloadTaskState.Importing);
            if (existing is not null)
                return existing.Snapshot.TaskId;

            var now = DateTimeOffset.Now;
            var taskId = Guid.NewGuid().ToString("N");
            entry = new TaskEntry(
                new NyaaDownloadTaskSnapshot(
                    taskId,
                    item,
                    NyaaDownloadTaskState.Queued,
                    0,
                    0,
                    0,
                    "Queued",
                    null,
                    null,
                    null,
                    now,
                    now),
                new CancellationTokenSource());
            _tasks.Add(taskId, entry);
        }

        RaiseTasksChanged();
        _ = RunTaskAsync(entry);
        return entry.Snapshot.TaskId;
    }

    public async Task PauseAsync(string taskId)
    {
        var entry = Find(taskId);
        if (entry is null || entry.Snapshot.State != NyaaDownloadTaskState.Downloading)
            return;

        var result = await _downloadService.PauseAsync(taskId);
        if (result.IsSuccess)
        {
            Update(entry, current => current with
            {
                State = NyaaDownloadTaskState.Paused,
                Status = "Paused",
            });
        }
        else if (!result.IsCancelled)
        {
            Update(entry, current => current with { Error = result.Error });
        }
    }

    public async Task ResumeAsync(string taskId)
    {
        var entry = Find(taskId);
        if (entry is null || entry.Snapshot.State != NyaaDownloadTaskState.Paused)
            return;

        var result = await _downloadService.ResumeAsync(taskId);
        if (result.IsSuccess)
        {
            Update(entry, current => current with
            {
                State = NyaaDownloadTaskState.Downloading,
                Status = "Downloading",
                Error = null,
            });
        }
        else if (!result.IsCancelled)
        {
            Update(entry, current => current with { Error = result.Error });
        }
    }

    public void Cancel(string taskId)
    {
        var entry = Find(taskId);
        if (entry is null || !entry.Snapshot.CanCancel)
            return;
        entry.Cancellation.Cancel();
    }

    public void Retry(string taskId)
    {
        var entry = Find(taskId);
        if (entry is null || !entry.Snapshot.CanRetry)
            return;

        lock (_gate)
        {
            entry.Cancellation.Dispose();
            entry.Cancellation = new CancellationTokenSource();
            entry.Snapshot = entry.Snapshot with
            {
                State = NyaaDownloadTaskState.Queued,
                ProgressPercent = 0,
                DownloadRateBytesPerSecond = 0,
                ConnectedPeers = 0,
                Status = "Queued",
                DownloadRootPath = null,
                Error = null,
                ImportResult = null,
                UpdatedAt = DateTimeOffset.Now,
            };
        }

        RaiseTasksChanged();
        _ = RunTaskAsync(entry);
    }

    public void Remove(string taskId)
    {
        TaskEntry? removed = null;
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var entry)
                && entry.Snapshot.CanRemove
                && _tasks.Remove(taskId))
            {
                removed = entry;
            }
        }

        removed?.Cancellation.Dispose();
        if (removed is not null)
            RaiseTasksChanged();
    }

    private async Task RunTaskAsync(TaskEntry entry)
    {
        try
        {
            Update(entry, current => current with
            {
                State = NyaaDownloadTaskState.Downloading,
                Status = "Preparing torrent metadata…",
            });
            var progress = new Progress<TorrentDownloadProgress>(value =>
                Update(entry, current =>
                    current.State is NyaaDownloadTaskState.Completed
                        or NyaaDownloadTaskState.Failed
                        or NyaaDownloadTaskState.Cancelled
                        ? current
                        : current with
                        {
                            ProgressPercent = value.Percent,
                            DownloadRateBytesPerSecond = value.DownloadRateBytesPerSecond,
                            ConnectedPeers = value.ConnectedPeers,
                            Status = current.State == NyaaDownloadTaskState.Paused
                                ? current.Status
                                : value.Status,
                        }));
            var download = await _downloadService.DownloadAsync(
                entry.Snapshot.TaskId,
                entry.Snapshot.Item,
                progress,
                entry.Cancellation.Token);
            if (download.IsCancelled)
            {
                SetCancelled(entry);
                return;
            }
            if (!download.IsSuccess)
            {
                SetFailed(entry, download.Error ?? "Torrent download failed.");
                return;
            }

            var downloadResult = download.Value!;
            Update(entry, current => current with
            {
                State = NyaaDownloadTaskState.Importing,
                ProgressPercent = 100,
                DownloadRateBytesPerSecond = 0,
                Status = "Importing resources…",
                DownloadRootPath = downloadResult.DownloadRootPath,
            });
            var imported = await _importService.ImportAsync(
                downloadResult.DownloadRootPath,
                entry.Cancellation.Token);
            if (imported.IsCancelled)
            {
                SetCancelled(entry);
                return;
            }
            if (!imported.IsSuccess)
            {
                SetFailed(entry, imported.Error ?? "Resource import failed.");
                return;
            }

            var importResult = imported.Value!;
            Update(entry, current => current with
            {
                State = NyaaDownloadTaskState.Completed,
                ProgressPercent = 100,
                DownloadRateBytesPerSecond = 0,
                ConnectedPeers = 0,
                Status = BuildImportSummary(importResult),
                Error = importResult.Warnings.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, importResult.Warnings),
                ImportResult = importResult,
            });
            if (importResult.ImportedNovelCount > 0)
                _messenger.Send(new NovelLibraryChangedMessage());
            if (importResult.ImportedVideoCount > 0)
                _messenger.Send(new VideoLibraryChangedMessage());
        }
        catch (OperationCanceledException)
        {
            SetCancelled(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nyaa download manager task {TaskId} failed", entry.Snapshot.TaskId);
            SetFailed(entry, ex.Message);
        }
    }

    private static string BuildImportSummary(ResourcePackageImportResult result)
    {
        var resources = new List<string>();
        if (result.ImportedNovelCount > 0)
            resources.Add($"{result.ImportedNovelCount} EPUB");
        if (result.MatchedNovelCount > 0)
            resources.Add($"{result.MatchedNovelCount} audiobook/SRT match");
        if (result.ImportedVideoCount > 0)
            resources.Add($"{result.ImportedVideoCount} video");
        return resources.Count == 0
            ? "Download completed; no supported resources were imported."
            : "Completed: " + string.Join(", ", resources);
    }

    private void SetCancelled(TaskEntry entry) =>
        Update(entry, current => current with
        {
            State = NyaaDownloadTaskState.Cancelled,
            DownloadRateBytesPerSecond = 0,
            ConnectedPeers = 0,
            Status = "Cancelled",
        });

    private void SetFailed(TaskEntry entry, string error) =>
        Update(entry, current => current with
        {
            State = NyaaDownloadTaskState.Failed,
            DownloadRateBytesPerSecond = 0,
            ConnectedPeers = 0,
            Status = "Failed",
            Error = error,
        });

    private TaskEntry? Find(string taskId)
    {
        lock (_gate)
            return _tasks.GetValueOrDefault(taskId);
    }

    private void Update(
        TaskEntry entry,
        Func<NyaaDownloadTaskSnapshot, NyaaDownloadTaskSnapshot> update)
    {
        lock (_gate)
        {
            if (!_tasks.ContainsKey(entry.Snapshot.TaskId))
                return;
            entry.Snapshot = update(entry.Snapshot) with { UpdatedAt = DateTimeOffset.Now };
        }

        RaiseTasksChanged();
    }

    private void RaiseTasksChanged() => TasksChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var entry in _tasks.Values)
            {
                entry.Cancellation.Cancel();
                entry.Cancellation.Dispose();
            }
            _tasks.Clear();
        }
    }

    private sealed class TaskEntry(
        NyaaDownloadTaskSnapshot snapshot,
        CancellationTokenSource cancellation)
    {
        public NyaaDownloadTaskSnapshot Snapshot { get; set; } = snapshot;
        public CancellationTokenSource Cancellation { get; set; } = cancellation;
    }
}
