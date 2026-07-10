using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;
using Microsoft.Extensions.Logging;

namespace Hoshi.Services.Novels;

internal sealed class NovelLibraryService : INovelLibraryService
{
    private readonly INovelBookStorageService _storage;
    private readonly INovelBookSidecarService _sidecars;
    private readonly INovelStorageAccessState _accessState;
    private readonly INovelEpubImportService _epubImportService;
    private readonly ILogger<NovelLibraryService> _logger;

    public NovelLibraryService(
        INovelBookStorageService storage,
        INovelBookSidecarService sidecars,
        INovelStorageAccessState accessState,
        INovelEpubImportService epubImportService,
        ILogger<NovelLibraryService> logger)
    {
        _storage = storage;
        _sidecars = sidecars;
        _accessState = accessState;
        _epubImportService = epubImportService;
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

                await _storage.DeleteAsync(bookId, token);
                _logger.LogInformation("Deleted novel '{Title}' ({Id})", book.Title, bookId);
                return Result.Success();
            },
            "Error deleting novel",
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
