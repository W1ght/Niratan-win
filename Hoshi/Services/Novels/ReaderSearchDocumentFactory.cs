using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public static class ReaderSearchDocumentFactory
{
    public static async Task<ReaderSearchDocument> CreateAsync(
        EpubBook book,
        IReadOnlyList<int> chapterCharacterCounts,
        CancellationToken ct = default)
    {
        var chapters = new List<ReaderSearchChapter>();
        var htmlByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labels = BuildChapterLabels(book);
        var currentTotal = 0;

        for (var i = 0; i < book.Chapters.Count; i++)
        {
            var chapter = book.Chapters[i];
            var chapterCount = i < chapterCharacterCounts.Count
                ? chapterCharacterCounts[i]
                : 0;
            chapters.Add(new ReaderSearchChapter(
                i,
                chapter.Href,
                currentTotal,
                chapterCount));

            if (File.Exists(chapter.Href))
                htmlByPath[chapter.Href] = await File.ReadAllTextAsync(chapter.Href, ct);
            else
                htmlByPath[chapter.Href] = "";

            currentTotal += chapterCount;
        }

        return new ReaderSearchDocument(chapters, htmlByPath, labels);
    }

    private static IReadOnlyDictionary<int, string> BuildChapterLabels(EpubBook book)
    {
        var labels = new Dictionary<int, string>();
        var pathToSpineIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < book.Chapters.Count; i++)
        {
            var normalized = NormalizeHref(book.Chapters[i].Href);
            if (normalized != null)
                pathToSpineIndex[normalized] = i;
        }

        void Walk(IEnumerable<EpubTocItem> items, string? topLabel)
        {
            foreach (var item in items)
            {
                var label = string.IsNullOrWhiteSpace(topLabel)
                    ? item.Label
                    : topLabel;
                var normalized = NormalizeHref(item.Href);
                if (normalized != null
                    && pathToSpineIndex.TryGetValue(normalized, out var spineIndex)
                    && !labels.ContainsKey(spineIndex))
                {
                    labels[spineIndex] = label;
                }

                Walk(item.Children, label);
            }
        }

        Walk(book.Toc, null);
        return labels;
    }

    private static string? NormalizeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        var withoutFragment = href.Split('#')[0];
        if (string.IsNullOrWhiteSpace(withoutFragment))
            return null;

        return Path.GetFullPath(Uri.UnescapeDataString(withoutFragment));
    }
}
