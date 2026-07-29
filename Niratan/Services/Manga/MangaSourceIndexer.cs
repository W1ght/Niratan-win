using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaSourceIndexer
{
    public Task<MangaBook> IndexAsync(string sourcePath, CancellationToken ct = default) =>
        Task.Run(() => Index(sourcePath, ct), ct);

    private static MangaBook Index(string sourcePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullPath))
            return IndexFolder(fullPath, ct);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected manga source no longer exists.", fullPath);

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mokuro", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Choose an image folder, EPUB, CBZ, ZIP, or Mokuro source.");
        }

        if (extension.Equals(".mokuro", StringComparison.OrdinalIgnoreCase))
            return IndexFolder(Path.GetDirectoryName(fullPath)!, ct, fullPath);

        return IndexArchive(
            fullPath,
            extension.Equals(".epub", StringComparison.OrdinalIgnoreCase),
            ct);
    }

    private static MangaBook IndexFolder(
        string folderPath,
        CancellationToken ct,
        string? selectedMokuroPath = null)
    {
        var metadataPath = selectedMokuroPath ?? FindFolderMokuroMetadata(folderPath);
        var pagePaths = metadataPath is null
            ? []
            : GetFolderMokuroPagePaths(folderPath, metadataPath);
        if (pagePaths.Count == 0)
        {
            metadataPath = null;
            pagePaths = MangaPathUtility.NaturalOrder(
                    Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
                        .Where(MangaPathUtility.IsImagePath))
                .ToList();
        }

        ct.ThrowIfCancellationRequested();
        if (pagePaths.Count == 0)
            throw new InvalidDataException("The selected folder contains no readable manga images.");

        var titleSource = selectedMokuroPath ?? folderPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return CreateBook(
            sourcePath: selectedMokuroPath ?? folderPath,
            title: Path.GetFileNameWithoutExtension(titleSource),
            MangaContainerKind.ImageFolder,
            pagePaths.Select(path => Path.GetRelativePath(folderPath, path).Replace('\\', '/')),
            Directory.GetLastWriteTimeUtc(folderPath),
            pageRootPath: folderPath,
            mokuroMetadataPath: metadataPath);
    }

    private static MangaBook IndexArchive(string archivePath, bool isEpub, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Where(entry => MangaPathUtility.IsVisibleArchiveEntry(entry.FullName))
            .ToDictionary(
                entry => entry.FullName.Replace('\\', '/'),
                entry => entry,
                StringComparer.OrdinalIgnoreCase);

        List<string> pagePaths;
        string? mokuroMetadataPath = null;
        if (isEpub)
        {
            pagePaths = IndexEpub(entries, ct);
        }
        else
        {
            mokuroMetadataPath = FindArchiveMokuroMetadata(entries.Keys);
            pagePaths = mokuroMetadataPath is null
                ? []
                : GetArchiveMokuroPagePaths(entries, mokuroMetadataPath);
            if (pagePaths.Count == 0)
            {
                mokuroMetadataPath = null;
                pagePaths = MangaPathUtility.NaturalOrder(
                        entries.Keys.Where(MangaPathUtility.IsImagePath))
                    .ToList();
            }
        }

        if (pagePaths.Count == 0)
            throw new InvalidDataException("The selected archive contains no readable manga images.");

        return CreateBook(
            archivePath,
            Path.GetFileNameWithoutExtension(archivePath),
            isEpub ? MangaContainerKind.EpubArchive : MangaContainerKind.ZipArchive,
            pagePaths,
            File.GetLastWriteTimeUtc(archivePath),
            mokuroMetadataPath: mokuroMetadataPath);
    }

    private static string? FindFolderMokuroMetadata(string folderPath)
    {
        var candidates = Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetExtension(path).Equals(".mokuro", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("mokuro.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return MangaPathUtility.NaturalOrder(candidates).FirstOrDefault();
    }

    private static List<string> GetFolderMokuroPagePaths(
        string folderPath,
        string metadataPath)
    {
        var root = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        try
        {
            return MangaMokuroParser.GetPagePaths(File.ReadAllBytes(metadataPath))
                .Select(path => Path.GetFullPath(Path.Combine(
                    root,
                    path.Replace('/', Path.DirectorySeparatorChar))))
                .Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Where(MangaPathUtility.IsImagePath)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? FindArchiveMokuroMetadata(IEnumerable<string> entryPaths) =>
        MangaPathUtility.NaturalOrder(entryPaths.Where(path =>
                Path.GetExtension(path).Equals(".mokuro", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("mokuro.json", StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();

    private static List<string> GetArchiveMokuroPagePaths(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string metadataPath)
    {
        try
        {
            using var stream = entries[metadataPath].Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return MangaMokuroParser.GetPagePaths(buffer.ToArray())
                .Select(path => MangaPathUtility.ResolveArchivePath(path, metadataPath))
                .Where(path => path is not null)
                .Cast<string>()
                .Where(MangaPathUtility.IsImagePath)
                .Where(entries.ContainsKey)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> IndexEpub(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken ct)
    {
        const string containerPath = "META-INF/container.xml";
        if (!entries.TryGetValue(containerPath, out var containerEntry))
            throw new InvalidDataException("EPUB container.xml is missing.");

        var container = LoadXml(containerEntry);
        var packageReference = container
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("full-path")
            ?.Value;
        var packagePath = MangaPathUtility.ResolveArchivePath(packageReference ?? "", "");
        if (packagePath is null || !entries.TryGetValue(packagePath, out var packageEntry))
            throw new InvalidDataException("EPUB package document is missing.");

        var package = LoadXml(packageEntry);
        var manifest = package
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value,
                Href = element.Attribute("href")?.Value,
                MediaType = element.Attribute("media-type")?.Value ?? "",
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                && !string.IsNullOrWhiteSpace(item.Href))
            .Select(item => new
            {
                item.Id,
                Path = MangaPathUtility.ResolveArchivePath(item.Href!, packagePath),
                item.MediaType,
            })
            .Where(item => item.Path is not null)
            .ToDictionary(item => item.Id!, item => (item.Path!, item.MediaType));

        var spine = package
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("idref")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        var pages = new List<string>();
        foreach (var id in spine)
        {
            ct.ThrowIfCancellationRequested();
            if (!manifest.TryGetValue(id!, out var item))
                continue;

            if (item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && MangaPathUtility.IsImagePath(item.Item1))
            {
                pages.Add(item.Item1);
                continue;
            }

            if (!entries.TryGetValue(item.Item1, out var documentEntry))
                continue;

            foreach (var reference in FindImageReferences(documentEntry))
            {
                var resolved = MangaPathUtility.ResolveArchivePath(reference, item.Item1);
                if (resolved is not null
                    && MangaPathUtility.IsImagePath(resolved)
                    && entries.ContainsKey(resolved))
                {
                    pages.Add(resolved);
                }
            }
        }

        if (pages.Count == 0)
        {
            pages.AddRange(MangaPathUtility.NaturalOrder(
                manifest.Values
                    .Where(item => item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Item1)
                    .Where(MangaPathUtility.IsImagePath)
                    .Where(entries.ContainsKey)));
        }

        return pages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FindImageReferences(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var document = new HtmlDocument();
        document.OptionMaxNestedChildNodes = 4096;
        document.Load(stream, Encoding.UTF8);

        foreach (var node in document.DocumentNode.Descendants())
        {
            if (node.Name.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                var src = node.GetAttributeValue("src", "");
                if (!string.IsNullOrWhiteSpace(src))
                    yield return src;
            }
            else if (node.Name.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                var href = node.GetAttributeValue("href", "");
                if (string.IsNullOrWhiteSpace(href))
                    href = node.GetAttributeValue("xlink:href", "");
                if (!string.IsNullOrWhiteSpace(href))
                    yield return href;
            }
        }
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static MangaBook CreateBook(
        string sourcePath,
        string title,
        MangaContainerKind kind,
        IEnumerable<string> pagePaths,
        DateTime sourceModifiedAt,
        string? pageRootPath = null,
        string? mokuroMetadataPath = null)
    {
        var canonicalPath = Path.GetFullPath(sourcePath);
        var id = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath.ToUpperInvariant())))
            .ToLowerInvariant()[..24];
        var pages = pagePaths
            .Select((path, index) => new MangaPageDescriptor
            {
                Index = index,
                Path = path.Replace('\\', '/'),
            })
            .ToList();
        return new MangaBook
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled manga" : title,
            OriginalTitle = string.IsNullOrWhiteSpace(title) ? "Untitled manga" : title,
            SourcePath = canonicalPath,
            PageRootPath = pageRootPath,
            MokuroMetadataPath = mokuroMetadataPath,
            ContainerKind = kind,
            Pages = pages,
            SourceModifiedAt = new DateTimeOffset(sourceModifiedAt, TimeSpan.Zero),
        };
    }
}
