using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface IEpubParserService
{
    EpubBook Parse(string epubFilePath, string outputDirectory);
}

public sealed class EpubParserService : IEpubParserService
{
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace NcxNs = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace XhtmlNs = "http://www.w3.org/1999/xhtml";

    private readonly ILogger<EpubParserService> _logger;

    public EpubParserService(ILogger<EpubParserService> logger)
    {
        _logger = logger;
    }

    public EpubBook Parse(string epubFilePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        ExtractZip(epubFilePath, outputDirectory);

        var containerPath = Path.Combine(outputDirectory, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidOperationException("Invalid EPUB: missing META-INF/container.xml");

        var containerDoc = XDocument.Load(containerPath);
        var rootfileElement = containerDoc
            .Descendants(ContainerNs + "rootfile")
            .FirstOrDefault(e =>
                string.Equals(
                    e.Attribute("media-type")?.Value,
                    "application/oebps-package+xml",
                    StringComparison.OrdinalIgnoreCase
                )
            );
        var opfRelativePath = rootfileElement?.Attribute("full-path")?.Value
            ?? throw new InvalidOperationException("Invalid EPUB: OPF rootfile not found in container.xml");

        var opfFullPath = Path.GetFullPath(Path.Combine(outputDirectory, opfRelativePath));
        var opfDirectory = Path.GetDirectoryName(opfFullPath)
            ?? outputDirectory;

        var opfDoc = XDocument.Load(opfFullPath);
        var packageElement = opfDoc.Element(OpfNs + "package")
            ?? opfDoc.Element("package")
            ?? throw new InvalidOperationException("Invalid EPUB: missing package element");

        var title = opfDoc.Descendants(DcNs + "title").FirstOrDefault()?.Value.Trim()
            ?? Path.GetFileNameWithoutExtension(epubFilePath);
        var author = opfDoc.Descendants(DcNs + "creator").FirstOrDefault()?.Value.Trim();
        var language = opfDoc.Descendants(DcNs + "language").FirstOrDefault()?.Value.Trim();
        var identifier = opfDoc.Descendants(DcNs + "identifier").FirstOrDefault()?.Value.Trim();

        var manifest = ParseManifest(packageElement, opfDirectory);
        var spine = ParseSpine(packageElement, manifest);
        var toc = ParseToc(opfDoc, opfDirectory, outputDirectory);
        var coverHref = FindCoverHref(packageElement, manifest);

        return new EpubBook
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(epubFilePath)
                : title,
            Author = string.IsNullOrWhiteSpace(author) ? null : author,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            UniqueIdentifier = string.IsNullOrWhiteSpace(identifier) ? null : identifier,
            ExtractedPath = outputDirectory,
            ContainerDirectory = opfDirectory,
            Chapters = spine,
            Toc = toc,
            Manifest = manifest,
            CoverHref = coverHref,
        };
    }

