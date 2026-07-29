using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaTextRegionService : IMangaTextRegionService
{
    private const long MaximumMetadataBytes = 64L * 1024 * 1024;

    public async Task<IReadOnlyList<MangaTextRegion>> GetRegionsAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(book.MokuroMetadataPath)
            || pageIndex < 0
            || pageIndex >= book.Pages.Count)
        {
            return [];
        }

        try
        {
            var data = book.ContainerKind == MangaContainerKind.ImageFolder
                ? await ReadFileAsync(book.MokuroMetadataPath, ct)
                : await ReadArchiveEntryAsync(book, book.MokuroMetadataPath, ct);
            return data.Length == 0
                ? []
                : MangaMokuroParser.GetRegions(
                    data,
                    book.Pages[pageIndex].Path,
                    pageIndex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumMetadataBytes)
            return [];
        return await File.ReadAllBytesAsync(path, ct);
    }

    private static async Task<byte[]> ReadArchiveEntryAsync(
        MangaBook book,
        string metadataPath,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(book.SourcePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(
                candidate.FullName.Replace('\\', '/'),
                metadataPath,
                StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length <= 0 || entry.Length > MaximumMetadataBytes)
            return [];

        await using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
