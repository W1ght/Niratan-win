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
    public VideoSubtitleSelectionKind SubtitleSelectionKind { get; set; } = VideoSubtitleSelectionKind.None;
    public string? SubtitleSelectionPath { get; set; }
    public int? SubtitleSelectionTrackId { get; set; }
    public string? SubtitleSelectionTrackName { get; set; }
    public string? ProfileId { get; set; }

    public VideoSubtitleSelection GetSubtitleSelection() =>
        SubtitleSelectionKind switch
        {
            VideoSubtitleSelectionKind.ExternalFile when !string.IsNullOrWhiteSpace(SubtitleSelectionPath) =>
                VideoSubtitleSelection.ExternalFile(SubtitleSelectionPath),
            VideoSubtitleSelectionKind.EmbeddedTrack when SubtitleSelectionTrackId.HasValue =>
                VideoSubtitleSelection.EmbeddedTrack(
                    SubtitleSelectionTrackId.Value,
                    SubtitleSelectionTrackName),
            VideoSubtitleSelectionKind.Off => VideoSubtitleSelection.Off(),
            _ => VideoSubtitleSelection.None(),
        };

    public void SetSubtitleSelection(VideoSubtitleSelection selection)
    {
        SubtitleSelectionKind = selection.Kind;
        SubtitleSelectionPath = selection.ExternalPath;
        SubtitleSelectionTrackId = selection.TrackId;
        SubtitleSelectionTrackName = selection.TrackName;
    }
}
