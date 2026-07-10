using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Services.Storage;

namespace Hoshi.Services.Video;

internal sealed class VideoThumbnailService : IVideoThumbnailService
{
    private const int MaximumConcurrentJobs = 1;
    private static readonly TimeSpan DefaultCaptureTime = TimeSpan.FromSeconds(5);

    private readonly IVideoMiningMediaExtractor _extractor;
    private readonly IVideoDataService _dataService;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _generationGate = new(MaximumConcurrentJobs, MaximumConcurrentJobs);
    private readonly Dictionary<string, Task<string?>> _inFlight = [];
    private readonly object _sync = new();
    private int _suspensionCount;

    public VideoThumbnailService(
        IVideoMiningMediaExtractor extractor,
        IVideoDataService dataService,
        string? cacheDirectory = null)
    {
        _extractor = extractor;
        _dataService = dataService;
        _cacheDirectory = cacheDirectory ?? Path.Combine(AppDataHelper.GetDataPath(), "VideoThumbnails");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public Task<string?> EnsureThumbnailAsync(
        VideoItem video,
        bool generateIfMissing,
        CancellationToken ct = default) =>
        EnsureThumbnailPathAsync(video, generateIfMissing, ct);

    private async Task<string?> EnsureThumbnailPathAsync(
        VideoItem video,
        bool generateIfMissing,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(video);

        var posterPath = GetExistingSourcePath(video.PosterPath);
        if (posterPath != null)
            return posterPath;

        var thumbnailPath = GetExistingSourcePath(video.ThumbnailPath);
        if (thumbnailPath != null)
            return thumbnailPath;

        if (string.IsNullOrWhiteSpace(video.FilePath))
            return null;

        var cacheKey = CacheKey(video);
        var outputPath = Path.Combine(_cacheDirectory, $"{cacheKey}.png");
        var cachedPath = GetExistingGeneratedPath(outputPath);
        if (cachedPath != null)
        {
            if (!string.Equals(video.ThumbnailPath, cachedPath, StringComparison.OrdinalIgnoreCase))
                await _dataService.UpdateVideoThumbnailPathAsync(video.Id, cachedPath, ct);

            return cachedPath;
        }

        if (!generateIfMissing
            || IsSuspended
            || !File.Exists(video.FilePath))
        {
            return null;
        }

        Task<string?> generationTask;
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(cacheKey, out generationTask!))
            {
                generationTask = GenerateThumbnailAsync(cacheKey, video, outputPath);
                _inFlight[cacheKey] = generationTask;
            }
        }

        return await generationTask.WaitAsync(ct);
    }

    public void Suspend() => Interlocked.Increment(ref _suspensionCount);

    public void Resume()
    {
        while (true)
        {
            var current = Volatile.Read(ref _suspensionCount);
            if (current <= 0)
                return;

            if (Interlocked.CompareExchange(ref _suspensionCount, current - 1, current) == current)
                return;
        }
    }

    private bool IsSuspended => Volatile.Read(ref _suspensionCount) > 0;

    private async Task<string?> GenerateThumbnailAsync(string cacheKey, VideoItem video, string outputPath)
    {
        try
        {
            await _generationGate.WaitAsync();

            var cachedPath = GetExistingGeneratedPath(outputPath);
            if (cachedPath != null)
                return cachedPath;

            Directory.CreateDirectory(_cacheDirectory);
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var generated = await _extractor.CaptureScreenshotAsync(
                video.FilePath,
                outputPath,
                DefaultCaptureTime,
                CancellationToken.None);
            if (!HasFile(generated))
                return null;

            if (!string.Equals(generated, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(generated!, outputPath, overwrite: true);
            }

            await _dataService.UpdateVideoThumbnailPathAsync(video.Id, outputPath, CancellationToken.None);
            return outputPath;
        }
        finally
        {
            _generationGate.Release();
            lock (_sync)
            {
                _inFlight.Remove(cacheKey);
            }
        }
    }

    private static string CacheKey(VideoItem video)
    {
        var input = FormattableString.Invariant(
            $"{Path.GetFullPath(video.FilePath)}|{video.FileSizeBytes}|{video.ModifiedAt?.Ticks ?? 0}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? GetExistingSourcePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static string? GetExistingGeneratedPath(string? path) => HasFile(path) ? path : null;

    private static bool HasFile(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && new FileInfo(path).Length > 0;
}
