using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Services.Sync;

public sealed class TtuBookDataConverter : ITtuBookDataConverter, ITtuBackupBookDataConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string TransparentGif = "R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";
    private readonly IEpubParserService _epubParser;

    public TtuBookDataConverter(IEpubParserService? epubParser = null)
    {
        _epubParser = epubParser ?? new EpubParserService(NullLogger<EpubParserService>.Instance);
    }

    public async Task<string> ReadTitleAsync(
        string ttuBookDataPath,
        CancellationToken ct = default)
    {
        using var source = ZipFile.OpenRead(ttuBookDataPath);
        var staticDataEntry = source.GetEntry("staticdata.json")
            ?? throw new InvalidOperationException("TTU bookdata is missing staticdata.json.");
        await using var stream = staticDataEntry.Open();
        var staticData = await JsonSerializer.DeserializeAsync<TtuStaticData>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException("TTU staticdata.json is invalid.");
        return staticData.Title;
    }

    public async Task<string> ConvertFromEpubAsync(
        NovelBook book,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath))
            throw new FileNotFoundException("The private EPUB file no longer exists.", book.FilePath);

        Directory.CreateDirectory(outputDirectory);
        var extractedRoot = Path.Combine(outputDirectory, ".epub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedRoot);
        try
        {
            var document = await Task.Run(() => _epubParser.Parse(book.FilePath, extractedRoot), ct);
            var bookInfo = await ReadBookInfoAsync(book.ExtractedPath, ct);
            var sections = new List<TtuSection>();
            var elementParts = new List<string>();
            string? currentParent = null;
            foreach (var chapter in document.Chapters.OrderBy(chapter => chapter.SpineIndex))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(chapter.Href))
                    continue;
                var chapterDocument = new HtmlDocument();
                chapterDocument.OptionOutputAsXml = false;
                chapterDocument.Load(chapter.Href, Encoding.UTF8);
                var htmlNode = chapterDocument.DocumentNode.SelectSingleNode("//*[local-name()='html']");
                var bodyNode = chapterDocument.DocumentNode.SelectSingleNode("//*[local-name()='body']");
                var htmlClass = htmlNode?.GetAttributeValue("class", "") ?? "";
                var bodyClass = bodyNode?.GetAttributeValue("class", "") ?? "";
                var bodyHtml = bodyNode?.InnerHtml ?? chapterDocument.DocumentNode.InnerHtml;
                bodyHtml = RewriteImagesToTtu(
                    bodyHtml,
                    chapter.Href,
                    document.ContainerDirectory,
                    document.ExtractedPath);
                bodyHtml = NormalizeTagsToHtml(bodyHtml);

                var reference = "ttu-" + SanitizeHtmlId(chapter.Id, chapter.SpineIndex);
                var chapterInfo = FindChapterInfo(bookInfo, chapter, document.ContainerDirectory);
                var characters = chapterInfo?.ChapterCount ?? 0;
                var noText = characters == 0 ? " ttu-no-text" : "";
                elementParts.Add(
                    $"<div id=\"{reference}\"><div class=\"{ClassList("ttu-book-html-wrapper", htmlClass, noText)}\"><div class=\"{ClassList("ttu-book-body-wrapper", bodyClass, noText)}\">{bodyHtml}</div></div></div>");

                var label = FindTocLabel(document.Toc, chapter.Href);
                var start = chapterInfo?.CurrentTotal ?? 0;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    currentParent = reference;
                    sections.Add(new TtuSection(reference, Math.Max(characters, 1), label, start, 0, null));
                }
                else if (currentParent != null)
                {
                    sections.Add(new TtuSection(reference, Math.Max(characters, 1), null, null, null, currentParent));
                }
                else
                {
                    currentParent = reference;
                    sections.Add(new TtuSection(reference, Math.Max(characters, 1), "Preface", start, 0, null));
                }
            }

            var totalCharacters = bookInfo?.CharacterCount ?? sections.Sum(section => section.CharactersWeight);
            for (var index = 0; index < sections.Count; index++)
            {
                if (sections[index].Label == null || sections[index].StartCharacter == null)
                    continue;
                var next = sections.Skip(index + 1).FirstOrDefault(section => section.Label != null);
                var end = next?.StartCharacter ?? totalCharacters;
                sections[index] = sections[index] with
                {
                    Characters = Math.Max(0, end - sections[index].StartCharacter!.Value),
                };
            }

            var stylesheet = new StringBuilder();
            foreach (var item in document.Manifest.Values.Where(item =>
                         string.Equals(item.MediaType, "text/css", StringComparison.OrdinalIgnoreCase)
                         && File.Exists(item.Href)))
            {
                stylesheet.AppendLine(await File.ReadAllTextAsync(item.Href, ct));
            }

            var title = string.IsNullOrWhiteSpace(book.OriginalTitle) ? book.Title : book.OriginalTitle;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lastAccess = ToDateTimeOffset(book.LastOpenedAt ?? book.ImportedAt)
                .ToUnixTimeMilliseconds();
            var outputPath = Path.Combine(
                outputDirectory,
                $"bookdata_1_6_{Math.Max(0, totalCharacters)}_{now}_{lastAccess}.zip");
            using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
            await AddJsonEntryAsync(
                archive,
                "staticdata.json",
                new TtuStaticData(title, stylesheet.ToString(), string.Concat(elementParts), sections),
                ct);
            foreach (var item in document.Manifest.Values.Where(item =>
                         item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                         && File.Exists(item.Href)))
            {
                var relative = SafeRelativePath(document.ContainerDirectory, item.Href);
                relative ??= SafeRelativePath(document.ExtractedPath, item.Href) is { } extractedRelative
                    ? "epub-root/" + extractedRelative
                    : null;
                if (relative == null)
                    continue;
                await CopyFileToArchiveAsync(archive, item.Href, "blobs/" + relative, ct);
            }

            if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath))
                await CopyFileToArchiveAsync(archive, book.CoverPath, "cover" + Path.GetExtension(book.CoverPath), ct);
            return outputPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractedRoot))
                    Directory.Delete(extractedRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    public async Task<string> ConvertToEpubAsync(
        string ttuBookDataPath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ttuBookDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        using var source = ZipFile.OpenRead(ttuBookDataPath);
        var staticDataEntry = source.GetEntry("staticdata.json")
            ?? throw new InvalidOperationException("TTU bookdata is missing staticdata.json.");
        await using var staticDataStream = staticDataEntry.Open();
        var staticData = await JsonSerializer.DeserializeAsync<TtuStaticData>(
            staticDataStream,
            JsonOptions,
            ct)
            ?? throw new InvalidOperationException("TTU staticdata.json is invalid.");

        var fileBaseName = SanitizeFileName(staticData.Title);
        var epubPath = Path.Combine(outputDirectory, $"{fileBaseName}.epub");
        if (File.Exists(epubPath))
            File.Delete(epubPath);

        using var epub = ZipFile.Open(epubPath, ZipArchiveMode.Create);
        await AddStringEntryAsync(epub, "mimetype", "application/epub+zip", CompressionLevel.NoCompression, ct);
        await AddStringEntryAsync(epub, "META-INF/container.xml", CreateContainerXml(), CompressionLevel.SmallestSize, ct);
        await AddStringEntryAsync(epub, "item/stylesheet.css", staticData.StyleSheet, CompressionLevel.SmallestSize, ct);

        var imageItems = await CopyBlobEntriesAsync(source, epub, ct);
        var coverItem = await CopyCoverEntryAsync(source, epub, ct);
        var chapters = BuildChapters(staticData);
        foreach (var chapter in chapters)
        {
            await AddStringEntryAsync(
                epub,
                $"item/xhtml/{chapter.FileName}",
                CreateChapterXhtml(staticData.Title, chapter.Fragment),
                CompressionLevel.SmallestSize,
                ct);
        }

        await AddStringEntryAsync(
            epub,
            "item/navigation-documents.xhtml",
            CreateNavigationDocument(chapters),
            CompressionLevel.SmallestSize,
            ct);
        await AddStringEntryAsync(
            epub,
            "item/standard.opf",
            CreateOpf(staticData.Title, chapters, imageItems, coverItem),
            CompressionLevel.SmallestSize,
            ct);
        return epubPath;
    }

    private static List<TtuChapter> BuildChapters(TtuStaticData staticData)
    {
        var sections = staticData.Sections.Count > 0
            ? staticData.Sections
            : [new TtuSection("ttu-chapter-1", 1, staticData.Title, 0, null, null)];
        var chapters = new List<TtuChapter>();
        var usedReferences = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;
        foreach (var section in sections)
        {
            if (!usedReferences.Add(section.Reference))
                continue;

            var fragment = ExtractElementById(staticData.ElementHtml, section.Reference)
                ?? staticData.ElementHtml;
            fragment = NormalizeImages(NormalizeTagsToXhtml(fragment));
            chapters.Add(new TtuChapter(
                $"chapter-{index:0000}.xhtml",
                string.IsNullOrWhiteSpace(section.Label) ? $"Section {index}" : section.Label,
                fragment));
            index++;
        }

        if (chapters.Count == 0)
        {
            chapters.Add(new TtuChapter(
                "chapter-0001.xhtml",
                staticData.Title,
                NormalizeImages(NormalizeTagsToXhtml(staticData.ElementHtml))));
        }

        return chapters;
    }

    private static string? ExtractElementById(string html, string id)
    {
        var startMatch = Regex.Match(
            html,
            $"<div\\b[^>]*\\bid=[\"']{Regex.Escape(id)}[\"'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!startMatch.Success)
            return null;

        var tokenMatches = Regex.Matches(
            html[startMatch.Index..],
            "<div\\b[^>]*>|</div>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var depth = 0;
        foreach (Match token in tokenMatches)
        {
            if (token.Value.StartsWith("</", StringComparison.Ordinal))
                depth--;
            else
                depth++;

            if (depth == 0)
            {
                var end = startMatch.Index + token.Index + token.Length;
                return html[startMatch.Index..end];
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<ManifestItem>> CopyBlobEntriesAsync(
        ZipArchive source,
        ZipArchive epub,
        CancellationToken ct)
    {
        var items = new List<ManifestItem>();
        foreach (var entry in source.Entries.Where(entry =>
            entry.FullName.StartsWith("blobs/", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(entry.Name)
            && IsSafeRelativePath(entry.FullName)))
        {
            var relative = entry.FullName["blobs/".Length..].Replace('\\', '/');
            var destination = $"item/{relative}";
            await CopyEntryAsync(entry, epub, destination, CompressionLevel.NoCompression, ct);
            items.Add(new ManifestItem(
                $"img{items.Count + 1}",
                relative,
                GetMediaType(relative)));
        }

        return items;
    }

    private static async Task<ManifestItem?> CopyCoverEntryAsync(
        ZipArchive source,
        ZipArchive epub,
        CancellationToken ct)
    {
        var cover = source.Entries.FirstOrDefault(entry =>
            entry.FullName.StartsWith("cover.", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(entry.Name)
            && IsSafeRelativePath(entry.FullName));
        if (cover == null)
            return null;

        var destination = $"item/{cover.Name}";
        await CopyEntryAsync(cover, epub, destination, CompressionLevel.NoCompression, ct);
        return new ManifestItem("cover-image", cover.Name, GetMediaType(cover.Name));
    }

    private static async Task CopyEntryAsync(
        ZipArchiveEntry source,
        ZipArchive destination,
        string destinationName,
        CompressionLevel compressionLevel,
        CancellationToken ct)
    {
        var entry = destination.CreateEntry(destinationName, compressionLevel);
        await using var input = source.Open();
        await using var output = entry.Open();
        await input.CopyToAsync(output, ct);
    }

    private static async Task AddStringEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CompressionLevel compressionLevel,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    private static string CreateContainerXml() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="item/standard.opf" media-type="application/oebps-package+xml" />
          </rootfiles>
        </container>
        """;

    private static string CreateChapterXhtml(string title, string fragment)
    {
        var escapedTitle = WebUtility.HtmlEncode(title);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head>
                <title>{escapedTitle}</title>
                <link rel="stylesheet" type="text/css" href="../stylesheet.css" />
              </head>
              <body>
                {fragment}
              </body>
            </html>
            """);
    }

    private static string CreateNavigationDocument(IReadOnlyList<TtuChapter> chapters)
    {
        var items = string.Join(
            Environment.NewLine,
            chapters.Select(chapter =>
                $"      <li><a href=\"xhtml/{chapter.FileName}\">{WebUtility.HtmlEncode(chapter.Label)}</a></li>"));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <head><title>Table of Contents</title></head>
              <body>
                <nav epub:type="toc">
                  <ol>
            {items}
                  </ol>
                </nav>
              </body>
            </html>
            """);
    }

    private static string CreateOpf(
        string title,
        IReadOnlyList<TtuChapter> chapters,
        IReadOnlyList<ManifestItem> imageItems,
        ManifestItem? coverItem)
    {
        var manifest = new StringBuilder();
        manifest.AppendLine("""    <item id="nav" href="navigation-documents.xhtml" media-type="application/xhtml+xml" properties="nav" />""");
        manifest.AppendLine("""    <item id="style" href="stylesheet.css" media-type="text/css" />""");
        for (var index = 0; index < chapters.Count; index++)
        {
            manifest.AppendLine($"""    <item id="chapter{index + 1}" href="xhtml/{chapters[index].FileName}" media-type="application/xhtml+xml" />""");
        }

        foreach (var image in imageItems)
            manifest.AppendLine($"""    <item id="{image.Id}" href="{image.Href}" media-type="{image.MediaType}" />""");
        if (coverItem != null)
            manifest.AppendLine($"""    <item id="{coverItem.Id}" href="{coverItem.Href}" media-type="{coverItem.MediaType}" properties="cover-image" />""");

        var spine = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, chapters.Count).Select(index => $"    <itemref idref=\"chapter{index}\" />"));
        var coverMeta = coverItem == null ? "" : """    <meta name="cover" content="cover-image" />""";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="bookid">urn:uuid:{Guid.NewGuid():D}</dc:identifier>
                <dc:title>{WebUtility.HtmlEncode(title)}</dc:title>
                <dc:language>ja</dc:language>
            {coverMeta}
              </metadata>
              <manifest>
            {manifest.ToString().TrimEnd()}
              </manifest>
              <spine>
            {spine}
              </spine>
            </package>
            """);
    }

    private static string NormalizeTagsToXhtml(string html)
    {
        var normalized = Regex.Replace(html, "<br\\b([^>/]*)>", "<br$1/>", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "<hr\\b([^>/]*)>", "<hr$1/>", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, "(<img\\b[^>]*?)(?<!/)>", "$1/>", RegexOptions.IgnoreCase);
        return normalized.Replace("&nbsp;", "&#160;", StringComparison.Ordinal);
    }

    private static string NormalizeImages(string html)
    {
        var normalized = Regex.Replace(
            html,
            "data:image/[^;\"']+;ttu:([^;\"']+);base64,[^\"']+",
            "../$1",
            RegexOptions.IgnoreCase);
        return Regex.Replace(normalized, "ttu:([^\"']+)", "../$1", RegexOptions.IgnoreCase);
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "book" : sanitized;
    }

    private static string GetMediaType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    private static bool IsSafeRelativePath(string path) =>
        !Path.IsPathRooted(path)
        && !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal);

    private static async Task<NovelBookInfo?> ReadBookInfoAsync(string? bookRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bookRoot))
            return null;
        var path = Path.Combine(bookRoot, NovelBookSidecarService.BookInfoFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<NovelBookInfo>(stream, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NovelBookInfoChapter? FindChapterInfo(
        NovelBookInfo? bookInfo,
        EpubChapter chapter,
        string containerDirectory)
    {
        if (bookInfo == null)
            return null;
        var relative = Path.GetRelativePath(containerDirectory, chapter.Href).Replace('\\', '/');
        return bookInfo.ChapterInfo.TryGetValue(relative, out var info)
            ? info
            : bookInfo.ChapterInfo.Values.FirstOrDefault(value => value.SpineIndex == chapter.SpineIndex);
    }

    private static string? FindTocLabel(IEnumerable<EpubTocItem> items, string chapterPath)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Href)
                && string.Equals(
                    Path.GetFullPath(item.Href.Split('#')[0]),
                    Path.GetFullPath(chapterPath.Split('#')[0]),
                    StringComparison.OrdinalIgnoreCase))
            {
                return item.Label;
            }
            var child = FindTocLabel(item.Children, chapterPath);
            if (child != null)
                return child;
        }
        return null;
    }

    private static string RewriteImagesToTtu(
        string html,
        string chapterPath,
        string containerDirectory,
        string extractedRoot)
    {
        return Regex.Replace(
            html,
            "(?<prefix><(?:img\\b[^>]*?\\bsrc|image\\b[^>]*?(?:xlink:href|href))=[\"'])(?<src>[^\"']+)(?<suffix>[\"'])",
            match =>
            {
                var source = WebUtility.HtmlDecode(match.Groups["src"].Value);
                if (Uri.TryCreate(source, UriKind.Absolute, out _) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return match.Value;
                var chapterDirectory = Path.GetDirectoryName(chapterPath)!;
                var resolved = Path.GetFullPath(Path.Combine(chapterDirectory, Uri.UnescapeDataString(source.Split('#')[0])));
                var relative = SafeRelativePath(containerDirectory, resolved);
                relative ??= SafeRelativePath(extractedRoot, resolved) is { } extractedRelative
                    ? "epub-root/" + extractedRelative
                    : null;
                if (relative == null)
                    return match.Value;
                return match.Groups["prefix"].Value
                    + $"data:image/gif;ttu:{relative};base64,{TransparentGif}"
                    + match.Groups["suffix"].Value;
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeTagsToHtml(string html) =>
        Regex.Replace(
                Regex.Replace(
                    Regex.Replace(html, "<br\\b([^>]*)/>", "<br$1>", RegexOptions.IgnoreCase),
                    "<hr\\b([^>]*)/>",
                    "<hr$1>",
                    RegexOptions.IgnoreCase),
                "<img\\b([^>]*)/>",
                "<img$1>",
                RegexOptions.IgnoreCase);

    private static string SanitizeHtmlId(string id, int fallbackIndex)
    {
        var sanitized = Regex.Replace(id ?? "", "[^A-Za-z0-9_-]", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"chapter-{fallbackIndex + 1}"
            : sanitized;
    }

    private static string ClassList(params string[] values) =>
        string.Join(
            ' ',
            values.Select(value => value.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? SafeRelativePath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
    }

    private static async Task CopyFileToArchiveAsync(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.NoCompression);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, ct);
    }

    private sealed record TtuStaticData(
        string Title,
        string StyleSheet,
        string ElementHtml,
        IReadOnlyList<TtuSection> Sections);

    private sealed record TtuSection(
        string Reference,
        int CharactersWeight,
        string? Label,
        int? StartCharacter,
        int? Characters,
        string? ParentChapter);

    private sealed record TtuChapter(
        string FileName,
        string Label,
        string Fragment);

    private sealed record ManifestItem(
        string Id,
        string Href,
        string MediaType);
}
