using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Niratan.Models;

public sealed record VideoMiningHistoryCapture(
    string SubtitleText,
    string VideoPath,
    string SubtitleSourceName,
    string? SubtitleSourcePath,
    VideoSubtitleSelectionKind SubtitleSelectionKind,
    int? EmbeddedSubtitleTrackId,
    TimeSpan CueStart,
    TimeSpan CueEnd,
    string? VideoTitle = null,
    RemoteVideoIdentity? RemoteVideoIdentity = null,
    string? SubtitleFormat = null);

public sealed class VideoMiningHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string SubtitleText { get; set; } = "";
    public string VideoFileName { get; set; } = "";
    public string VideoTitle { get; set; } = "";
    public string? VideoPath { get; set; }
    public RemoteVideoIdentity? RemoteVideoIdentity { get; set; }
    public string SubtitleSourceName { get; set; } = "";
    public string? SubtitleSourcePath { get; set; }
    public string? SubtitleFormat { get; set; }
    public VideoSubtitleSelectionKind SubtitleSelectionKind { get; set; }
    public int? EmbeddedSubtitleTrackId { get; set; }

    [JsonPropertyName("cueStart")]
    public double CueStartSeconds { get; set; }

    [JsonPropertyName("cueEnd")]
    public double CueEndSeconds { get; set; }

    [JsonIgnore]
    public TimeSpan CueStart => TimeSpan.FromSeconds(Math.Max(0, CueStartSeconds));

    [JsonIgnore]
    public TimeSpan CueEnd => TimeSpan.FromSeconds(Math.Max(0, CueEndSeconds));

    [JsonIgnore]
    public string CueStartText => VideoTimeText.Format(CueStart);

    [JsonIgnore]
    public string AutomationName => $"{SubtitleText}, {CueStartText}";

    public static VideoMiningHistoryItem FromCapture(
        VideoMiningHistoryCapture capture,
        DateTime? createdAt = null,
        string? id = null)
    {
        var videoFileName = string.IsNullOrWhiteSpace(capture.VideoPath)
            ? ""
            : RemoteVideoIdentity.IsPersistenceKey(capture.VideoPath, "youtube")
                ? capture.VideoPath[(capture.VideoPath.LastIndexOf('/') + 1)..]
                : Path.GetFileName(capture.VideoPath);
        return new VideoMiningHistoryItem
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            SubtitleText = capture.SubtitleText,
            VideoFileName = videoFileName,
            VideoTitle = string.IsNullOrWhiteSpace(capture.VideoTitle)
                ? videoFileName
                : capture.VideoTitle,
            VideoPath = capture.RemoteVideoIdentity == null ? capture.VideoPath : null,
            RemoteVideoIdentity = capture.RemoteVideoIdentity,
            SubtitleSourceName = string.IsNullOrWhiteSpace(capture.SubtitleSourceName)
                ? videoFileName
                : capture.SubtitleSourceName,
            SubtitleSourcePath = capture.SubtitleSourcePath,
            SubtitleFormat = capture.SubtitleFormat,
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
