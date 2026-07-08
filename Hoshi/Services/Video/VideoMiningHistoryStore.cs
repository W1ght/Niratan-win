using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models;

namespace Hoshi.Services.Video;

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
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly List<VideoMiningHistoryItem> _items;
    private int _limit;

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

        var item = VideoMiningHistoryItem.FromCapture(capture);
        _items.Add(item);
        Prune();
        await SaveAsync(ct);
        return item.Id;
    }

    public async Task UpdateLimitAsync(int limit, CancellationToken ct = default)
    {
        _limit = Math.Max(0, limit);
        Prune();
        await SaveAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        await SaveAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        _items.Clear();
        await SaveAsync(ct);
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, _items, JsonOptions, ct);
        }
        catch
        {
            // History is optional learning context and must not block playback.
        }
    }

    private static List<VideoMiningHistoryItem> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return [];

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<VideoMiningHistoryItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
