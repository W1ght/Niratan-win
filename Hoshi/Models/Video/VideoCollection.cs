using System;
using System.Collections.Generic;

namespace Hoshi.Models.Video;

public class VideoCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public VideoCollectionKind Kind { get; set; }
    public string? RuleJson { get; set; }
    public int ManualSortOrder { get; set; }
    public IReadOnlyList<VideoSmartRule> SmartRules { get; set; } = [];
    public IReadOnlyList<string> ItemIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
