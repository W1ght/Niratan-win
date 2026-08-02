using System;
using System.Collections.Generic;
using System.Collections.Immutable;

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
    string? SourceUrl);

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
    ImmutableArray<VideoRelatedItem> RelatedItems = default)
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
        RelatedItems = RelatedItems.IsDefault ? [] : RelatedItems,
    };
}

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
    string? LocalBackdropPath = null);

public sealed record VideoArtworkCandidate(
    string ProviderId,
    string Url,
    string Kind,
    string? Language,
    int? Width,
    int? Height,
    string? AttributionUrl);

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
