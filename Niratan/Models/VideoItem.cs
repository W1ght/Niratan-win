using System;

namespace Niratan.Models;

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
    public long FileSizeBytes { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? SourceFolderPath { get; set; }
    public string? PosterPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? Tags { get; set; }
    public string? CollectionName { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsWatched { get; set; }
    public VideoSubtitleSelectionKind SubtitleSelectionKind { get; set; } = VideoSubtitleSelectionKind.None;
    public string? SubtitleSelectionPath { get; set; }
    public int? SubtitleSelectionTrackId { get; set; }
    public string? SubtitleSelectionTrackName { get; set; }
    public string? ProfileId { get; set; }
    public string? ProviderId { get; set; }
    public string? RemoteId { get; set; }
    public string? OriginalUrl { get; set; }
    public string? CanonicalUrl { get; set; }
    public string? RemoteThumbnailUrl { get; set; }
    public string? RemoteSubtitleLanguage { get; set; }

    public bool IsRemote => !string.IsNullOrWhiteSpace(ProviderId);

    public RemoteVideoIdentity? GetRemoteIdentity() =>
        !IsRemote
        || string.IsNullOrWhiteSpace(RemoteId)
        || string.IsNullOrWhiteSpace(OriginalUrl)
        || string.IsNullOrWhiteSpace(CanonicalUrl)
            ? null
            : new RemoteVideoIdentity(
                ProviderId!,
                RemoteId,
                OriginalUrl,
                CanonicalUrl,
                Title,
                RemoteThumbnailUrl,
                DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds) : null);

    public VideoSubtitleSelection GetSubtitleSelection() =>
        SubtitleSelectionKind switch
        {
            VideoSubtitleSelectionKind.ExternalFile when !string.IsNullOrWhiteSpace(SubtitleSelectionPath) =>
                VideoSubtitleSelection.ExternalFile(SubtitleSelectionPath),
            VideoSubtitleSelectionKind.EmbeddedTrack when SubtitleSelectionTrackId.HasValue =>
                VideoSubtitleSelection.EmbeddedTrack(
                    SubtitleSelectionTrackId.Value,
                    SubtitleSelectionTrackName),
            VideoSubtitleSelectionKind.RemoteLanguage when !string.IsNullOrWhiteSpace(RemoteSubtitleLanguage) =>
                VideoSubtitleSelection.RemoteLanguage(RemoteSubtitleLanguage),
            VideoSubtitleSelectionKind.Off => VideoSubtitleSelection.Off(),
            _ => VideoSubtitleSelection.None(),
        };

    public void SetSubtitleSelection(VideoSubtitleSelection selection)
    {
        SubtitleSelectionKind = selection.Kind;
        SubtitleSelectionPath = selection.ExternalPath;
        SubtitleSelectionTrackId = selection.TrackId;
        SubtitleSelectionTrackName = selection.TrackName;
        RemoteSubtitleLanguage = selection.RemoteLanguageCode;
    }
}
