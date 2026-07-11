using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class NovelStatisticsSidecarService : INovelStatisticsSidecarService
{
    public const string StatisticsFileName = "statistics.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<NovelReadingStatistic>> LoadAsync(
        string bookRootPath,
        CancellationToken ct = default) =>
        (await LoadWithStatusAsync(bookRootPath, ct)).Statistics;

    public async Task<NovelStatisticsSidecarLoadResult> LoadWithStatusAsync(
        string bookRootPath,
        CancellationToken ct = default)
    {
        var path = Path.Combine(bookRootPath, StatisticsFileName);
        if (!File.Exists(path))
            return new(NovelStatisticsSidecarLoadStatus.Missing, []);

        try
        {
            await using var stream = File.OpenRead(path);
            var statistics = await JsonSerializer.DeserializeAsync<List<NovelReadingStatistic>>(
                stream,
                JsonOptions,
                ct);
            return new(
                NovelStatisticsSidecarLoadStatus.Loaded,
                Deduplicate(statistics ?? []));
        }
        catch (JsonException)
        {
            return new(NovelStatisticsSidecarLoadStatus.Corrupt, []);
        }
        catch (IOException)
        {
            return new(NovelStatisticsSidecarLoadStatus.Unavailable, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new(NovelStatisticsSidecarLoadStatus.Unavailable, []);
        }
    }

    public async Task SaveAsync(
        string bookRootPath,
        IReadOnlyList<NovelReadingStatistic> statistics,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        Directory.CreateDirectory(bookRootPath);
        var targetPath = Path.Combine(bookRootPath, StatisticsFileName);
        var tempPath = targetPath
            + "."
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            + ".tmp";
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
                await JsonSerializer.SerializeAsync(
                    stream,
                    Deduplicate(statistics),
                    JsonOptions,
                    ct);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static IReadOnlyList<NovelReadingStatistic> Deduplicate(
        IEnumerable<NovelReadingStatistic> statistics) =>
        statistics
            .GroupBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(statistic => statistic.LastStatisticModified)
                .First())
            .OrderBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .ToList();
}
