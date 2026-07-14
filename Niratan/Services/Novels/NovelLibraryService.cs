using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Novel;
using Microsoft.Extensions.Logging;

namespace Niratan.Services.Novels;

internal sealed class NovelLibraryService : INovelLibraryService
{
    private readonly INovelBookStorageService _storage;
    private readonly INovelBookSidecarService _sidecars;
    private readonly INovelStorageAccessState _accessState;
    private readonly INovelEpubImportService _epubImportService;
    private readonly INovelShelfService _shelves;
    private readonly ILogger<NovelLibraryService> _logger;

    public NovelLibraryService(
        INovelBookStorageService storage,
        INovelBookSidecarService sidecars,
        INovelStorageAccessState accessState,
        INovelEpubImportService epubImportService,
        INovelShelfService shelves,
        ILogger<NovelLibraryService> logger)
    {
        _storage = storage;
        _sidecars = sidecars;
        _accessState = accessState;
        _epubImportService = epubImportService;
        _shelves = shelves;
        _logger = logger;
    }

    public Task<Result<NovelBookCatalogSnapshot>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default) =>
        ExecuteAsync(
            async token => Result<NovelBookCatalogSnapshot>.Success(
                await _storage.LoadSnapshotAsync(queryText, token)),
            "Error loading novels",
            ct);

    public async Task<Result<NovelBook>> ImportEpubAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure<NovelBook>();
        if (readOnly is not null)
            return readOnly;

        var importResult = await _epubImportService.ImportAsync(filePath, ct);
        if (!importResult.IsSuccess)
        {
            return importResult.IsCancelled
                ? Result<NovelBook>.Cancelled()
                : Result<NovelBook>.Failure(
                    importResult.Error!,
                    importResult.ErrorTitle ?? "Import failed");
        }

        var book = importResult.Value!.Book;
        try
        {
            await _storage.SaveMetadataAsync(book, ct);
            _logger.LogInformation("Imported novel EPUB {FilePath}", book.FilePath);
            return Result<NovelBook>.Success(book);
        }
        catch (OperationCanceledException)
        {
            await TryDeleteIncompleteImportAsync(book.Id);
            return Result<NovelBook>.Cancelled();
        }
        catch (Exception ex)
        {
            await TryDeleteIncompleteImportAsync(book.Id);
            _logger.LogError(ex, "Error saving imported novel {BookId}", book.Id);
            return Result<NovelBook>.Failure(ex.Message, "Error saving novel");
        }
    }

    public Task<Result<NovelBook?>> GetNovelBookAsync(
        string bookId,
        CancellationToken ct = default) =>
        ExecuteAsync(
            async token => Result<NovelBook?>.Success(await _storage.LoadAsync(bookId, token)),
            "Error loading novel",
            ct);

    public Task<Result> ExportEpubAsync(
        string bookId,
        string destinationPath,
        CancellationToken ct = default) =>
        ExecuteAsync(
            async token =>
            {
                if (string.IsNullOrWhiteSpace(destinationPath)
                    || !string.Equals(
                        Path.GetExtension(destinationPath),
                        ".epub",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure(
                        "Choose a valid .epub destination.",
                        "EPUB export failed");
                }

                var book = await _storage.LoadAsync(bookId, token);
                if (book is null)
                    return Result.Failure("Book not found.", "EPUB export failed");
                if (string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath))
                {
                    return Result.Failure(
                        "The private EPUB file no longer exists.",
                        "EPUB file not found");
                }

                var sourcePath = Path.GetFullPath(book.FilePath);
                var targetPath = Path.GetFullPath(destinationPath);
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure(
                        "The export destination must differ from the private EPUB.",
                        "EPUB export failed");
                }

                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
                {
                    return Result.Failure(
                        "The export folder does not exist.",
                        "EPUB export failed");
                }

                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);
                await using var target = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await source.CopyToAsync(target, token);
                _logger.LogInformation(
                    "Exported novel EPUB {BookId} to {DestinationPath}",
                    bookId,
                    targetPath);
                return Result.Success();
            },
            "EPUB export failed",
            ct);

    public async Task<Result> MarkOpenedAsync(
        string bookId,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                await _storage.UpdateLastAccessAsync(bookId, DateTimeOffset.UtcNow, token);
                return Result.Success();
            },
            "Error opening novel",
            ct);
    }

    public async Task<Result> DeleteNovelAsync(
        string bookId,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                var book = await _storage.LoadAsync(bookId, token);
                if (book is null)
                    return Result.Failure("Book not found.", "Delete failed");

                var shelfResult = await _shelves.RemoveBookAsync(bookId, token);
                if (!shelfResult.IsSuccess)
                {
                    return shelfResult.IsCancelled
                        ? Result.Cancelled()
                        : Result.Failure(
                            shelfResult.Error!,
                            shelfResult.ErrorTitle ?? "Delete failed");
                }

                await _storage.DeleteAsync(bookId, token);
                _logger.LogInformation("Deleted novel '{Title}' ({Id})", book.Title, bookId);
                return Result.Success();
            },
            "Error deleting novel",
            ct);
    }

    public async Task<Result> MarkReadAsync(
        string bookId,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                var rootPath = _storage.ResolveRootPath(bookId);
                var bookInfo = await _sidecars.LoadBookInfoAsync(rootPath, token);
                if (bookInfo is null)
                    return Result.Success();

                var finalChapterIndex = bookInfo.ChapterInfo.Values
                    .Where(chapter => chapter.SpineIndex.HasValue)
                    .Select(chapter => chapter.SpineIndex!.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                await _sidecars.SaveBookmarkAsync(
                    rootPath,
                    new NovelBookmark(
                        finalChapterIndex,
                        1,
                        bookInfo.CharacterCount,
                        DateTimeOffset.UtcNow),
                    token);
                return Result.Success();
            },
            "Error marking novel as read",
            ct);
    }

    public async Task<Result> SaveProgressAsync(
        string bookId,
        int chapterIndex,
        double progress,
        int currentCharacterCount,
        int totalCharacterCount,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                await _sidecars.SaveBookmarkAsync(
                    _storage.ResolveRootPath(bookId),
                    new NovelBookmark(
                        chapterIndex,
                        Math.Clamp(progress, 0, 1),
                        Math.Max(0, currentCharacterCount),
                        DateTimeOffset.UtcNow),
                    token);
                return Result.Success();
            },
            "Error saving progress",
            ct);
    }

    public async Task<Result> SaveNovelBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                await _storage.SaveBookOrderAsync(orderedBookIds, token);
                return Result.Success();
            },
            "Error saving novel order",
            ct);
    }

    public async Task<Result> SetNovelProfileAsync(
        string bookId,
        string? profileId,
        CancellationToken ct = default)
    {
        var readOnly = ReadOnlyFailure();
        if (readOnly is not null)
            return readOnly;

        return await ExecuteAsync(
            async token =>
            {
                await _storage.UpdateProfileAsync(bookId, profileId, token);
                return Result.Success();
            },
            "Error saving novel profile",
            ct);
    }

    private Result? ReadOnlyFailure() =>
        _accessState.IsReadOnly
            ? Result.Failure(
                _accessState.ErrorMessage ?? "Novel storage migration requires recovery.",
                "Novel library is read-only")
            : null;

    private Result<T>? ReadOnlyFailure<T>() =>
        _accessState.IsReadOnly
            ? Result<T>.Failure(
                _accessState.ErrorMessage ?? "Novel storage migration requires recovery.",
                "Novel library is read-only")
            : null;

    private async Task TryDeleteIncompleteImportAsync(string bookId)
    {
        try
        {
            await _storage.DeleteAsync(bookId, CancellationToken.None);
        }
        catch (Exception cleanupError)
        {
            _logger.LogWarning(
                cleanupError,
                "Failed to clean incomplete novel import {BookId}",
                bookId);
        }
    }

    private async Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> action,
        string errorTitle,
        CancellationToken ct)
    {
        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ErrorTitle}", errorTitle);
            return Result.Failure(ex.Message, errorTitle);
        }
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
