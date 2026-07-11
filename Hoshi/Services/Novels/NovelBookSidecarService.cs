using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class NovelBookSidecarService : INovelBookSidecarService
{
    public const string BookmarkFileName = "bookmark.json";
    public const string BookInfoFileName = "bookinfo.json";

    private readonly INiratanJsonFileStore _json;

    public NovelBookSidecarService()
        : this(new NiratanJsonFileStore())
    {
    }

    internal NovelBookSidecarService(INiratanJsonFileStore json)
    {
        _json = json ?? throw new ArgumentNullException(nameof(json));
    }

    public Task<NovelBookmark?> LoadBookmarkAsync(
        string bookRootPath,
        CancellationToken ct = default) =>
        TryReadAsync<NovelBookmark>(Path.Combine(bookRootPath, BookmarkFileName), ct);

    public Task SaveBookmarkAsync(
        string bookRootPath,
        NovelBookmark bookmark,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return _json.WriteAsync(Path.Combine(bookRootPath, BookmarkFileName), bookmark, ct);
    }

    public Task<NovelBookInfo?> LoadBookInfoAsync(
        string bookRootPath,
        CancellationToken ct = default) =>
        TryReadAsync<NovelBookInfo>(Path.Combine(bookRootPath, BookInfoFileName), ct);

    public Task SaveBookInfoAsync(
        string bookRootPath,
        NovelBookInfo bookInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookInfo);
        return _json.WriteAsync(Path.Combine(bookRootPath, BookInfoFileName), bookInfo, ct);
    }

    public NovelBookInfo CreateBookInfo(
        IReadOnlyList<EpubChapter> chapters,
        IReadOnlyList<int> chapterCharacterCounts,
        string? containerDirectory)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(chapterCharacterCounts);

        var chapterInfo = new Dictionary<string, NovelBookInfoChapter>(StringComparer.Ordinal);
        var currentTotal = 0;
        var count = Math.Min(chapters.Count, chapterCharacterCounts.Count);
        for (var i = 0; i < count; i++)
        {
            var chapterCount = Math.Max(0, chapterCharacterCounts[i]);
            var key = GetChapterInfoKey(chapters[i], containerDirectory, i);
            chapterInfo[key] = new NovelBookInfoChapter(
                chapters[i].SpineIndex,
                currentTotal,
                chapterCount);
            currentTotal += chapterCount;
        }

        return new NovelBookInfo(currentTotal, chapterInfo);
    }

    private async Task<T?> TryReadAsync<T>(
        string path,
        CancellationToken ct)
        where T : class
    {
        var result = await _json.ReadAsync<T>(path, ct);
        return result.Status == NovelJsonReadStatus.Success ? result.Value : null;
    }

    private static string GetChapterInfoKey(
        EpubChapter chapter,
        string? containerDirectory,
        int index)
    {
        if (!string.IsNullOrWhiteSpace(chapter.Href))
        {
            var href = chapter.Href.Split('#')[0];
            if (!string.IsNullOrWhiteSpace(containerDirectory) && Path.IsPathRooted(href))
            {
                var relativePath = Path.GetRelativePath(containerDirectory, href);
                if (!relativePath.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relativePath))
                {
                    return NormalizePath(relativePath);
                }
            }

            return NormalizePath(href);
        }

        return index.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

}
