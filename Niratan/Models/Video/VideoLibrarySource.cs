using System;
using System.Collections.Generic;

namespace Niratan.Models.Video;

public sealed class VideoLibrarySource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public VideoLibraryMediaType MediaType { get; set; } = VideoLibraryMediaType.Auto;
    public string Language { get; set; } = "ja-JP";
    public string Region { get; set; } = "JP";
    public IReadOnlyList<string> ProviderOrder { get; set; } = [];
    public long ScanGeneration { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }
    public string? LastError { get; set; }
}
