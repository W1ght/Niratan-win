using System;

namespace Hoshi.Models;

public class NovelBook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpenedAt { get; set; }
    public string? Language { get; set; }
    public string? UniqueIdentifier { get; set; }
    public string? ExtractedPath { get; set; }
    public int ChapterCount { get; set; }
}
