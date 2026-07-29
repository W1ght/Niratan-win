using System.Collections.Generic;
using System.Text.Json.Serialization;
using Niratan.Helpers;

namespace Niratan.Models.Manga;

public enum SuwayomiAuthMode
{
    None,
    Basic,
    UiLogin,
    Bearer,
}

public sealed class SuwayomiServerConfiguration
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:4567";
    public SuwayomiAuthMode AuthMode { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? CredentialId { get; set; }
}

public sealed class SuwayomiSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public bool SupportsLatest { get; set; }
    public bool IsConfigurable { get; set; }
    public bool IsNsfw { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Lang)
        ? DisplayName
        : $"[{Lang}] {DisplayName}";
}

public sealed class SuwayomiCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class SuwayomiManga
{
    public int Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public bool Initialized { get; set; }
    public string? Artist { get; set; }
    public string? Author { get; set; }
    [JsonPropertyName("description")]
    public string? MangaDescription { get; set; }
    public List<string> Genre { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public bool InLibrary { get; set; }
}

public sealed class SuwayomiPagedManga
{
    public List<SuwayomiManga> MangaList { get; set; } = [];
    public bool HasNextPage { get; set; }
}

public sealed class SuwayomiChapter
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long UploadDate { get; set; }
    public double ChapterNumber { get; set; }
    public string? Scanlator { get; set; }
    public int MangaId { get; set; }
    public bool Read { get; set; }
    public bool Bookmarked { get; set; }
    public int LastPageRead { get; set; }
    public long LastReadAt { get; set; }
    public int Index { get; set; }
    public bool Downloaded { get; set; }
    public int PageCount { get; set; }

    public string Label => Read
        ? ResourceStringHelper.FormatString(
            "SuwayomiReadChapterLabel",
            "{0} · read",
            Name)
        : Name;
}
