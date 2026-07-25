using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public sealed class MonoTorrentDownloadService : ITorrentDownloadService, IDisposable
{
    private static readonly Uri NyaaBaseUri = new("https://nyaa.si/");
    private const long MaximumTorrentMetadataBytes = 32 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly ClientEngine _engine;
    private readonly ILogger<MonoTorrentDownloadService> _logger;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentManager> _activeManagers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _downloadBasePath;
    private bool _disposed;

    public MonoTorrentDownloadService(
        HttpClient httpClient,
        ILogger<MonoTorrentDownloadService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _downloadBasePath = Path.Combine(AppDataHelper.GetDataPath(), "TorrentDownloads");
        var cachePath = Path.Combine(AppDataHelper.GetAppDataPath(), "Cache", "MonoTorrent");
        Directory.CreateDirectory(_downloadBasePath);
        Directory.CreateDirectory(cachePath);
        _engine = new ClientEngine(new EngineSettingsBuilder
        {
            AllowPortForwarding = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = cachePath,
            MaximumConnections = 120,
            MaximumHalfOpenConnections = 20,
            MaximumOpenFiles = 96,
            MaximumUploadRate = 2 * 1024 * 1024,
        }.ToSettings());
    }

    public async Task<Result<TorrentDownloadResult>> DownloadAsync(
        string taskId,
        NyaaTorrentItem item,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gateAcquired = false;
        var jobRoot = Path.Combine(_downloadBasePath, BuildJobDirectoryName(item));
        TorrentManager? manager = null;
        var completed = false;

        try
        {
            await _downloadGate.WaitAsync(ct);
            gateAcquired = true;
            Directory.CreateDirectory(jobRoot);
            progress?.Report(new TorrentDownloadProgress("Downloading torrent metadata…", 0, 0, 0));
            var metadataPath = await DownloadTorrentMetadataAsync(item, jobRoot, ct);
            manager = await _engine.AddAsync(
                metadataPath,
                jobRoot,
                new TorrentSettingsBuilder
                {
                    MaximumConnections = 80,
                }.ToSettings());

            ValidateTorrentPaths(manager, jobRoot);
            if (!_activeManagers.TryAdd(taskId, manager))
                throw new InvalidOperationException("A torrent task with the same identifier is already active.");
            await manager.StartAsync();
            progress?.Report(new TorrentDownloadProgress("Connecting to peers…", 0, 0, 0));

            while (!manager.Complete)
            {
                ct.ThrowIfCancellationRequested();
                var peers = await manager.GetPeersAsync();
                progress?.Report(new TorrentDownloadProgress(
                    manager.State.ToString(),
                    Math.Clamp(manager.Progress, 0, 100),
                    Math.Max(0, manager.Monitor.DownloadRate),
                    peers.Count));
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            progress?.Report(new TorrentDownloadProgress("Verifying download…", 100, 0, 0));
            var files = manager.Files
                .Select(file => Path.GetFullPath(file.FullPath))
                .Where(File.Exists)
                .ToList();
            completed = true;
            _logger.LogInformation(
                "Completed Nyaa torrent {TorrentId} with {FileCount} files in {RootPath}",
                item.Id,
                files.Count,
                jobRoot);
            return Result<TorrentDownloadResult>.Success(new TorrentDownloadResult(jobRoot, files));
        }
        catch (OperationCanceledException)
        {
            return Result<TorrentDownloadResult>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nyaa torrent download failed for {TorrentId}", item.Id);
            return Result<TorrentDownloadResult>.Failure(ex.Message, "Torrent download failed");
        }
        finally
        {
            _activeManagers.TryRemove(taskId, out _);
            if (manager is not null)
                await StopAndRemoveAsync(manager);
            if (!completed)
                TryDeleteIncompleteDownload(jobRoot);
            if (gateAcquired)
                _downloadGate.Release();
        }
    }

    public async Task<Result> PauseAsync(string taskId)
    {
        if (!_activeManagers.TryGetValue(taskId, out var manager))
            return Result.Failure("The torrent is not active.", "Could not pause download");

        try
        {
            await manager.PauseAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, "Could not pause download");
        }
    }

    public async Task<Result> ResumeAsync(string taskId)
    {
        if (!_activeManagers.TryGetValue(taskId, out var manager))
            return Result.Failure("The torrent is not active.", "Could not resume download");

        try
        {
            await manager.StartAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(ex.Message, "Could not resume download");
        }
    }

    private async Task<string> DownloadTorrentMetadataAsync(
        NyaaTorrentItem item,
        string jobRoot,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, item.TorrentUri);
        request.Headers.Referrer = item.DetailsUri;
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();
        EnsureNyaaOrigin(response.RequestMessage?.RequestUri);
        if (response.Content.Headers.ContentLength > MaximumTorrentMetadataBytes)
            throw new InvalidDataException("Torrent metadata exceeded the 32 MiB safety limit.");

        var metadataPath = Path.Combine(jobRoot, ".niratan-source.torrent");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(
            metadataPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaximumTorrentMetadataBytes)
                    throw new InvalidDataException("Torrent metadata exceeded the 32 MiB safety limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return metadataPath;
    }

    private static void EnsureNyaaOrigin(Uri? uri)
    {
        if (uri is null
            || !uri.Scheme.Equals(NyaaBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(NyaaBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != NyaaBaseUri.Port
            || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException("Nyaa redirected torrent metadata outside its allowed origin.");
        }
    }

    private static void ValidateTorrentPaths(TorrentManager manager, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        foreach (var file in manager.Files)
        {
            var fullPath = Path.GetFullPath(file.FullPath);
            var relative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Torrent contains a file outside the download directory: {file.Path}");
            }
        }
    }

    private async Task StopAndRemoveAsync(TorrentManager manager)
    {
        try
        {
            if (manager.State != TorrentState.Stopped)
                await manager.StopAsync(TimeSpan.FromSeconds(5));
            await _engine.RemoveAsync(manager);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not cleanly stop the torrent manager");
        }
    }

    private void TryDeleteIncompleteDownload(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_downloadBasePath));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var relative = Path.GetRelativePath(root, target);
            if (relative.Length > 0
                && !Path.IsPathRooted(relative)
                && !relative.StartsWith("..", StringComparison.Ordinal)
                && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete incomplete torrent data at {Path}", path);
        }
    }

    private static string BuildJobDirectoryName(NyaaTorrentItem item)
    {
        var id = string.Concat(item.Id.Where(char.IsLetterOrDigit));
        if (id.Length == 0)
            id = "unknown";
        return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id}-{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _engine.Dispose();
        _downloadGate.Dispose();
    }
}
