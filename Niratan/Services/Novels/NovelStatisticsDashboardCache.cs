using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Helpers;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

internal sealed record NovelStatisticsDashboardCachePayload(
    int SchemaVersion,
    string CacheKey,
    NovelStatisticsDashboardSnapshot Snapshot);

internal sealed class NovelStatisticsDashboardCache
    : IRecipient<NovelLibraryChangedMessage>
{
    internal const int SchemaVersion = 1;
    internal const string FileName = "statistics_dashboard_cache_v1.json";
    private readonly INiratanJsonFileStore _store;
    private readonly string _path;
    private string? _memoryKey;
    private NovelStatisticsDashboardSnapshot? _memorySnapshot;

    public NovelStatisticsDashboardCache(
        INiratanJsonFileStore store,
        IMessenger messenger)
        : this(store, messenger, Path.Combine(AppDataHelper.GetNovelBooksPath(), FileName))
    {
    }

    internal NovelStatisticsDashboardCache(
        INiratanJsonFileStore store,
        IMessenger messenger,
        string path)
    {
        _store = store;
        _path = path;
        messenger.RegisterAll(this);
    }

    public async Task<NovelStatisticsDashboardSnapshot?> TryLoadAsync(
        string key,
        CancellationToken ct)
    {
        if (_memoryKey == key)
            return _memorySnapshot;
        NovelJsonReadResult<NovelStatisticsDashboardCachePayload> result;
        try
        {
            result = await _store.ReadAsync<NovelStatisticsDashboardCachePayload>(_path, ct);
        }
        catch (NotSupportedException)
        {
            Invalidate();
            return null;
        }
        if (result.Status == NovelJsonReadStatus.Missing)
            return null;
        if (result.Status != NovelJsonReadStatus.Success
            || result.Value is not { } payload
            || payload.SchemaVersion != SchemaVersion
            || payload.CacheKey != key)
        {
            Invalidate();
            return null;
        }
        _memoryKey = key;
        _memorySnapshot = payload.Snapshot;
        return payload.Snapshot;
    }

    public async Task StoreAsync(
        string key,
        NovelStatisticsDashboardSnapshot snapshot,
        CancellationToken ct)
    {
        _memoryKey = key;
        _memorySnapshot = snapshot;
        await _store.WriteAsync(
            _path,
            new NovelStatisticsDashboardCachePayload(SchemaVersion, key, snapshot),
            ct);
    }

    public void Receive(NovelLibraryChangedMessage message) => Invalidate();

    private void Invalidate()
    {
        _memoryKey = null;
        _memorySnapshot = null;
        if (File.Exists(_path))
            File.Delete(_path);
    }

    public static string CreateKey(IReadOnlyList<NovelBook> books, DateOnly today)
    {
        var builder = new StringBuilder(today.ToString("yyyy-MM-dd"));
        foreach (var book in books.OrderBy(book => book.Id, StringComparer.Ordinal))
        {
            builder.Append('|').Append(book.Id).Append('|').Append(book.Title)
                .Append('|').Append(book.ExtractedPath);
            if (string.IsNullOrWhiteSpace(book.ExtractedPath)) continue;
            foreach (var file in new[] { "metadata.json", "bookinfo.json", "statistics.json" })
            {
                var path = Path.Combine(book.ExtractedPath, file);
                var info = new FileInfo(path);
                builder.Append('|').Append(file).Append(':')
                    .Append(info.Exists ? info.Length : -1).Append(':')
                    .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
