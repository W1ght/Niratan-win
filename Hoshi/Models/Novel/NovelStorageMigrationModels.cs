using System;
using System.Collections.Generic;

namespace Hoshi.Models.Novel;

public sealed record NovelStorageMigrationResult(
    bool IsSuccess,
    bool IsReadOnly,
    string? ErrorMessage,
    int MigratedBookCount);

public sealed record NovelStorageMigrationManifest(
    int Version,
    DateTimeOffset CompletedAt,
    IReadOnlyList<NovelStorageMigrationBook> Books);

public sealed record NovelStorageMigrationBook(
    string Id,
    string Folder,
    int ChapterIndex,
    int CharacterCount,
    int TotalCharacterCount,
    string? ProfileId);

public interface INovelStorageAccessState
{
    bool IsReadOnly { get; }
    string? ErrorMessage { get; }
}

internal sealed class NovelStorageAccessState : INovelStorageAccessState
{
    public bool IsReadOnly { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Apply(NovelStorageMigrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IsReadOnly = result.IsReadOnly;
        ErrorMessage = result.ErrorMessage;
    }
}
