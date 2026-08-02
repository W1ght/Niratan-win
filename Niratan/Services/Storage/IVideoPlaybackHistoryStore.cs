using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Storage;

public sealed record VideoPlaybackHistoryEntry(
    VideoPlaybackState State,
    DateTimeOffset? UpdatedAt,
    bool IsFinished);

public interface IVideoPlaybackHistoryStore
{
    Task<VideoPlaybackHistoryEntry> GetAsync(string identityKey, CancellationToken ct = default);
    Task SaveProgressAsync(
        string identityKey,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default);
    Task SaveAsync(string identityKey, VideoPlaybackState state, CancellationToken ct = default);
    Task UpdateLastOpenedAsync(string identityKey, DateTimeOffset openedAt, CancellationToken ct = default);
    Task MarkWatchedAsync(string identityKey, DateTimeOffset watchedAt, CancellationToken ct = default);
    Task ClearProgressAsync(string identityKey, CancellationToken ct = default);
}
