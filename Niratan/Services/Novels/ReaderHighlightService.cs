using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public sealed class ReaderHighlightService : IReaderHighlightService
{
    public const string HighlightsFileName = "highlights.json";

    public static readonly DateTimeOffset MacAbsoluteDateReference =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<IReadOnlyList<ReaderHighlight>> LoadAsync(
        string bookRootPath,
        CancellationToken ct = default)
    {
        var path = GetHighlightsPath(bookRootPath);
        if (!File.Exists(path))
            return [];

        try
        {
            await using var stream = File.OpenRead(path);
            var highlights = await JsonSerializer.DeserializeAsync<List<ReaderHighlight>>(
                stream,
                JsonOptions,
                ct);

            return SortHighlights(highlights ?? []);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(
        string bookRootPath,
        IReadOnlyList<ReaderHighlight> highlights,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(highlights);

        _ = GetHighlightsPath(bookRootPath);
        Directory.CreateDirectory(bookRootPath);

        var targetPath = Path.Combine(bookRootPath, HighlightsFileName);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
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
                    SortHighlights(highlights),
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

    public ReaderHighlight CreateFromChapterSelection(
        Guid id,
        int chapterIndex,
        int chapterCharacterOffset,
        int rawOffset,
        string text,
        ReaderHighlightColor color,
        DateTimeOffset createdAt,
        IReadOnlyList<int> chapterCharacterCounts)
    {
        var range = GetChapterRange(chapterCharacterCounts, chapterIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(chapterIndex));

        return new ReaderHighlight(
            id,
            range.Start + Math.Max(0, chapterCharacterOffset),
            Math.Max(0, rawOffset),
            text,
            color,
            createdAt.ToUniversalTime());
    }

    public IReadOnlyList<ReaderHighlight> GetChapterHighlights(
        IEnumerable<ReaderHighlight> highlights,
        IReadOnlyList<int> chapterCharacterCounts,
        int chapterIndex)
    {
        ArgumentNullException.ThrowIfNull(highlights);

        var range = GetChapterRange(chapterCharacterCounts, chapterIndex);
        if (range is null)
            return [];

        return highlights
            .Where(h => h.Character >= range.Value.Start && h.Character < range.Value.End)
            .OrderBy(h => h.Character)
            .ThenBy(h => h.CreatedAt)
            .ToList();
    }

    public ReaderHighlightJumpTarget? ResolveJumpTarget(
        ReaderHighlight highlight,
        IReadOnlyList<int> chapterCharacterCounts)
    {
        ArgumentNullException.ThrowIfNull(chapterCharacterCounts);

        var total = chapterCharacterCounts.Sum(count => Math.Max(0, count));
        if (total <= 0)
            return null;

        var clamped = Math.Clamp(highlight.Character, 0, total - 1);
        var start = 0;
        for (var i = 0; i < chapterCharacterCounts.Count; i++)
        {
            var count = Math.Max(0, chapterCharacterCounts[i]);
            if (count <= 0)
                continue;

            var end = start + count;
            if (clamped >= start && clamped < end)
                return new ReaderHighlightJumpTarget(i, (clamped - start) / (double)count);

            start = end;
        }

        return null;
    }

    public string? SerializeForWebView(IEnumerable<ReaderHighlight> highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);

        var sorted = SortHighlights(highlights);
        return sorted.Count == 0
            ? null
            : JsonSerializer.Serialize(sorted, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new ReaderHighlightColorJsonConverter());
        options.Converters.Add(new MacAbsoluteDateTimeOffsetJsonConverter());
        return options;
    }

    private static string GetHighlightsPath(string bookRootPath)
    {
        if (string.IsNullOrWhiteSpace(bookRootPath))
            throw new ArgumentException("Book root path is required.", nameof(bookRootPath));

        return Path.Combine(bookRootPath, HighlightsFileName);
    }

    private static IReadOnlyList<ReaderHighlight> SortHighlights(
        IEnumerable<ReaderHighlight> highlights) =>
        highlights
            .OrderBy(h => h.Character)
            .ThenBy(h => h.CreatedAt)
            .ToList();

    private static (int Start, int End)? GetChapterRange(
        IReadOnlyList<int> chapterCharacterCounts,
        int chapterIndex)
    {
        ArgumentNullException.ThrowIfNull(chapterCharacterCounts);
        if (chapterIndex < 0 || chapterIndex >= chapterCharacterCounts.Count)
            return null;

        var start = 0;
        for (var i = 0; i < chapterIndex; i++)
            start += Math.Max(0, chapterCharacterCounts[i]);

        var count = Math.Max(0, chapterCharacterCounts[chapterIndex]);
        return (start, start + count);
    }

    private sealed class ReaderHighlightColorJsonConverter
        : JsonConverter<ReaderHighlightColor>
    {
        public override ReaderHighlightColor Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value?.Trim().ToLowerInvariant() switch
            {
                "yellow" => ReaderHighlightColor.Yellow,
                "green" => ReaderHighlightColor.Green,
                "blue" => ReaderHighlightColor.Blue,
                "pink" => ReaderHighlightColor.Pink,
                "purple" => ReaderHighlightColor.Purple,
                _ => throw new JsonException($"Unsupported reader highlight color: {value}"),
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            ReaderHighlightColor value,
            JsonSerializerOptions options)
        {
            var name = value switch
            {
                ReaderHighlightColor.Yellow => "yellow",
                ReaderHighlightColor.Green => "green",
                ReaderHighlightColor.Blue => "blue",
                ReaderHighlightColor.Pink => "pink",
                ReaderHighlightColor.Purple => "purple",
                _ => throw new JsonException($"Unsupported reader highlight color: {value}"),
            };
            writer.WriteStringValue(name);
        }
    }

    private sealed class MacAbsoluteDateTimeOffsetJsonConverter
        : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return MacAbsoluteDateReference.AddSeconds(reader.GetDouble());

            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            throw new JsonException("Invalid reader highlight createdAt value.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            var seconds = (value.ToUniversalTime() - MacAbsoluteDateReference).TotalSeconds;
            writer.WriteNumberValue(seconds);
        }
    }
}
