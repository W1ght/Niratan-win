using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public sealed class ReaderImageGalleryService : IReaderImageGalleryService
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".jpg", ".jpeg", ".png"],
        StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ReaderGalleryImage>> LoadImagesAsync(
        EpubBook book,
        IReadOnlyList<string>? cachedRelativePaths = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        return Task.Run<IReadOnlyList<ReaderGalleryImage>>(
            () =>
            {
                var scanned = ScanImages(book, ct);
                return scanned.Count > 0 || cachedRelativePaths is null
                    ? scanned
                    : ResolveCachedImages(book, cachedRelativePaths, ct);
            },
            ct);
    }

    private static IReadOnlyList<ReaderGalleryImage> ScanImages(
        EpubBook book,
        CancellationToken ct)
    {
        if (!TryGetContentRoot(book, out var contentRoot))
            return [];

        var images = new List<ReaderGalleryImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in book.Chapters.OrderBy(chapter => chapter.SpineIndex))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryResolveContainedFile(contentRoot, chapter.Href, out var chapterPath)
                || !File.Exists(chapterPath))
            {
                continue;
            }

            HtmlDocument document;
            string html;
            try
            {
                html = File.ReadAllText(chapterPath);
                document = new HtmlDocument();
                document.LoadHtml(html);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var node in document.DocumentNode.Descendants()
                         .Where(node => node.Name.Equals("img", StringComparison.OrdinalIgnoreCase)
                             || node.Name.Equals("image", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                if (HasClass(node, "gaiji"))
                    continue;

                var source = node.Attributes["src"]?.Value
                    ?? node.Attributes["xlink:href"]?.Value
                    ?? node.Attributes["href"]?.Value;
                if (!TryResolveImage(
                        contentRoot,
                        chapterPath,
                        source,
                        chapter.SpineIndex,
                        CalculateChapterProgress(html, node.StreamPosition),
                        out var image)
                    || !seen.Add(image.RelativePath))
                {
                    continue;
                }

                images.Add(image);
            }
        }

        return images;
    }

    private static IReadOnlyList<ReaderGalleryImage> ResolveCachedImages(
        EpubBook book,
        IReadOnlyList<string> cachedRelativePaths,
        CancellationToken ct)
    {
        if (!TryGetContentRoot(book, out var contentRoot))
            return [];

        var images = new List<ReaderGalleryImage>(cachedRelativePaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in cachedRelativePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryResolveContainedFile(contentRoot, relativePath, out var filePath)
                || !SupportedExtensions.Contains(Path.GetExtension(filePath))
                || !File.Exists(filePath))
            {
                continue;
            }

            var normalized = NormalizeRelativePath(Path.GetRelativePath(contentRoot, filePath));
            if (seen.Add(normalized))
                images.Add(new ReaderGalleryImage(normalized, filePath, -1, 0));
        }

        return images;
    }

    private static bool TryResolveImage(
        string contentRoot,
        string chapterPath,
        string? source,
        int spineIndex,
        double chapterProgress,
        out ReaderGalleryImage image)
    {
        image = null!;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var decoded = HtmlEntity.DeEntitize(source).Trim();
        var suffixIndex = decoded.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
            decoded = decoded[..suffixIndex];
        if (string.IsNullOrWhiteSpace(decoded)
            || decoded.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(decoded, UriKind.Absolute, out _))
        {
            return false;
        }

        try
        {
            decoded = Uri.UnescapeDataString(decoded).Replace('/', Path.DirectorySeparatorChar);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var chapterDirectory = Path.GetDirectoryName(chapterPath) ?? contentRoot;
        if (!TryResolveContainedFile(contentRoot, Path.Combine(chapterDirectory, decoded), out var filePath)
            || !SupportedExtensions.Contains(Path.GetExtension(filePath))
            || !File.Exists(filePath))
        {
            return false;
        }

        image = new ReaderGalleryImage(
            NormalizeRelativePath(Path.GetRelativePath(contentRoot, filePath)),
            filePath,
            spineIndex,
            chapterProgress);
        return true;
    }

    private static double CalculateChapterProgress(string html, int streamPosition)
    {
        var total = ReaderTextFilter.CountReadableCharacters(html);
        if (total <= 0)
            return 0;

        var position = Math.Clamp(streamPosition, 0, html.Length);
        var beforeImage = ReaderTextFilter.CountReadableCharacters(html[..position]);
        return Math.Clamp(beforeImage / (double)total, 0, 1);
    }

    private static bool TryGetContentRoot(EpubBook book, out string contentRoot)
    {
        contentRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(book.ContainerDirectory))
            return false;

        try
        {
            contentRoot = Path.GetFullPath(book.ContainerDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(contentRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveContainedFile(
        string contentRoot,
        string path,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(contentRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = contentRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool HasClass(HtmlNode node, string className) =>
        node.GetAttributeValue("class", string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');
}
