using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Niratan.Models.Video;

public enum VideoDiscoveryFeedKind
{
    Explore,
    Recommendation,
}

public sealed record VideoDiscoveryFeed(
    string ProviderId,
    string Id,
    string DisplayName,
    VideoDiscoveryFeedKind Kind,
    ImmutableArray<VideoMetadataMediaKind> SupportedMediaKinds,
    bool SupportsPaging = true,
    bool SupportsFilters = false);

public sealed record VideoDiscoveryRequest(
    string FeedId,
    VideoMetadataMediaKind MediaKind,
    int Page = 1,
    int? Year = null,
    string? GenreId = null,
    string? SortBy = null,
    string? TimeWindow = null,
    string Language = "ja-JP",
    string Region = "JP");

public sealed record VideoDiscoveryItem(
    VideoMetadataCandidate Identity,
    string? Overview,
    double? CommunityRating,
    int? VoteCount,
    string? PosterUrl,
    string? BackdropUrl,
    string? LocalPosterPath = null,
    string? LocalBackdropPath = null);

public sealed record VideoDiscoveryPage(
    string ProviderId,
    string FeedId,
    int Page,
    int? TotalPages,
    ImmutableArray<VideoDiscoveryItem> Items,
    string? Error = null)
{
    public bool HasMore => TotalPages is null || Page < TotalPages.Value;
}

public sealed record VideoDiscoveryArtwork(
    string? PosterPath,
    string? BackdropPath,
    string? LogoPath);

public sealed record VideoDiscoveryDetails(
    VideoMetadataDetails Metadata,
    VideoDiscoveryArtwork Artwork,
    ImmutableArray<VideoDiscoverySeason> Seasons = default);

public sealed record VideoDiscoverySeason(
    int SeasonNumber,
    string Title,
    string? Overview,
    string? AirDate,
    int EpisodeCount,
    string? PosterPath,
    ImmutableArray<VideoDiscoveryEpisode> Episodes,
    string? LocalPosterPath = null);

public sealed record VideoDiscoveryEpisode(
    int EpisodeNumber,
    string Title,
    string? OriginalTitle,
    string? Overview,
    string? AirDate,
    int? RuntimeMinutes,
    string? ThumbnailPath,
    string? SourceUrl,
    string? LocalThumbnailPath = null);

public sealed record VideoResourceSearchRequest(
    VideoMetadataCandidate Identity,
    string? Query = null,
    string CategoryCode = "0_0");
