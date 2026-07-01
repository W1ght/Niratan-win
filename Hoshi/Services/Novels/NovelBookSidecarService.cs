using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class NovelBookSidecarService : INovelBookSidecarService
{
    public const string BookmarkFileName = "bookmark.json";
    public const string BookInfoFileName = "bookinfo.json";

    private static readonly DateTimeOffset MacAbsoluteDateReference =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public Task<NovelBookmark?> LoadBookmarkAsync(
        string bookRootPath,
        CancellationToken ct = default) =>
        TryReadAsync<NovelBookmark>(Path.Combine(bookRootPath, BookmarkFileName), ct);

    public Task SaveBookmarkAsync(
        string bookRootPath,
        NovelBookmark bookmark,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return WriteJsonAsync(Path.Combine(bookRootPath, BookmarkFileName), bookmark, ct);
    }

    public Task<NovelBookInfo?> LoadBookInfoAsync(
        string bookRootPath,
        CancellationToken ct = default) =>
        TryReadAsync<NovelBookInfo>(Path.Combine(bookRootPath, BookInfoFileName), ct);

    public Task SaveBookInfoAsync(
        string bookRootPath,
        NovelBookInfo bookInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookInfo);
        return WriteJsonAsync(Path.Combine(bookRootPath, BookInfoFileName), bookInfo, ct);
    }

    public NovelBookInfo CreateBookInfo(
        IReadOnlyList<EpubChapter> chapters,
        IReadOnlyList<int> chapterCharacterCounts,
        string? containerDirectory)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(chapterCharacterCounts);

        var chapterInfo = new Dictionary<string, NovelBookInfoChapter>(StringComparer.Ordinal);
        var currentTotal = 0;
        var count = Math.Min(chapters.Count, chapterCharacterCounts.Count);
        for (var i = 0; i < count; i++)
        {
            var chapterCount = Math.Max(0, chapterCharacterCounts[i]);
            var key = GetChapterInfoKey(chapters[i], containerDirectory, i);
            chapterInfo[key] = new NovelBookInfoChapter(
                chapters[i].SpineIndex,
                currentTotal,
                chapterCount);
            currentTotal += chapterCount;
        }

        return new NovelBookInfo(currentTotal, chapterInfo);
    }

    private static async Task<T?> TryReadAsync<T>(
        string path,
        CancellationToken ct)
        where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
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

    private static async Task WriteJsonAsync<T>(
        string path,
        T data,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path
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
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions, ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string GetChapterInfoKey(
        EpubChapter chapter,
        string? containerDirectory,
        int index)
    {
        if (!string.IsNullOrWhiteSpace(chapter.Href))
        {
            var href = chapter.Href.Split('#')[0];
            if (!string.IsNullOrWhiteSpace(containerDirectory) && Path.IsPathRooted(href))
            {
                var relativePath = Path.GetRelativePath(containerDirectory, href);
                if (!relativePath.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relativePath))
                {
                    return NormalizePath(relativePath);
                }
            }

            return NormalizePath(href);
        }

        return index.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new MacAbsoluteDateTimeOffsetJsonConverter());
        return options;
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

            throw new JsonException("Invalid Mac absolute date value.");
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
