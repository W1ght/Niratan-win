using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Video;

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
    public string? SourceId { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool IsAvailable { get; set; } = true;
    public Guid? CatalogAssetId { get; set; }
    public Guid? CatalogNodeId { get; set; }
    public Guid? CatalogSeriesNodeId { get; set; }
    public string? CatalogSeriesTitle { get; set; }
    public string? CatalogSeriesOriginalTitle { get; set; }
    public string? CatalogSeriesOverview { get; set; }
    public int? CatalogSeriesReleaseYear { get; set; }
    public VideoCatalogNodeKind CatalogNodeKind { get; set; } = VideoCatalogNodeKind.Unmatched;
    public VideoLibraryMediaType LibraryMediaType { get; set; } = VideoLibraryMediaType.Auto;
    public string? OriginalTitle { get; set; }
    public string? LocalizedSubtitle { get; set; }
    public string? Overview { get; set; }
    public int? ReleaseYear { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int? EpisodeEnd { get; set; }
    public int? AbsoluteEpisodeNumber { get; set; }
    public bool IsSpecialEpisode { get; set; }
    public bool IdentityLocked { get; set; }
    public bool NeedsReview { get; set; }
    public bool IsUnorganized { get; set; }
    public IReadOnlyDictionary<string, string> ExternalIds { get; set; } =
        new Dictionary<string, string>();
    public IReadOnlyList<VideoMatchCandidateSnapshot> MatchCandidates { get; set; } =
        Array.Empty<VideoMatchCandidateSnapshot>();
    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Actors { get; set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ProviderSourceUrls { get; set; } =
        new Dictionary<string, string>();
    public string? BackdropPath { get; set; }
    public string? ThumbPath { get; set; }
    public string? LogoPath { get; set; }
    public string? SeriesPosterPath { get; set; }
    public string? SeriesThumbPath { get; set; }
    public string? Tagline { get; set; }
    public string? OfficialRating { get; set; }
    public double? CommunityRating { get; set; }
    public int? EndYear { get; set; }
    public string? SeriesStatus { get; set; }
    public IReadOnlyList<string> MetadataTags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Studios { get; set; } = Array.Empty<string>();
    public IReadOnlyList<VideoPersonCredit> People { get; set; } = Array.Empty<VideoPersonCredit>();
    public IReadOnlyList<VideoRelatedItem> RelatedItems { get; set; } = Array.Empty<VideoRelatedItem>();
    public IReadOnlyList<VideoDiscoverySeason> CatalogSeriesSeasons { get; set; } = Array.Empty<VideoDiscoverySeason>();
    public string GenresText => string.Join(" · ", Genres);
    public string ActorsText => string.Join(" · ", Actors);
    public bool HasGenres => Genres.Count > 0;
    public bool HasActors => Actors.Count > 0;
    public bool HasProviderSources => ProviderSourceUrls.Count > 0;
    public bool HasOverview => !string.IsNullOrWhiteSpace(Overview);
    public string ProviderAttributionText => string.Join(" · ", ProviderSourceUrls.Keys.Select(id => id.ToUpperInvariant()));
    public Uri? PrimaryProviderSourceUri => Uri.TryCreate(ProviderSourceUrls.Values.FirstOrDefault(), UriKind.Absolute, out var uri)
        ? uri
        : null;
    public string RuntimeText => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\h\ mm\m" : @"m\m")
        : "";

    public string CatalogNumberingText => CatalogNodeKind switch
    {
        VideoCatalogNodeKind.Episode when SeasonNumber.HasValue && EpisodeNumber.HasValue =>
            FormatSeasonEpisodeNumber(),
        VideoCatalogNodeKind.Episode when AbsoluteEpisodeNumber.HasValue => FormatAbsoluteEpisodeNumber(),
        _ => ReleaseYear?.ToString() ?? "",
    };

    private string FormatSeasonEpisodeNumber()
    {
        var start = EpisodeNumber!.Value;
        return EpisodeEnd is > 0 && EpisodeEnd.Value > start
            ? $"S{SeasonNumber:00}E{start:00}–E{EpisodeEnd:00}"
            : $"S{SeasonNumber:00}E{start:00}";
    }

    private string FormatAbsoluteEpisodeNumber()
    {
        var start = AbsoluteEpisodeNumber!.Value;
        return EpisodeEnd is > 0 && EpisodeEnd.Value > start
            ? $"#{start}–{EpisodeEnd}"
            : $"#{start}";
    }

    public string ExternalIdsText =>
        string.Join(", ", ExternalIds.Select(pair => $"{pair.Key}: {pair.Value}"));
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
    public int SubtitleDelayMilliseconds { get; set; }
    public double PlaybackSpeed { get; set; } = 1;
    public double AudioDelaySeconds { get; set; }
    public VideoAudioSelectionKind AudioSelectionKind { get; set; }
    public int? AudioSelectionTrackId { get; set; }
    public int? AudioSelectionFfIndex { get; set; }
    public string? AudioSelectionTitle { get; set; }
    public string? AudioSelectionLanguage { get; set; }
    public string? AudioSelectionCodec { get; set; }

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

    public VideoAudioSelection GetAudioSelection() =>
        AudioSelectionKind switch
        {
            VideoAudioSelectionKind.EmbeddedTrack =>
                new VideoAudioSelection(
                    AudioSelectionKind,
                    AudioSelectionTrackId,
                    AudioSelectionFfIndex,
                    AudioSelectionTitle,
                    AudioSelectionLanguage,
                    AudioSelectionCodec),
            VideoAudioSelectionKind.Off => VideoAudioSelection.Off(),
            _ => VideoAudioSelection.None(),
        };
}
