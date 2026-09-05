using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Niratan.Models.Video;

[Flags]
public enum VideoMetadataCapabilities
{
    None = 0,
    Search = 1,
    Details = 2,
    Artwork = 4,
    EpisodeOrder = 8,
    TitleIndex = 16,
}

public enum VideoMetadataMediaKind
{
    Movie,
    Series,
    Season,
    Episode,
    Anime,
}

/// <summary>
/// Confidence of a provider cross-reference. Numeric values intentionally
/// match Shoko's MatchRating contract so persisted xrefs remain comparable.
/// </summary>
public enum VideoMetadataMatchRating : byte
{
    None = 0,
    UserVerified = 1,
    DateAndTitleMatches = 2,
    DateMatches = 3,
    TitleMatches = 4,
    FirstAvailable = 5,
    TitleKindaMatches = 7,
    DateAndTitleKindaMatches = 8,
    DateKindaMatches = 9,
}

/// <summary>
/// TMDB episode-group ordering type. Values follow TMDB/Shoko, including the
/// synthetic default ordering which is represented by the show ID.
/// </summary>
public enum VideoTmdbOrderingType
{
    Default = -1,
    Unknown = 0,
    OriginalAirDate = 1,
    Absolute = 2,
    Dvd = 3,
    Digital = 4,
    StoryArc = 5,
    Production = 6,
    Tv = 7,
}

public sealed record VideoMetadataSearchQuery(
    string Title,
    VideoMetadataMediaKind MediaKind,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    string Language,
    string Region,
    ImmutableDictionary<string, string> ExternalIds);

public sealed record VideoMetadataCandidate(
    string ProviderId,
    string ProviderItemId,
    VideoMetadataMediaKind MediaKind,
    string Title,
    string? OriginalTitle,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    ImmutableArray<string> Aliases,
    ImmutableDictionary<string, string> ExternalIds,
    string? SourceUrl,
    string? PosterUrl = null,
    string? BackdropUrl = null);

public sealed record VideoMetadataDetails(
    string ProviderId,
    string ProviderItemId,
    VideoMetadataMediaKind MediaKind,
    string Title,
    string? OriginalTitle,
    string? Subtitle,
    string? Overview,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    ImmutableArray<string> Aliases,
    ImmutableArray<string> Genres,
    ImmutableArray<string> Actors,
    ImmutableDictionary<string, string> ExternalIds,
    string? SourceUrl,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt,
    string? Tagline = null,
    string? OfficialRating = null,
    double? CommunityRating = null,
    int? EndYear = null,
    string? Status = null,
    ImmutableArray<string> Tags = default,
    ImmutableArray<string> Studios = default,
    ImmutableArray<VideoPersonCredit> People = default,
    ImmutableArray<VideoRelatedItem> RelatedItems = default,
    ImmutableArray<VideoMetadataSeason> Seasons = default,
    VideoTmdbOrdering? TmdbOrdering = null)
{
    public VideoMetadataDetails WithInitializedCollections() => this with
    {
        Aliases = Aliases.IsDefault ? [] : Aliases,
        Genres = Genres.IsDefault ? [] : Genres,
        Actors = Actors.IsDefault ? [] : Actors,
        ExternalIds = ExternalIds ?? ImmutableDictionary<string, string>.Empty,
        Tags = Tags.IsDefault ? [] : Tags,
        Studios = Studios.IsDefault ? [] : Studios,
        People = People.IsDefault ? [] : People,
        RelatedItems = RelatedItems.IsDefault
            ? []
            : RelatedItems.Select(item => item with
            {
                Aliases = item.Aliases.IsDefault ? [] : item.Aliases,
            }).ToImmutableArray(),
        Seasons = Seasons.IsDefault
            ? []
            : Seasons.Select(season => season with
            {
                Episodes = season.Episodes.IsDefault ? [] : season.Episodes,
            }).ToImmutableArray(),
    };
}

