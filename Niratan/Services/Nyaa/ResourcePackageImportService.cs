using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Services.Novels;
using Niratan.Services.Sasayaki;
using Niratan.Services.Settings;
using Niratan.Services.Video;

namespace Niratan.Services.Nyaa;

public sealed class ResourcePackageImportService : IResourcePackageImportService
{
    private readonly ResourcePackageAnalyzer _analyzer;
    private readonly INovelLibraryService _novelLibraryService;
    private readonly INovelBookStorageService _novelStorageService;
    private readonly ISasayakiMatchService _sasayakiMatchService;
    private readonly IVideoLibraryService _videoLibraryService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ResourcePackageImportService> _logger;

    public ResourcePackageImportService(
        ResourcePackageAnalyzer analyzer,
        INovelLibraryService novelLibraryService,
        INovelBookStorageService novelStorageService,
        ISasayakiMatchService sasayakiMatchService,
        IVideoLibraryService videoLibraryService,
        ISettingsService settingsService,
        ILogger<ResourcePackageImportService> logger)
    {
        _analyzer = analyzer;
        _novelLibraryService = novelLibraryService;
        _novelStorageService = novelStorageService;
        _sasayakiMatchService = sasayakiMatchService;
        _videoLibraryService = videoLibraryService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public ResourcePackageAnalysis Analyze(string rootPath) => _analyzer.Analyze(rootPath);

    public async Task<Result<ResourcePackageImportResult>> ImportAsync(
        string rootPath,
        CancellationToken ct = default)
    {
        ResourcePackageAnalysis analysis;
        try
        {
            analysis = Analyze(rootPath);
        }
        catch (Exception ex)
        {
            return Result<ResourcePackageImportResult>.Failure(ex.Message, "Resource package analysis failed");
        }

        if (analysis.EpubFiles.Count == 0 && analysis.VideoFiles.Count == 0)
        {
            return Result<ResourcePackageImportResult>.Failure(
                "The download did not contain a supported EPUB or video file.",
                "Nothing to import");
        }

        var warnings = analysis.Warnings.ToList();
        var importedNovels = new Dictionary<string, NovelBook>(StringComparer.OrdinalIgnoreCase);
        var importedVideoCount = 0;
        var matchedNovelCount = 0;

        foreach (var epubPath in analysis.EpubFiles)
        {
            if (ct.IsCancellationRequested)
                return Result<ResourcePackageImportResult>.Cancelled();
            var result = await _novelLibraryService.ImportEpubAsync(epubPath, ct);
            if (result.IsCancelled)
                return Result<ResourcePackageImportResult>.Cancelled();
            if (!result.IsSuccess)
            {
                warnings.Add($"EPUB import failed for '{Path.GetFileName(epubPath)}': {result.Error}");
                continue;
            }

            importedNovels[epubPath] = result.Value!;
        }

        if (analysis.NovelMatch is not null
            && importedNovels.TryGetValue(analysis.NovelMatch.EpubPath, out var matchedBook))
        {
            try
            {
                var resources = await CopyNovelResourcesAsync(
                    matchedBook,
                    analysis.NovelMatch,
                    ct);
                var match = await _sasayakiMatchService.MatchAsync(
                    matchedBook,
                    resources.AudiobookPath,
                    resources.SubtitlePath,
                    _settingsService.Current.SasayakiSettings.SearchWindowSize,
                    ct);
                if (match.IsValid)
                {
                    matchedNovelCount++;
                }
                else
                {
                    warnings.Add(
                        $"Sasayaki could not align any subtitle cues for '{matchedBook.Title}'. "
                        + "The audiobook and SRT were kept for manual review.");
                }
            }
            catch (OperationCanceledException)
            {
                return Result<ResourcePackageImportResult>.Cancelled();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic Sasayaki resource matching failed for {BookId}", matchedBook.Id);
                warnings.Add($"Automatic audiobook/SRT matching failed for '{matchedBook.Title}': {ex.Message}");
            }
        }

        foreach (var videoPath in analysis.VideoFiles)
        {
            if (ct.IsCancellationRequested)
                return Result<ResourcePackageImportResult>.Cancelled();
            var result = await _videoLibraryService.ImportVideoAsync(videoPath, ct);
            if (result.IsCancelled)
                return Result<ResourcePackageImportResult>.Cancelled();
            if (!result.IsSuccess)
            {
                warnings.Add($"Video import failed for '{Path.GetFileName(videoPath)}': {result.Error}");
                continue;
            }

            importedVideoCount++;
            if (analysis.VideoSubtitleMatches.TryGetValue(videoPath, out var subtitlePath)
                && !string.Equals(
                    result.Value!.SubtitlePath,
                    subtitlePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                var subtitleResult = await _videoLibraryService.UpdateVideoDetailsAsync(
                    result.Value.Id,
                    result.Value.Title,
                    SplitTags(result.Value.Tags),
                    subtitlePath,
                    ct);
                if (subtitleResult.IsCancelled)
                    return Result<ResourcePackageImportResult>.Cancelled();
                if (!subtitleResult.IsSuccess && !subtitleResult.IsCancelled)
                {
                    warnings.Add(
                        $"Subtitle binding failed for '{Path.GetFileName(videoPath)}': "
                        + subtitleResult.Error);
                }
            }
        }

        return Result<ResourcePackageImportResult>.Success(new ResourcePackageImportResult(
            importedNovels.Count,
            matchedNovelCount,
            importedVideoCount,
            warnings));
    }

    private async Task<(string AudiobookPath, string SubtitlePath)> CopyNovelResourcesAsync(
        NovelBook book,
        NovelResourceMatch match,
        CancellationToken ct)
    {
        var bookRoot = _novelStorageService.ResolveRootPath(book.Id);
        var resourceRoot = Path.Combine(bookRoot, "Resources", "Sasayaki");
        Directory.CreateDirectory(resourceRoot);
        var audioDestination = Path.Combine(
            resourceRoot,
            "audiobook" + Path.GetExtension(match.AudiobookPath).ToLowerInvariant());
        var subtitleDestination = Path.Combine(resourceRoot, "subtitles.srt");
        await CopyFileAsync(match.AudiobookPath, audioDestination, ct);
        await CopyFileAsync(match.SubtitlePath, subtitleDestination, ct);
        return (audioDestination, subtitleDestination);
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (string.Equals(
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(destinationPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, ct);
    }

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
