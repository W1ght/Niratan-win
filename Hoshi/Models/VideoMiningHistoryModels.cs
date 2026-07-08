using System;
using System.IO;

namespace Hoshi.Models;

public sealed record VideoMiningHistoryCapture(
    string SubtitleText,
    string VideoPath,
    string SubtitleSourceName,
    string? SubtitleSourcePath,
    VideoSubtitleSelectionKind SubtitleSelectionKind,
    int? EmbeddedSubtitleTrackId,
    TimeSpan CueStart,
    TimeSpan CueEnd);

public sealed class VideoMiningHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string SubtitleText { get; set; } = "";
    public string VideoFileName { get; set; } = "";
    public string? VideoPath { get; set; }
    public string SubtitleSourceName { get; set; } = "";
    public string? SubtitleSourcePath { get; set; }
    public VideoSubtitleSelectionKind SubtitleSelectionKind { get; set; }
    public int? EmbeddedSubtitleTrackId { get; set; }
    public double CueStartSeconds { get; set; }
    public double CueEndSeconds { get; set; }

    public TimeSpan CueStart => TimeSpan.FromSeconds(Math.Max(0, CueStartSeconds));
    public TimeSpan CueEnd => TimeSpan.FromSeconds(Math.Max(0, CueEndSeconds));
    public string CueStartText => VideoTimeText.Format(CueStart);
    public string AutomationName => $"{SubtitleText}, {CueStartText}";

    public static VideoMiningHistoryItem FromCapture(
        VideoMiningHistoryCapture capture,
        DateTime? createdAt = null,
        string? id = null)
    {
        var videoFileName = string.IsNullOrWhiteSpace(capture.VideoPath)
            ? ""
            : Path.GetFileName(capture.VideoPath);
        return new VideoMiningHistoryItem
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            SubtitleText = capture.SubtitleText,
            VideoFileName = videoFileName,
            VideoPath = capture.VideoPath,
            SubtitleSourceName = string.IsNullOrWhiteSpace(capture.SubtitleSourceName)
                ? videoFileName
                : capture.SubtitleSourceName,
            SubtitleSourcePath = capture.SubtitleSourcePath,
            SubtitleSelectionKind = capture.SubtitleSelectionKind,
            EmbeddedSubtitleTrackId = capture.EmbeddedSubtitleTrackId,
            CueStartSeconds = Math.Max(0, capture.CueStart.TotalSeconds),
            CueEndSeconds = Math.Max(0, capture.CueEnd.TotalSeconds),
        };
    }
}

public sealed class VideoMiningHistoryRow
{
    public VideoMiningHistoryRow(VideoMiningHistoryItem item, bool showSourceHeader)
    {
        Item = item;
        ShowSourceHeader = showSourceHeader;
    }

    public VideoMiningHistoryItem Item { get; }
    public string Id => Item.Id;
    public string SubtitleText => string.IsNullOrWhiteSpace(Item.SubtitleText)
        ? "Blank Subtitle"
        : Item.SubtitleText;
    public string TimeText => Item.CueStartText;
    public bool ShowSourceHeader { get; }
    public string SourceHeader => ShowSourceHeader ? Item.SubtitleSourceName : "";
    public double SourceHeaderOpacity => ShowSourceHeader ? 1 : 0;
    public string AutomationName => Item.AutomationName;
}
