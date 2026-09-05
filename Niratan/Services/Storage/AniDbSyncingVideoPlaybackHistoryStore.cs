using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Services.Storage;

internal sealed class AniDbSyncingVideoPlaybackHistoryStore : IVideoPlaybackHistoryStore
{
    private readonly VideoPlaybackHistoryStore _inner;
    private readonly IAniDbImportService _aniDb;

    public AniDbSyncingVideoPlaybackHistoryStore(VideoPlaybackHistoryStore inner, IAniDbImportService aniDb)
    {
        _inner = inner;
        _aniDb = aniDb;
    }

    public Task<VideoPlaybackHistoryEntry> GetAsync(string identityKey, CancellationToken ct = default) =>
        _inner.GetAsync(identityKey, ct);

    public async Task SaveProgressAsync(string identityKey, double positionSeconds, double durationSeconds, CancellationToken ct = default)
    {
        await _inner.SaveProgressAsync(identityKey, positionSeconds, durationSeconds, ct);
        if (durationSeconds > 0 && positionSeconds / durationSeconds >= 0.975)
            await _aniDb.QueueMyListStateAsync(identityKey, true, ct);
    }

    public async Task SaveAsync(string identityKey, VideoPlaybackState state, CancellationToken ct = default)
    {
        await _inner.SaveAsync(identityKey, state, ct);
        var entry = await _inner.GetAsync(identityKey, ct);
        if (entry.IsFinished)
            await _aniDb.QueueMyListStateAsync(identityKey, true, ct);
    }

    public Task UpdateLastOpenedAsync(string identityKey, DateTimeOffset openedAt, CancellationToken ct = default) =>
        _inner.UpdateLastOpenedAsync(identityKey, openedAt, ct);

    public async Task MarkWatchedAsync(string identityKey, DateTimeOffset watchedAt, CancellationToken ct = default)
    {
        await _inner.MarkWatchedAsync(identityKey, watchedAt, ct);
        await _aniDb.QueueMyListStateAsync(identityKey, true, ct);
    }

    public async Task ClearProgressAsync(string identityKey, CancellationToken ct = default)
    {
        await _inner.ClearProgressAsync(identityKey, ct);
        await _aniDb.QueueMyListStateAsync(identityKey, false, ct);
    }
}
