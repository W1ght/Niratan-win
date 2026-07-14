using System.Collections.Generic;

namespace Niratan.Models.Novel;

public sealed class EpubBook
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Language { get; set; }
    public string? UniqueIdentifier { get; set; }
    public string ExtractedPath { get; set; } = string.Empty;
    public string ContainerDirectory { get; set; } = string.Empty;
    public List<EpubChapter> Chapters { get; set; } = [];
    public List<EpubTocItem> Toc { get; set; } = [];
    public Dictionary<string, EpubManifestItem> Manifest { get; set; } = [];
    public string? CoverHref { get; set; }
}
