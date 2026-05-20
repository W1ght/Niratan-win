using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.Storage;

namespace Hoshi.Services.Novels;

internal sealed class NovelLibraryService : INovelLibraryService
{
    private readonly IDataService _dataService;
    private readonly INovelEpubImportService _epubImportService;
    private readonly ILogger<NovelLibraryService> _logger;

    public NovelLibraryService(
        IDataService dataService,
        INovelEpubImportService epubImportService,
        ILogger<NovelLibraryService> logger
    )
    {
        _dataService = dataService;
        _epubImportService = epubImportService;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<NovelBook>>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    ) =>
        await ExecuteAsync(
            async token =>
                Result<IReadOnlyList<NovelBook>>.Success(
                    await _dataService.GetNovelBooksAsync(queryText, token)
                ),
            "Error loading novels",
            ct
        );

    public async Task<Result<NovelBook>> ImportEpubAsync(
        string filePath,
        CancellationToken ct = default
    )
    {
        var importResult = await _epubImportService.ImportAsync(filePath, ct);
        if (!importResult.IsSuccess)
            return Result<NovelBook>.Failure(
                importResult.Error!,
                importResult.ErrorTitle ?? "Import failed"
            );

        return await ExecuteAsync(
            async token =>
            {
                var book = importResult.Value!.Book;
                await _dataService.UpsertNovelBookAsync(book, token);
                _logger.LogInformation("Imported novel EPUB {FilePath}", book.FilePath);
                return Result<NovelBook>.Success(book);
            },
            "Error saving novel",
            ct
        );
    }

    public async Task<Result<NovelBook?>> GetNovelBookAsync(
        string bookId,
        CancellationToken ct = default
    ) =>
        await ExecuteAsync(
            async token =>
                Result<NovelBook?>.Success(await _dataService.GetNovelBookAsync(bookId, token)),
            "Error loading novel",
            ct
        );

    public async Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.UpdateNovelLastOpenedAsync(bookId, DateTime.UtcNow, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating novel last opened time for {BookId}", bookId);
            return Result.Failure(ex.Message, "Error opening novel");
        }
    }

    public async Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default)
    {
        try
        {
            var bookResult = await GetNovelBookAsync(bookId, ct);
            if (!bookResult.IsSuccess || bookResult.Value == null)
                return Result.Failure("Book not found.", "Delete failed");

            var book = bookResult.Value;

            await _dataService.DeleteNovelBookAsync(bookId, ct);

            if (!string.IsNullOrEmpty(book.ExtractedPath) && Directory.Exists(book.ExtractedPath))
                Directory.Delete(book.ExtractedPath, true);

            _logger.LogInformation("Deleted novel '{Title}' ({Id})", book.Title, bookId);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting novel {BookId}", bookId);
            return Result.Failure(ex.Message, "Error deleting novel");
        }
    }

    public async Task<Result> SaveProgressAsync(
        string bookId,
        int chapterIndex,
        double progress,
        CancellationToken ct = default
    )
    {
        try
        {
            await _dataService.SaveNovelProgressAsync(bookId, chapterIndex, progress, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving progress for {BookId}", bookId);
            return Result.Failure(ex.Message, "Error saving progress");
        }
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> action,
        string errorTitle,
        CancellationToken ct
    )
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
