using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public sealed class SasayakiSidecarService : ISasayakiSidecarService
{
    private const int LegacyWindowsSchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<SasayakiMatchData?> LoadMatchAsync(
        string bookRootPath,
        CancellationToken cancellationToken = default)
    {
        foreach (var fileName in new[]
                 {
                     ISasayakiSidecarService.MatchFileName,
                     ISasayakiSidecarService.LegacyMatchFileName,
                 })
        {
            var path = Path.Combine(bookRootPath, fileName);
            var json = await TryReadTextAsync(path, cancellationToken);
            if (json == null)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (IsLegacyWindowsMatch(document.RootElement))
                {
                    var legacy = TryDeserialize<LegacyWindowsMatchData>(json);
                    if (legacy?.SchemaVersion != LegacyWindowsSchemaVersion)
                        continue;

                    var migration = ConvertLegacyWindowsMatch(legacy);
                    await TryPersistLegacyMigrationAsync(
                        bookRootPath,
                        legacy,
                        migration,
                        cancellationToken);
                    return migration.Data;
                }

                if (!IsPortableMatch(document.RootElement))
                    continue;

                var data = TryDeserialize<SasayakiMatchData>(json);
                if (data != null)
                    return NormalizeMatch(data);
            }
        }

        return null;
    }

    public Task SaveMatchAsync(
        string bookRootPath,
        SasayakiMatchData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var path = Path.Combine(bookRootPath, ISasayakiSidecarService.MatchFileName);
        return WriteJsonAsync(path, NormalizeMatch(data), cancellationToken);
    }

    public Task<SasayakiSourceData?> LoadSourceAsync(
        string bookRootPath,
        CancellationToken cancellationToken = default) =>
        TryReadAsync<SasayakiSourceData>(
            Path.Combine(bookRootPath, ISasayakiSidecarService.SourceFileName),
            cancellationToken);

    public Task SaveSourceAsync(
        string bookRootPath,
        SasayakiSourceData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var path = Path.Combine(bookRootPath, ISasayakiSidecarService.SourceFileName);
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

    private static bool IsPortableMatch(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("matches", out var matches)
        && matches.ValueKind == JsonValueKind.Array
        && root.TryGetProperty("unmatched", out var unmatched)
        && unmatched.ValueKind == JsonValueKind.Number;

    private static bool IsLegacyWindowsMatch(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("schemaVersion", out _)
        && root.TryGetProperty("cues", out _)
        && root.TryGetProperty("matches", out _);

    private static LegacyMigration ConvertLegacyWindowsMatch(LegacyWindowsMatchData legacy)
    {
        var matches = new List<SasayakiMatch>();
        var legacyCueIndexes = new List<int>();
        foreach (var legacyMatch in legacy.Matches)
        {
            if (legacyMatch.CueIndex < 0 || legacyMatch.CueIndex >= legacy.Cues.Count)
                continue;

            var cue = legacy.Cues[legacyMatch.CueIndex];
            matches.Add(new SasayakiMatch
            {
                Id = legacyMatch.CueIndex.ToString(CultureInfo.InvariantCulture),
                StartTime = cue.StartTime,
                EndTime = cue.EndTime,
                Text = cue.Text,
                ChapterIndex = legacyMatch.ChapterIndex,
                Start = legacyMatch.StartCodePoint,
                Length = legacyMatch.Length,
            });
            legacyCueIndexes.Add(legacyMatch.CueIndex);
        }

        return new LegacyMigration(
            new SasayakiMatchData
            {
                Matches = matches,
                Unmatched = Math.Max(
                    Math.Max(0, legacy.UnmatchedCount),
                    Math.Max(0, legacy.Cues.Count - matches.Count)),
            },
            legacyCueIndexes);
    }

    private static async Task TryPersistLegacyMigrationAsync(
        string bookRootPath,
        LegacyWindowsMatchData legacy,
        LegacyMigration migration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(legacy.AudiobookPath)
                || !string.IsNullOrWhiteSpace(legacy.SrtPath))
            {
                await WriteJsonAsync(
                    Path.Combine(bookRootPath, ISasayakiSidecarService.SourceFileName),
                    new SasayakiSourceData
                    {
                        AudiobookPath = legacy.AudiobookPath,
                        SrtPath = legacy.SrtPath,
                    },
                    cancellationToken);
            }

            // The portable match file is the migration commit point. Write the
            // Windows-only source first so a partial migration can be retried.
            await WriteJsonAsync(
                Path.Combine(bookRootPath, ISasayakiSidecarService.MatchFileName),
                migration.Data,
                cancellationToken);

            var playbackPath = Path.Combine(
                bookRootPath,
                ISasayakiSidecarService.PlaybackFileName);
            if (File.Exists(playbackPath))
            {
                var playback = NormalizePlayback(
                    await TryReadAsync<SasayakiPlaybackData>(playbackPath, cancellationToken));
                playback.AudioBookmark = ResolveMigratedBookmark(
                    playback.AudioBookmark,
                    migration.LegacyCueIndexes);
                await WriteJsonAsync(playbackPath, playback, cancellationToken);
            }
        }
        catch (IOException)
        {
            // The converted data is still usable for this session; retry migration next load.
        }
        catch (UnauthorizedAccessException)
        {
            // The converted data is still usable for this session; retry migration next load.
        }
    }

    private static int ResolveMigratedBookmark(
        int legacyBookmark,
        IReadOnlyList<int> legacyCueIndexes)
    {
        if (legacyBookmark < 0 || legacyCueIndexes.Count == 0)
            return -1;

        var nearest = -1;
        for (var i = 0; i < legacyCueIndexes.Count; i++)
        {
            if (legacyCueIndexes[i] == legacyBookmark)
                return i;
            if (legacyCueIndexes[i] < legacyBookmark)
                nearest = i;
        }

        return nearest >= 0 ? nearest : 0;
    }

    private static SasayakiMatchData NormalizeMatch(SasayakiMatchData data)
    {
        data.Matches ??= [];
        data.Unmatched = Math.Max(0, data.Unmatched);
        foreach (var match in data.Matches)
        {
            match.Id ??= "";
            match.Text ??= "";
        }

        return data;
    }

    private static T? TryDeserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
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

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var json = await TryReadTextAsync(path, cancellationToken);
        return json == null ? null : TryDeserialize<T>(json);
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

    private sealed record LegacyMigration(
        SasayakiMatchData Data,
        IReadOnlyList<int> LegacyCueIndexes);

    private sealed class LegacyWindowsMatchData
    {
        public int SchemaVersion { get; set; }
        public string AudiobookPath { get; set; } = "";
        public string SrtPath { get; set; } = "";
        public List<LegacyWindowsCue> Cues { get; set; } = [];
        public List<LegacyWindowsMatch> Matches { get; set; } = [];
        public int UnmatchedCount { get; set; }
    }

    private sealed class LegacyWindowsCue
    {
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class LegacyWindowsMatch
    {
        public int CueIndex { get; set; }
        public int ChapterIndex { get; set; }
        public int StartCodePoint { get; set; }
        public int Length { get; set; }
    }
}
