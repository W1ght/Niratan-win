using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public interface IReaderHighlightService
{
    Task<IReadOnlyList<ReaderHighlight>> LoadAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task SaveAsync(
        string bookRootPath,
        IReadOnlyList<ReaderHighlight> highlights,
        CancellationToken ct = default);

    ReaderHighlight CreateFromChapterSelection(
        Guid id,
        int chapterIndex,
        int chapterCharacterOffset,
        int rawOffset,
        string text,
        ReaderHighlightColor color,
        DateTimeOffset createdAt,
        IReadOnlyList<int> chapterCharacterCounts);

    IReadOnlyList<ReaderHighlight> GetChapterHighlights(
        IEnumerable<ReaderHighlight> highlights,
        IReadOnlyList<int> chapterCharacterCounts,
        int chapterIndex);

    ReaderHighlightJumpTarget? ResolveJumpTarget(
        ReaderHighlight highlight,
        IReadOnlyList<int> chapterCharacterCounts);

    string? SerializeForWebView(IEnumerable<ReaderHighlight> highlights);
}
