using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelBookStorageService
{
    Task<NovelBookCatalogSnapshot> LoadSnapshotAsync(
        string? queryText = null,
        CancellationToken ct = default);

    Task<NovelBook?> LoadAsync(string bookId, CancellationToken ct = default);

    Task SaveMetadataAsync(NovelBook book, CancellationToken ct = default);

    Task UpdateLastAccessAsync(
        string bookId,
        DateTimeOffset lastAccess,
        CancellationToken ct = default);

    Task UpdateProfileAsync(
        string bookId,
        string? profileId,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> LoadBookOrderAsync(CancellationToken ct = default);

    Task SaveBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default);

    Task DeleteAsync(string bookId, CancellationToken ct = default);

    string ResolveRootPath(string bookId);
}
