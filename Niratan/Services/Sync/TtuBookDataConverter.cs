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

namespace Niratan.Services.Sync;

public sealed class TtuBookDataConverter : ITtuBookDataConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
