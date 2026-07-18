using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.DTO;

namespace Niratan.Services.Novels;

public sealed class NovelEpubImportService : INovelEpubImportService
{
    private readonly IEpubParserService _epubParser;
    private readonly INovelBookSidecarService _sidecars;
    private readonly IReaderImageGalleryService? _imageGallery;
    private readonly ILogger<NovelEpubImportService> _logger;
    private readonly Func<string, string> _bookRootResolver;

    public NovelEpubImportService(
        IEpubParserService epubParser,
        INovelBookSidecarService sidecars,
        ILogger<NovelEpubImportService> logger,
        IReaderImageGalleryService? imageGallery = null
    ) : this(epubParser, sidecars, logger, AppDataHelper.GetNovelBookPath, imageGallery)
    {
    }

    internal NovelEpubImportService(
        IEpubParserService epubParser,
        INovelBookSidecarService sidecars,
        ILogger<NovelEpubImportService> logger,
        Func<string, string> bookRootResolver,
        IReaderImageGalleryService? imageGallery = null)
    {
        _epubParser = epubParser;
        _sidecars = sidecars;
        _imageGallery = imageGallery;
        _logger = logger;
        _bookRootResolver = bookRootResolver;
    }

    public async Task<Result<NovelImportResult>> ImportAsync(
        string filePath,
        CancellationToken ct = default
    )
    {
        string? bookRoot = null;
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
            bookRoot = _bookRootResolver(bookId);
            Directory.CreateDirectory(bookRoot);
            var privateEpubPath = Path.Combine(bookRoot, bookId + ".epub");
            File.Copy(filePath, privateEpubPath, overwrite: false);

            var epubBook = await Task.Run(
                () => _epubParser.Parse(privateEpubPath, bookRoot),
                ct
            );
            var chapterCharacterCounts = await Task.Run(
                () => epubBook.Chapters
                    .Select(chapter => CountReadableCharacters(chapter.Href))
                    .ToArray(),
                ct
            );
            var galleryImages = _imageGallery == null
                ? null
                : await _imageGallery.LoadImagesAsync(epubBook, ct: ct);
            var bookInfo = _sidecars.CreateBookInfo(
                epubBook.Chapters,
                chapterCharacterCounts,
                epubBook.ContainerDirectory,
                galleryImages?.Select(image => image.RelativePath).ToArray()
            );
            await _sidecars.SaveBookInfoAsync(bookRoot, bookInfo, ct);

            var book = new NovelBook
            {
                Id = bookId,
                Title = epubBook.Title,
                OriginalTitle = epubBook.Title,
                Folder = bookId,
                Author = epubBook.Author,
                FilePath = privateEpubPath,
                CoverPath = epubBook.CoverHref != null
                    ? Path.Combine(epubBook.ContainerDirectory, epubBook.CoverHref)
                    : null,
                ExtractedPath = bookRoot,
                ChapterCount = epubBook.Chapters.Count,
                ImportedAt = DateTime.UtcNow,
                Language = epubBook.Language,
                UniqueIdentifier = epubBook.UniqueIdentifier,
            };

            return Result<NovelImportResult>.Success(new NovelImportResult(book));
        }
        catch (OperationCanceledException)
        {
            DeleteIncompleteRoot(bookRoot);
            return Result<NovelImportResult>.Cancelled();
        }
        catch (Exception ex)
        {
            DeleteIncompleteRoot(bookRoot);
            _logger.LogWarning(ex, "Failed to import EPUB {FilePath}", filePath);
            return Result<NovelImportResult>.Failure(ex.Message, "EPUB import failed");
        }
    }

    private static int CountReadableCharacters(string chapterPath)
    {
        if (!File.Exists(chapterPath))
            return 0;

        return ReaderTextFilter.CountReadableCharacters(File.ReadAllText(chapterPath));
    }

    private static void DeleteIncompleteRoot(string? bookRoot)
    {
        if (!string.IsNullOrWhiteSpace(bookRoot) && Directory.Exists(bookRoot))
            Directory.Delete(bookRoot, recursive: true);
    }
}
