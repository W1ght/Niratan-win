using System;

namespace Niratan.Models.Novel;

public sealed record NovelBookMetadata(
    string Id,
    string Title,
    string? Epub,
    string? Cover,
    string Folder,
    DateTimeOffset LastAccess,
    string? RenamedTitle = null,
    string? ProfileId = null,
    string? BookLanguage = null)
{
    public string DisplayTitle => RenamedTitle ?? Title;
}
