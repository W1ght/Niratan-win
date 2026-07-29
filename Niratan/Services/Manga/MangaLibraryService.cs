using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaLibraryService : IMangaLibraryService
{
    private readonly MangaSourceIndexer _indexer;
    private readonly IMangaPageProvider _pageProvider;
    private readonly IMangaCatalogStore _catalogStore;
    private readonly ILogger<MangaLibraryService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MangaLibraryCatalog? _catalog;

    public MangaLibraryService(
        MangaSourceIndexer indexer,
        IMangaPageProvider pageProvider,
        IMangaCatalogStore catalogStore,
        ILogger<MangaLibraryService> logger)
    {
        _indexer = indexer;
        _pageProvider = pageProvider;
        _catalogStore = catalogStore;
        _logger = logger;
    }

    public Task<Result<IReadOnlyList<MangaBook>>> GetBooksAsync(CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<MangaBook>>(
            async token =>
            {
                var catalog = await LoadCatalogAsync(token);
                return catalog.Books
                    .Where(book => !book.IsHidden)
                    .OrderByDescending(book => book.LastReadAt ?? book.ImportedAt)
                    .ToList();
            },
            "Unable to load manga library.",
            ct);

    public Task<Result<MangaBook?>> GetBookAsync(
        string bookId,
        CancellationToken ct = default) =>
        ExecuteAsync<MangaBook?>(
            async token => (await LoadCatalogAsync(token)).Books
                .FirstOrDefault(book => book.Id == bookId && !book.IsHidden),
            "Unable to load manga.",
            ct);

    public Task<Result<MangaBook>> ImportAsync(
        string sourcePath,
        CancellationToken ct = default) =>
        ExecuteAsync(
            async token =>
            {
                var indexed = await _indexer.IndexAsync(sourcePath, token);
                await _gate.WaitAsync(token);
                try
                {
                    var catalog = await LoadCatalogCoreAsync(token);
                    var existing = catalog.Books.FirstOrDefault(book => book.Id == indexed.Id);
                    if (existing is not null)
                    {
                        indexed.ImportedAt = existing.ImportedAt;
                        indexed.CurrentPageIndex = Math.Clamp(
                            existing.CurrentPageIndex,
                            0,
                            Math.Max(0, indexed.PageCount - 1));
                        indexed.LastReadAt = existing.LastReadAt;
                        indexed.RenamedTitle = existing.RenamedTitle;
                        catalog.Books.Remove(existing);
                    }

                    catalog.Books.Add(indexed);
                    await PrepareCoverAsync(indexed, token);
                    await SaveCatalogCoreAsync(catalog, token);
                }
                finally
                {
                    _gate.Release();
                }

                _logger.LogInformation(
                    "Imported manga {Title} from read-only source {SourcePath}",
                    indexed.DisplayTitle,
                    indexed.SourcePath);
                return indexed;
            },
            "Unable to import manga.",
            ct);

    public Task<Result> SaveProgressAsync(
        string bookId,
        int pageIndex,
        CancellationToken ct = default) =>
        UpdateBookAsync(
            bookId,
            book =>
            {
                book.CurrentPageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, book.PageCount - 1));
                book.LastReadAt = DateTimeOffset.UtcNow;
            },
            "Unable to save manga progress.",
            ct);

    public async Task<Result> SaveReaderPreferencesAsync(
        MangaReaderPreferences preferences,
        CancellationToken ct = default)
    {
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                var catalog = await LoadCatalogCoreAsync(ct);
                catalog.ReaderPreferences = new MangaReaderPreferences
                {
                    Layout = preferences.Layout,
                    Direction = preferences.Direction,
                    ZoomPercentage = Math.Clamp(preferences.ZoomPercentage, 50, 200),
                    IsGoogleOcrEnabled = preferences.IsGoogleOcrEnabled,
                    GoogleOcrDisclosureAccepted = preferences.GoogleOcrDisclosureAccepted,
                };
                await SaveCatalogCoreAsync(catalog, ct);
            }
            finally
            {
                _gate.Release();
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to save manga reader preferences");
            return Result.Failure(ex.Message, "Manga reader settings");
        }
    }

    public Task<Result> RenameAsync(
        string bookId,
        string title,
        CancellationToken ct = default) =>
        UpdateBookAsync(
            bookId,
            book => book.RenamedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            "Unable to rename manga.",
            ct);

    public Task<Result> MarkReadAsync(string bookId, CancellationToken ct = default) =>
        UpdateBookAsync(
            bookId,
            book =>
            {
                book.CurrentPageIndex = Math.Max(0, book.PageCount - 1);
                book.LastReadAt = DateTimeOffset.UtcNow;
            },
            "Unable to mark manga as read.",
            ct);

    public Task<Result> RemoveAsync(string bookId, CancellationToken ct = default) =>
        UpdateBookAsync(
            bookId,
            book => book.IsHidden = true,
            "Unable to remove manga.",
            ct);

    public Task<Result<MangaReaderSession?>> CreateReaderSessionAsync(
        string bookId,
        CancellationToken ct = default) =>
        ExecuteAsync<MangaReaderSession?>(
            async token =>
            {
                var catalog = await LoadCatalogAsync(token);
                var book = catalog.Books.FirstOrDefault(
                    item => item.Id == bookId && !item.IsHidden);
                return book is null
                    ? null
                    : new MangaReaderSession(
                        book,
                        new MangaReaderPreferences
                        {
                            Layout = catalog.ReaderPreferences.Layout,
                            Direction = catalog.ReaderPreferences.Direction,
                            ZoomPercentage = catalog.ReaderPreferences.ZoomPercentage,
                            IsGoogleOcrEnabled =
                                catalog.ReaderPreferences.IsGoogleOcrEnabled,
                            GoogleOcrDisclosureAccepted =
                                catalog.ReaderPreferences.GoogleOcrDisclosureAccepted,
                        });
            },
            "Unable to prepare manga reader.",
            ct);

    public Task<Result<MangaReaderSession>> CreateReaderSessionAsync(
        MangaBook book,
        CancellationToken ct = default) =>
        ExecuteAsync(
            async token =>
            {
                ArgumentNullException.ThrowIfNull(book);
                var catalog = await LoadCatalogAsync(token);
                var resolved = book.ContainerKind is
                    MangaContainerKind.Suwayomi or MangaContainerKind.Mihon
                    ? book
                    : catalog.Books.FirstOrDefault(
                        item => item.Id == book.Id && !item.IsHidden)
                      ?? throw new InvalidOperationException("Manga could not be found.");
                return new MangaReaderSession(
                    resolved,
                    new MangaReaderPreferences
                    {
                        Layout = catalog.ReaderPreferences.Layout,
                        Direction = catalog.ReaderPreferences.Direction,
                        ZoomPercentage = catalog.ReaderPreferences.ZoomPercentage,
                        IsGoogleOcrEnabled =
                            catalog.ReaderPreferences.IsGoogleOcrEnabled,
                        GoogleOcrDisclosureAccepted =
                            catalog.ReaderPreferences.GoogleOcrDisclosureAccepted,
                    });
            },
            "Unable to prepare manga reader.",
            ct);

    private async Task<Result> UpdateBookAsync(
        string bookId,
        Action<MangaBook> update,
        string failureMessage,
        CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                var catalog = await LoadCatalogCoreAsync(ct);
                var book = catalog.Books.FirstOrDefault(item => item.Id == bookId);
                if (book is null)
                    return Result.Failure("Manga not found.", "Manga library");
                update(book);
                await SaveCatalogCoreAsync(catalog, ct);
            }
            finally
            {
                _gate.Release();
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{FailureMessage}", failureMessage);
            return Result.Failure(ex.Message, "Manga library");
        }
    }

    private async Task PrepareCoverAsync(MangaBook book, CancellationToken ct)
    {
        var pagePath = await _pageProvider.GetPagePathAsync(book, 0, ct);
        var coverDirectory = Path.Combine(AppDataHelper.GetMangaCachePath(), book.Id);
        Directory.CreateDirectory(coverDirectory);
        var coverPath = Path.Combine(
            coverDirectory,
            "cover" + MangaPathUtility.SafeExtension(pagePath));
        if (!string.Equals(
            Path.GetFullPath(pagePath),
            Path.GetFullPath(coverPath),
            StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(pagePath, coverPath, overwrite: true);
        }

        book.CoverCachePath = coverPath;
    }

    private async Task<MangaLibraryCatalog> LoadCatalogAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadCatalogCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MangaLibraryCatalog> LoadCatalogCoreAsync(CancellationToken ct)
    {
        if (_catalog is not null)
            return _catalog;

        _catalog = await _catalogStore.LoadAsync(ct);
        _catalog.Books ??= [];
        _catalog.ReaderPreferences ??= new MangaReaderPreferences();
        return _catalog;
    }

    private Task SaveCatalogCoreAsync(MangaLibraryCatalog catalog, CancellationToken ct)
    {
        _catalog = catalog;
        return _catalogStore.SaveAsync(catalog, ct);
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string failureMessage,
        CancellationToken ct)
    {
        try
        {
            return Result<T>.Success(await action(ct));
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{FailureMessage}", failureMessage);
            return Result<T>.Failure(ex.Message, "Manga library");
        }
    }
}
