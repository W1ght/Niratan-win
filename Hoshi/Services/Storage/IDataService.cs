using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Storage;

public interface IDataService
{
    Task<IReadOnlyList<NovelBook>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    );
    Task<NovelBook?> GetNovelBookAsync(string bookId, CancellationToken ct = default);

    Task UpsertNovelBookAsync(NovelBook book, CancellationToken ct = default);
    Task DeleteNovelBookAsync(string bookId, CancellationToken ct = default);
    Task UpdateNovelLastOpenedAsync(
        string bookId,
        DateTime lastOpenedAt,
        CancellationToken ct = default
    );
    Task SaveNovelProgressAsync(
        string bookId,
        int chapterIndex,
        double progress,
        int currentCharacterCount,
        int totalCharacterCount,
        CancellationToken ct = default
    );
    Task SaveNovelBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default
    );

    Task<IReadOnlyList<VideoItem>> GetVideosAsync(
        string? queryText = null,
        CancellationToken ct = default
    );
    Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default);
    Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default);
    Task DeleteVideoAsync(string videoId, CancellationToken ct = default);
    Task UpdateVideoLastOpenedAsync(
        string videoId,
        DateTime lastOpenedAt,
        CancellationToken ct = default
    );
    Task SaveVideoProgressAsync(
        string videoId,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default
    );
}
