using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal interface IMangaCatalogStore
{
    Task<MangaLibraryCatalog> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(MangaLibraryCatalog catalog, CancellationToken ct = default);
}

internal sealed class MangaCatalogStore : IMangaCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _path;

    public MangaCatalogStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<MangaLibraryCatalog> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            return new MangaLibraryCatalog();

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<MangaLibraryCatalog>(
                    stream,
                    JsonOptions,
                    ct)
                ?? new MangaLibraryCatalog();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The manga catalog JSON is invalid.", ex);
        }
    }

    public async Task SaveAsync(
        MangaLibraryCatalog catalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + $".{Guid.NewGuid():N}.tmp";
        var backupPath = _path + $".{Guid.NewGuid():N}.backup.tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, ct);
            }

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, backupPath, ignoreMetadataErrors: true);
                File.Delete(backupPath);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }
}
