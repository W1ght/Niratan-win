using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public sealed class SasayakiSidecarService : ISasayakiSidecarService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<SasayakiMatchData?> LoadMatchAsync(
        string bookRootPath,
        CancellationToken cancellationToken = default)
    {
        foreach (var fileName in new[] { ISasayakiSidecarService.MatchFileName, ISasayakiSidecarService.LegacyMatchFileName })
        {
            var path = Path.Combine(bookRootPath, fileName);
            var data = await TryReadAsync<SasayakiMatchData>(path, cancellationToken);
            if (data?.IsCurrentSchemaVersion == true)
                return data;
        }

        return null;
    }

    public Task SaveMatchAsync(
        string bookRootPath,
        SasayakiMatchData data,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(bookRootPath, ISasayakiSidecarService.MatchFileName);
        return WriteJsonAsync(path, data, cancellationToken);
    }

    public async Task<SasayakiPlaybackData> LoadPlaybackAsync(
        string bookRootPath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(bookRootPath, ISasayakiSidecarService.PlaybackFileName);
        var data = await TryReadAsync<SasayakiPlaybackData>(path, cancellationToken);
        return NormalizePlayback(data);
    }

    public Task SavePlaybackAsync(
        string bookRootPath,
        SasayakiPlaybackData data,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(bookRootPath, ISasayakiSidecarService.PlaybackFileName);
        return WriteJsonAsync(path, NormalizePlayback(data), cancellationToken);
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private static SasayakiPlaybackData NormalizePlayback(SasayakiPlaybackData? data)
    {
        data ??= new SasayakiPlaybackData();
        if (data.Rate <= 0)
            data.Rate = 1.0;
        return data;
    }
}
