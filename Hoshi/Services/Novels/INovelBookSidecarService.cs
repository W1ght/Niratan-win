using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelBookSidecarService
{
    Task<NovelBookmark?> LoadBookmarkAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task SaveBookmarkAsync(
        string bookRootPath,
        NovelBookmark bookmark,
        CancellationToken ct = default);

    Task<NovelBookInfo?> LoadBookInfoAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task SaveBookInfoAsync(
        string bookRootPath,
        NovelBookInfo bookInfo,
        CancellationToken ct = default);

    NovelBookInfo CreateBookInfo(
        IReadOnlyList<EpubChapter> chapters,
        IReadOnlyList<int> chapterCharacterCounts,
        string? containerDirectory);
}
