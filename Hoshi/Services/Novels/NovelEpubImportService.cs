using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;

namespace Hoshi.Services.Novels;

public sealed class NovelEpubImportService : INovelEpubImportService
{
    private readonly IEpubParserService _epubParser;
    private readonly ILogger<NovelEpubImportService> _logger;

    public NovelEpubImportService(
        IEpubParserService epubParser,
        ILogger<NovelEpubImportService> logger
    )
    {
        _epubParser = epubParser;
        _logger = logger;
    }

    public async Task<Result<NovelImportResult>> ImportAsync(
        string filePath,
        CancellationToken ct = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return Result<NovelImportResult>.Failure("The selected EPUB file does not exist.");

            if (
                !string.Equals(
                    Path.GetExtension(filePath),
                    ".epub",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return Result<NovelImportResult>.Failure("Please select a .epub file.");

            var bookId = Guid.NewGuid().ToString("N");
            var extractedPath = AppDataHelper.GetNovelBookPath(bookId);

            var epubBook = await Task.Run(
                () => _epubParser.Parse(filePath, extractedPath),
                ct
            );

            var book = new NovelBook
            {
                Id = bookId,
                Title = epubBook.Title,
                Author = epubBook.Author,
                FilePath = Path.GetFullPath(filePath),
                CoverPath = epubBook.CoverHref != null
                    ? Path.Combine(epubBook.ContainerDirectory, epubBook.CoverHref)
                    : null,
                ExtractedPath = extractedPath,
                ChapterCount = epubBook.Chapters.Count,
                ImportedAt = DateTime.UtcNow,
                Language = epubBook.Language,
                UniqueIdentifier = epubBook.UniqueIdentifier,
            };

            return Result<NovelImportResult>.Success(new NovelImportResult(book));
        }
        catch (OperationCanceledException)
        {
            return Result<NovelImportResult>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import EPUB {FilePath}", filePath);
            return Result<NovelImportResult>.Failure(ex.Message, "EPUB import failed");
        }
    }
}
