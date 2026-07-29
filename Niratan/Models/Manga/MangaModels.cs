using System;
using System.Collections.Generic;

namespace Niratan.Models.Manga;

public enum MangaContainerKind
{
    ImageFolder,
    ZipArchive,
    EpubArchive,
    Suwayomi,
    Mihon,
}

public enum MangaReaderLayout
{
    SinglePage,
    DoublePage,
    Continuous,
}

public enum MangaReadingDirection
{
    RightToLeft,
    LeftToRight,
}

public sealed class MangaPageDescriptor
{
    public int Index { get; set; }
    public string Path { get; set; } = string.Empty;
}

public sealed class MangaBook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string? RenamedTitle { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string? PageRootPath { get; set; }
    public string? MokuroMetadataPath { get; set; }
    public MangaContainerKind ContainerKind { get; set; }
    public List<MangaPageDescriptor> Pages { get; set; } = [];
    public string? CoverCachePath { get; set; }
    public int CurrentPageIndex { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastReadAt { get; set; }
    public DateTimeOffset? SourceModifiedAt { get; set; }
    public bool IsHidden { get; set; }
    public string? SuwayomiServerId { get; set; }
    public int? SuwayomiMangaId { get; set; }
    public int? SuwayomiChapterId { get; set; }
    public int? SuwayomiChapterIndex { get; set; }
    public string? MihonSourceId { get; set; }
    public string? MihonPackageName { get; set; }
    public string? MihonExtensionSha256 { get; set; }
    public string? MihonMangaUrl { get; set; }
    public string? MihonChapterUrl { get; set; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(RenamedTitle) ? Title : RenamedTitle.Trim();

    public int PageCount => Pages.Count;

    public double Progress =>
        PageCount <= 1
            ? (CurrentPageIndex > 0 ? 1 : 0)
            : Math.Clamp((double)CurrentPageIndex / (PageCount - 1), 0, 1);
}

public sealed record MangaTextRegion(
    string Id,
    int PageIndex,
    string BlockId,
    string LineId,
    string Sentence,
    int Utf16Offset,
    bool IsVertical,
    double X,
    double Y,
    double Width,
    double Height);

public sealed class MangaReaderPreferences
{
    public MangaReaderLayout Layout { get; set; } = MangaReaderLayout.SinglePage;
    public MangaReadingDirection Direction { get; set; } = MangaReadingDirection.RightToLeft;
    public int ZoomPercentage { get; set; } = 100;
    public bool IsGoogleOcrEnabled { get; set; }
    public bool GoogleOcrDisclosureAccepted { get; set; }
}

public sealed record MangaOcrCacheKey(
    string ItemId,
    int PageIndex,
    string PageIdentity,
    DateTimeOffset? ModifiedAt);

public sealed class MangaLibraryCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<MangaBook> Books { get; set; } = [];
    public MangaReaderPreferences ReaderPreferences { get; set; } = new();
}

public sealed record MangaReaderSession(
    MangaBook Book,
    MangaReaderPreferences Preferences);
