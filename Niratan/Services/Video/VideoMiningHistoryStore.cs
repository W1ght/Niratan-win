using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoMiningHistoryStore
{
    IReadOnlyList<VideoMiningHistoryItem> Items { get; }
    Task<string?> RecordAsync(VideoMiningHistoryCapture capture, CancellationToken ct = default);
    Task UpdateLimitAsync(int limit, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed class VideoMiningHistoryStore : IVideoMiningHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly List<VideoMiningHistoryItem> _items;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _limit;

    static VideoMiningHistoryStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        JsonOptions.Converters.Add(new MacAbsoluteDateTimeJsonConverter());
        JsonOptions.Converters.Add(new SecondsTimeSpanJsonConverter());
    }

    public VideoMiningHistoryStore(int limit = 25)
        : this(Path.Combine(AppDataHelper.GetDataPath(), "video_mining_history.json"), limit)
    {
    }

    public VideoMiningHistoryStore(string filePath, int limit = 25)
    {
        _filePath = filePath;
        _limit = Math.Max(0, limit);
        _items = Load(filePath);
        Prune();
    }

    public IReadOnlyList<VideoMiningHistoryItem> Items => _items;

    public async Task<string?> RecordAsync(VideoMiningHistoryCapture capture, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_limit <= 0 || string.IsNullOrWhiteSpace(capture.SubtitleText))
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            var item = VideoMiningHistoryItem.FromCapture(capture);
            _items.Add(item);
            Prune();
            await SaveAsync(ct);
            return item.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateLimitAsync(int limit, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _limit = Math.Max(0, limit);
            Prune();
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _items.Clear();
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Prune()
    {
        _items.Sort((left, right) =>
        {
            var date = left.CreatedAt.CompareTo(right.CreatedAt);
            return date != 0 ? date : string.CompareOrdinal(left.Id, right.Id);
        });
        if (_limit <= 0)
        {
            _items.Clear();
            return;
        }

        if (_items.Count > _limit)
            _items.RemoveRange(0, _items.Count - _limit);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = tempPath + ".backup";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, _items, JsonOptions, ct);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, backupPath, ignoreMetadataErrors: true);
                File.Delete(backupPath);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch
        {
            // History is optional learning context and must not block playback.
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    private static List<VideoMiningHistoryItem> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return [];

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var items = JsonSerializer.Deserialize<List<VideoMiningHistoryItem>>(stream, JsonOptions) ?? [];
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.VideoTitle))
                    item.VideoTitle = item.VideoFileName;
            }
            return items;
        }
        catch
        {
            return [];
        }
    }

    private sealed class MacAbsoluteDateTimeJsonConverter : JsonConverter<DateTime>
    {
        private static readonly DateTimeOffset Reference =
            new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTime Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return Reference.AddSeconds(reader.GetDouble()).UtcDateTime;
            if (reader.TokenType == JsonTokenType.String
                && DateTime.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }
            throw new JsonException("Invalid video history date.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTime value,
            JsonSerializerOptions options)
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            writer.WriteNumberValue((new DateTimeOffset(utc) - Reference).TotalSeconds);
        }
    }

    private sealed class SecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number
                ? TimeSpan.FromSeconds(reader.GetDouble())
                : TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(
            Utf8JsonWriter writer,
            TimeSpan value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.TotalSeconds);
    }
}
