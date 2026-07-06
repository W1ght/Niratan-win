using System;

namespace Hoshi.Models;

public class VideoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? SubtitlePath { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpenedAt { get; set; }
    public double LastPositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public int ManualSortOrder { get; set; }
}
