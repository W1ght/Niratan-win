using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.QBittorrent;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

internal sealed class VideoDownloadImportService : IVideoDownloadImportService
{
    private readonly IVideoLibraryService _videoLibrary;

    public VideoDownloadImportService(IVideoLibraryService videoLibrary) => _videoLibrary = videoLibrary;

    public async Task<Result<IReadOnlyList<VideoLibrarySource>>> GetCompatibleSourcesAsync(
        QbittorrentTorrent task,
        CancellationToken ct = default)
    {
        if (!task.IsCompleted)
            return Result<IReadOnlyList<VideoLibrarySource>>.Failure(
                "The download is not complete yet.", "Import unavailable");
        if (!TryGetLocalContentPath(task, out var contentPath))
            return Result<IReadOnlyList<VideoLibrarySource>>.Failure(
                "The qBittorrent content path is not accessible on this computer.", "Import unavailable");

        var sources = await _videoLibrary.GetSourcesAsync(ct);
        if (!sources.IsSuccess || sources.Value is null)
            return Result<IReadOnlyList<VideoLibrarySource>>.Failure(
                sources.Error ?? "Video sources could not be loaded.", sources.ErrorTitle ?? "Import unavailable");
        return Result<IReadOnlyList<VideoLibrarySource>>.Success(
            sources.Value.Where(source => IsWithin(contentPath, source.FolderPath)).ToList());
    }

    public async Task<Result<VideoSourceRefreshResult>> ImportCompletedTaskAsync(
        QbittorrentTorrent task,
        string sourceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return Result<VideoSourceRefreshResult>.Failure("Choose a video source first.", "Import unavailable");
        var compatible = await GetCompatibleSourcesAsync(task, ct);
        if (!compatible.IsSuccess || compatible.Value is null)
            return Result<VideoSourceRefreshResult>.Failure(
                compatible.Error ?? "The download cannot be imported.", compatible.ErrorTitle ?? "Import unavailable");
        var source = compatible.Value.FirstOrDefault(item => item.Id.Equals(sourceId, StringComparison.Ordinal));
        if (source is null)
            return Result<VideoSourceRefreshResult>.Failure(
                "The selected source does not contain the completed download.", "Import unavailable");

        // RefreshSourceAsync performs a non-destructive scan. We intentionally never call
        // ScanFolderAsync here because that API creates a new source when one is missing.
        return await _videoLibrary.RefreshSourceAsync(source.Id, ct);
    }

    private static bool TryGetLocalContentPath(QbittorrentTorrent task, out string path)
    {
        path = string.Empty;
        var candidate = !string.IsNullOrWhiteSpace(task.ContentPath)
            ? task.ContentPath
            : task.SavePath;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        try
        {
            path = Path.GetFullPath(candidate);
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception) when (candidate is not null)
        {
            return false;
        }
    }

    private static bool IsWithin(string contentPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;
        try
        {
            var content = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentPath));
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
            if (content.Equals(source, StringComparison.OrdinalIgnoreCase))
                return true;
            var prefix = source + Path.DirectorySeparatorChar;
            return content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                    && content.StartsWith(source + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