    private static void ExtractZip(string epubFilePath, string outputDirectory)
    {
        using var archive = ZipFile.OpenRead(epubFilePath);
        foreach (var entry in archive.Entries)
        {
            var destPath = Path.GetFullPath(Path.Combine(outputDirectory, entry.FullName));
            if (!destPath.StartsWith(outputDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Zip slip prevented: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static Dictionary<string, EpubManifestItem> ParseManifest(
        XElement packageElement, string opfDirectory)
    {
        var manifestElement = packageElement.Element(OpfNs + "manifest")
            ?? packageElement.Element("manifest");
        if (manifestElement == null)
            return [];

        var items = new Dictionary<string, EpubManifestItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifestElement.Elements(OpfNs + "item").Concat(manifestElement.Elements("item")))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            var mediaType = item.Attribute("media-type")?.Value;
            if (id == null || href == null || mediaType == null)
                continue;

            var resolvedHref = ResolveResourcePath(href, opfDirectory);
            items[id] = new EpubManifestItem
            {
                Id = id,
                Href = resolvedHref,
                MediaType = mediaType,
            };
        }

        return items;
    }

    private static List<EpubChapter> ParseSpine(
        XElement packageElement, Dictionary<string, EpubManifestItem> manifest)
    {
        var spineElement = packageElement.Element(OpfNs + "spine")
            ?? packageElement.Element("spine");
        if (spineElement == null)
            return [];

        var chapters = new List<EpubChapter>();
        var spineIndex = 0;
        foreach (var itemRef in spineElement.Elements(OpfNs + "itemref").Concat(spineElement.Elements("itemref")))
        {
            var idRef = itemRef.Attribute("idref")?.Value;
            if (idRef == null || !manifest.TryGetValue(idRef, out var manifestItem))
                continue;

            var linear = itemRef.Attribute("linear")?.Value;
            var mediaType = manifestItem.MediaType;
            if (!IsHtmlMediaType(mediaType))
                continue;

            chapters.Add(new EpubChapter
            {
                Id = idRef,
                Href = manifestItem.Href,
                MediaType = mediaType,
                IsLinear = !string.Equals(linear, "no", StringComparison.OrdinalIgnoreCase),
                SpineIndex = spineIndex++,
            });
        }

        return chapters;
    }

    private static List<EpubTocItem> ParseToc(
        XDocument opfDoc, string opfDirectory, string extractedPath)
    {
        var spineElement = opfDoc.Descendants(OpfNs + "spine").FirstOrDefault()
            ?? opfDoc.Descendants("spine").FirstOrDefault();
        var tocId = spineElement?.Attribute("toc")?.Value;

        // Try NCX TOC
        var ncxItem = opfDoc.Descendants(OpfNs + "item")
            .Concat(opfDoc.Descendants("item"))
            .FirstOrDefault(e =>
                e.Attribute("id")?.Value == tocId
                || string.Equals(
                    e.Attribute("media-type")?.Value,
                    "application/x-dtbncx+xml",
                    StringComparison.OrdinalIgnoreCase
                )
            );
        if (ncxItem != null)
        {
            var ncxHref = ncxItem.Attribute("href")?.Value;
            if (ncxHref != null)
            {
                var ncxFullPath = ResolveResourcePath(ncxHref, opfDirectory);
                if (File.Exists(ncxFullPath))
                    return ParseNcxToc(ncxFullPath, opfDirectory);
            }
        }

        // Try NAV (EPUB 3)
        foreach (var item in opfDoc.Descendants(OpfNs + "item").Concat(opfDoc.Descendants("item")))
        {
            var properties = item.Attribute("properties")?.Value ?? "";
            if (properties.Contains("nav"))
            {
                var navHref = item.Attribute("href")?.Value;
                if (navHref != null)
                {
                    var navFullPath = ResolveResourcePath(navHref, opfDirectory);
                    if (File.Exists(navFullPath))
                        return ParseNavToc(navFullPath, opfDirectory);
                }
            }
        }

        return [];
    }

    private static List<EpubTocItem> ParseNcxToc(string ncxFilePath, string opfDirectory)
    {
        try
        {
            var doc = XDocument.Load(ncxFilePath);
            var navMap = doc.Descendants(NcxNs + "navMap").FirstOrDefault()
                ?? doc.Descendants("navMap").FirstOrDefault();
            if (navMap == null)
                return [];

            return ParseNcxNavPoints(navMap, opfDirectory);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<EpubTocItem> ParseNcxNavPoints(
        XElement parent, string opfDirectory)
    {
        var items = new List<EpubTocItem>();
        foreach (var navPoint in parent.Elements(NcxNs + "navPoint").Concat(parent.Elements("navPoint")))
        {
            var label = navPoint.Element(NcxNs + "navLabel")?.Element(NcxNs + "text")?.Value.Trim()
                ?? navPoint.Element("navLabel")?.Element("text")?.Value.Trim()
                ?? "";
            var content = navPoint.Element(NcxNs + "content")?.Attribute("src")?.Value
                ?? navPoint.Element("content")?.Attribute("src")?.Value;
            var resolvedHref = content != null
                ? ResolveResourcePath(content, opfDirectory)
                : null;

            var children = ParseNcxNavPoints(navPoint, opfDirectory);
            items.Add(new EpubTocItem
            {
                Label = label,
                Href = resolvedHref,
                Children = children,
            });
        }

        return items;
    }

    private static List<EpubTocItem> ParseNavToc(string navFilePath, string opfDirectory)
    {
        try
        {
            var doc = XDocument.Load(navFilePath);
            var navElements = doc.Descendants(XhtmlNs + "nav")
                .Concat(doc.Descendants("nav"))
                .Where(e =>
                {
                    var type = e.Attribute(XhtmlNs + "type")?.Value
                        ?? e.Attribute("type")?.Value
                        ?? e.Attribute("epub:type")?.Value;
                    return type == "toc";
                });

            var navElement = navElements.FirstOrDefault();
            if (navElement == null)
                return [];

            var ol = navElement.Element(XhtmlNs + "ol") ?? navElement.Element("ol");
            if (ol == null)
                return [];

            return ParseNavOl(ol, opfDirectory);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<EpubTocItem> ParseNavOl(XElement ol, string opfDirectory)
    {
        var items = new List<EpubTocItem>();
        foreach (var li in ol.Elements(XhtmlNs + "li").Concat(ol.Elements("li")))
        {
            var a = li.Element(XhtmlNs + "a") ?? li.Element("a");
            var label = a?.Value.Trim() ?? "";
            var href = a?.Attribute("href")?.Value;
            var resolvedHref = href != null
                ? ResolveResourcePath(href, opfDirectory)
                : null;

            var childOl = li.Element(XhtmlNs + "ol") ?? li.Element("ol");
            var children = childOl != null ? ParseNavOl(childOl, opfDirectory) : [];

            items.Add(new EpubTocItem
            {
                Label = label,
                Href = resolvedHref,
                Children = children,
            });
        }

        return items;
    }

    private static string? FindCoverHref(XElement packageElement, Dictionary<string, EpubManifestItem> manifest)
    {
        // Try to find cover by ID
        var coverId = packageElement.Descendants(OpfNs + "meta")
            .Concat(packageElement.Descendants("meta"))
            .FirstOrDefault(e =>
                string.Equals(e.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase)
            )
            ?.Attribute("content")?.Value;

        if (coverId != null && manifest.TryGetValue(coverId, out var coverItem))
            return coverItem.Href;

        // Fallback: first image item
        foreach (var kv in manifest)
        {
            if (kv.Value.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return kv.Value.Href;
        }

        return null;
    }

    private static string ResolveResourcePath(string href, string opfDirectory)
    {
        // Remove fragment
        var pureHref = href.Split('#')[0];
        if (string.IsNullOrEmpty(pureHref))
            return href;

        var fullPath = Path.GetFullPath(Path.Combine(opfDirectory, Uri.UnescapeDataString(pureHref)));
        return Path.GetFullPath(fullPath); // Normalize
    }

    private static bool IsHtmlMediaType(string mediaType) =>
        mediaType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase)
        || mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
}
