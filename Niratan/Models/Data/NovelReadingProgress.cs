using System;

namespace Niratan.Models.Data;

public class NovelReadingProgress
{
    public string LocationJson { get; set; } = "{}";
    public double? Progression { get; set; }
    public string? ChapterHref { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