public sealed record VideoMetadataSeason(
    int SeasonNumber,
    string Title,
    string? Overview,
    string? AirDate,
    int? EpisodeCount,
    string? PosterUrl,
    ImmutableArray<VideoMetadataEpisode> Episodes = default)
{
    public int? TmdbShowId { get; init; }

    public int? TmdbSeasonId { get; init; }

    public string? TmdbOrderingId { get; init; }

    public string? TmdbEpisodeGroupId { get; init; }

    public VideoTmdbOrderingType TmdbOrderingType { get; init; } = VideoTmdbOrderingType.Default;

    public int Ordinal { get; init; }

    public VideoMetadataMatchRating MatchRating { get; init; } = VideoMetadataMatchRating.FirstAvailable;
}

public sealed record VideoMetadataEpisode(
    int EpisodeNumber,
    string Title,
    string? OriginalTitle,
    string? Overview,
    string? AirDate,
    int? RuntimeMinutes,
    string? ThumbnailUrl,
    string? SourceUrl,
    string? DisplayNumber = null)
{
    public int? TmdbShowId { get; init; }

    public int? TmdbEpisodeId { get; init; }

    public int? TmdbSeasonId { get; init; }

    public string? TmdbOrderingId { get; init; }

    public string? TmdbEpisodeGroupId { get; init; }

    public int Ordinal { get; init; }

    public VideoMetadataMatchRating MatchRating { get; init; } = VideoMetadataMatchRating.FirstAvailable;
}

public sealed record VideoTmdbOrdering(
    int TmdbShowId,
    string OrderingId,
    VideoTmdbOrderingType Type,
    bool IsPreferred);

public sealed record VideoPersonCredit(
    string ProviderPersonId,
    string Name,
    string? Role,
    string Type,
    string? ImageUrl,
    string? LocalImagePath = null);

public sealed record VideoRelatedItem(
    string ProviderId,
    string ProviderItemId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,
    string? BackdropUrl,
    string? SourceUrl,
    string? LocalPosterPath = null,
    string? LocalBackdropPath = null,
    ImmutableArray<string> Aliases = default);

public sealed record VideoArtworkCandidate(
    string ProviderId,
    string Url,
    string Kind,
    string? Language,
    int? Width,
    int? Height,
    string? AttributionUrl)
{
    /// <summary>
    /// Optional catalog owner override for provider detail images such as season
    /// posters and episode stills. When omitted, the matched metadata owner is used.
    /// </summary>
    public VideoMetadataMediaKind? OwnerKind { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Shoko-style cross-reference state. Refreshes may update availability policy,
    /// but the catalog owns the stable preferred/selected decision.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    public bool IsDesired { get; init; } = true;

    public bool IsPreferred { get; init; }

    public int Ordinal { get; init; }
}

public sealed record VideoMetadataMatchScore(
    VideoMetadataCandidate Candidate,
    double Score,
    double TitleScore,
    bool HasHardConflict,
    string Evidence,
    bool IsAccepted,
    bool IsIdentityLocked);

public sealed record VideoMetadataFieldValue(
    string Field,
    string? Value,
    string ProviderId,
    int Priority,
    bool IsLocked,
    DateTimeOffset UpdatedAt);

public sealed record VideoMetadataMergeResult(
    ImmutableDictionary<string, VideoMetadataFieldValue> Fields,
    ImmutableArray<string> Providers);

public sealed record VideoRematchFieldChange(
    string Field,
    string? CurrentValue,
    string? ProposedValue,
    string ProviderId);

public sealed record VideoRematchPreview(
    Guid AssetId,
    ImmutableArray<Guid> CurrentNodeIds,
    VideoMetadataCandidate Candidate,
    ImmutableArray<VideoRematchFieldChange> FieldChanges,
    string ProposedHierarchy,
    bool RequiresCrossSeasonConfirmation);

public sealed record VideoMetadataRefreshResult(
    Guid AssetId,
    bool Matched,
    bool NeedsReview,
    string? ProviderId,
    string? Error,
    ImmutableArray<VideoMetadataMatchScore> Candidates);

public sealed record VideoMetadataTaskSnapshot(
    Guid JobId,
    Guid? SourceId,
    VideoCatalogJobState State,
    int ProcessedCount,
    int TotalCount,
    int MatchedCount,
    int NeedsReviewCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int FailedCount = 0);
