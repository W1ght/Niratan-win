using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaPageProvider : IMangaPageProvider
{
    private const long MaximumExtractedPageBytes = 100L * 1024 * 1024;
    private readonly string _cacheRoot;
    private readonly ISuwayomiService? _suwayomi;
    private readonly IMihonExtensionService? _mihon;

    public MangaPageProvider(
        ISuwayomiService suwayomi,
        IMihonExtensionService mihon)
        : this(AppDataHelper.GetMangaCachePath(), suwayomi, mihon)
    {
    }

    internal MangaPageProvider()
        : this(AppDataHelper.GetMangaCachePath(), null, null)
    {
    }

    internal MangaPageProvider(string cacheRoot)
        : this(cacheRoot, null, null)
    {
    }

    private MangaPageProvider(
        string cacheRoot,
        ISuwayomiService? suwayomi,
        IMihonExtensionService? mihon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _suwayomi = suwayomi;
        _mihon = mihon;
    }

    public async Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (pageIndex < 0 || pageIndex >= book.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        if (book.ContainerKind == MangaContainerKind.Suwayomi)
        {
            return await (_suwayomi
                          ?? throw new InvalidOperationException(
                              "Suwayomi page loading is unavailable."))
                .GetPagePathAsync(book, pageIndex, ct);
        }

        if (book.ContainerKind == MangaContainerKind.Mihon)
        {
            return await (_mihon
                          ?? throw new InvalidOperationException(
                              "Mihon page loading is unavailable."))
                .GetPagePathAsync(book, pageIndex, ct);
        }

        var page = book.Pages[pageIndex];
        if (book.ContainerKind == MangaContainerKind.ImageFolder)
            return ResolveFolderPage(book.PageRootPath ?? book.SourcePath, page.Path);

        var cacheDirectory = MangaPathUtility.GetCacheDirectory(
            _cacheRoot,
            book.Id,
            "pages");
        Directory.CreateDirectory(cacheDirectory);
        var targetPath = Path.Combine(
            cacheDirectory,
            $"{pageIndex:D6}{MangaPathUtility.SafeExtension(page.Path)}");
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            return targetPath;

        using var archive = ZipFile.OpenRead(book.SourcePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(
                candidate.FullName.Replace('\\', '/'),
                page.Path,
                StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidDataException($"Manga page is missing from the archive: {page.Path}");
        if (entry.Length <= 0 || entry.Length > MaximumExtractedPageBytes)
            throw new InvalidDataException("The manga page is empty or exceeds the safe extraction limit.");

        var tempPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var input = entry.Open();
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await input.CopyToAsync(output, ct);
            }

            if (new FileInfo(tempPath).Length > MaximumExtractedPageBytes)
                throw new InvalidDataException("The manga page exceeds the safe extraction limit.");
            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string ResolveFolderPage(string sourcePath, string relativePath)
    {
        var root = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manga page path escapes the selected source folder.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The manga page no longer exists.", fullPath);
        return fullPath;
    }
}
