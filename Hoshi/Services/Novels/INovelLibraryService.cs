using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelLibraryService
{
    Task<Result<NovelBookCatalogSnapshot>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    );

    Task<Result<NovelBook>> ImportEpubAsync(string filePath, CancellationToken ct = default);

    Task<Result> ExportEpubAsync(
        string bookId,
        string destinationPath,
        CancellationToken ct = default);

    Task<Result<NovelBook?>> GetNovelBookAsync(string bookId, CancellationToken ct = default);

    Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default);

    Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default);

    Task<Result> SaveProgressAsync(
        string bookId,
        int chapterIndex,
        double progress,
        int currentCharacterCount,
        int totalCharacterCount,
        CancellationToken ct = default
    );

    Task<Result> SaveNovelBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default
    );

    Task<Result> SetNovelProfileAsync(
        string bookId,
        string? profileId,
        CancellationToken ct = default
    );
}
