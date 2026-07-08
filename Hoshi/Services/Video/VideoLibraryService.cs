using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.Storage;
using Microsoft.Extensions.Logging;

namespace Hoshi.Services.Video;

internal sealed class VideoLibraryService : IVideoLibraryService
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv",
        ".mp4",
        ".webm",
        ".avi",
        ".mov",
    };

    private static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    private readonly IDataService _dataService;
    private readonly ILogger<VideoLibraryService> _logger;

    public VideoLibraryService(IDataService dataService, ILogger<VideoLibraryService> logger)
    {
        _dataService = dataService;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<IReadOnlyList<VideoItem>>.Success(
                await _dataService.GetVideosAsync(queryText, token)),
            "Error loading videos",
            ct);

    public async Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Result<VideoItem>.Failure("Video path is empty.", "Import failed");

        if (!File.Exists(filePath))
            return Result<VideoItem>.Failure("Video file was not found.", "Import failed");

        if (!SupportedVideoExtensions.Contains(Path.GetExtension(filePath)))
            return Result<VideoItem>.Failure("Unsupported video file type.", "Import failed");

        return await ExecuteAsync(
            async token =>
            {
                var video = new VideoItem
                {
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    FilePath = filePath,
                    SubtitlePath = FindSidecarSubtitle(filePath),
                    ImportedAt = DateTime.UtcNow,
                };
                await _dataService.UpsertVideoAsync(video, token);
                _logger.LogInformation("Imported video {FilePath}", video.FilePath);
                return Result<VideoItem>.Success(video);
            },
            "Error saving video",
            ct);
    }

    public async Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default) =>
        await ExecuteAsync(
            async token => Result<VideoItem?>.Success(await _dataService.GetVideoAsync(videoId, token)),
            "Error loading video",
            ct);

    public async Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.UpdateVideoLastOpenedAsync(videoId, DateTime.UtcNow, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating video last opened time for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error opening video");
        }
    }

    public async Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.DeleteVideoAsync(videoId, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting video {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error deleting video");
        }
    }

    public async Task<Result> SaveProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.SaveVideoProgressAsync(videoId, positionSeconds, durationSeconds, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving video progress for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error saving video progress");
        }
    }

    public async Task<Result> SavePlaybackStateAsync(
        string videoId,
        VideoPlaybackState state,
        CancellationToken ct = default)
    {
        try
        {
            await _dataService.SaveVideoPlaybackStateAsync(videoId, state, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving video playback state for {VideoId}", videoId);
            return Result.Failure(ex.Message, "Error saving video playback state");
        }
    }

    public static string? FindSidecarSubtitle(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.Combine(directory ?? "", Path.GetFileNameWithoutExtension(videoPath));

        foreach (var extension in SubtitleExtensions)
        {
            var candidate = stem + extension;
            if (File.Exists(candidate))
                return candidate;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        foreach (var extension in SubtitleExtensions)
        {
            var prefix = Path.GetFileNameWithoutExtension(videoPath) + ".";
            var matches = Directory.EnumerateFiles(directory, "*" + extension)
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            var candidate = matches.FirstOrDefault();
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> action,
        string errorTitle,
        CancellationToken ct)
    {
        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ErrorTitle}", errorTitle);
            return Result<T>.Failure(ex.Message, errorTitle);
        }
    }
}
