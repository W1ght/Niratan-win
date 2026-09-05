using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Niratan.Models.Video;

public enum VideoDiscoveryFeedKind
{
    Explore,
    Recommendation,
}

public enum VideoDiscoverySearchCategory
{
    All,
    Movie,
    Series,
    Anime,
}

public enum VideoDiscoveryAggregateFeed
{
    Trending,
    Seasonal,
    Popular,
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

public sealed record VideoDiscoveryAggregateRequest(
    int Page = 1,
    int PageSize = 20,
    int? Year = null,
    string? GenreId = null,
    string? SortBy = null,
    string Language = "ja-JP",
    string Region = "JP",
    VideoDiscoveryAggregateFeed Feed = VideoDiscoveryAggregateFeed.Popular);

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
    string? LocalThumbnailPath = null,
    string? DisplayNumber = null);

public sealed record VideoResourceSearchRequest(
    VideoMetadataCandidate Identity,
    string? Query = null,
    string CategoryCode = "0_0");

public sealed record VideoSubtitleSearchRequest(
    VideoMetadataCandidate Identity,
    string? Query = null);

public sealed record JimakuSubtitleItem(
    int EntryId,
    string EntryName,
    string FileName,
    Uri DownloadUri,
    long? SizeBytes,
    string? Language,
    int? EpisodeNumber);

/// <summary>
/// In-process navigation payload for a provider-backed discovery work. The
/// route keeps only provider identity and already cached artwork; opening a
/// page never creates a local catalog item.
/// </summary>
public sealed record VideoDiscoveryNavigationTarget(
    VideoMetadataCandidate Identity,
    VideoDiscoveryArtwork Artwork,
    string? Overview = null,
    double? CommunityRating = null)
{
    public static VideoDiscoveryNavigationTarget FromItem(VideoDiscoveryItem item) => new(
        item.Identity with
        {
            PosterUrl = item.Identity.PosterUrl ?? item.PosterUrl,
            BackdropUrl = item.Identity.BackdropUrl ?? item.BackdropUrl,
        },
        new VideoDiscoveryArtwork(item.LocalPosterPath, item.LocalBackdropPath, null),
        item.Overview,
        item.CommunityRating);

    public static VideoDiscoveryNavigationTarget FromRelated(
        VideoRelatedItem item,
        VideoMetadataMediaKind mediaKind) => new(
            new VideoMetadataCandidate(
                item.ProviderId,
                item.ProviderItemId,
                mediaKind,
                item.Title,
                item.OriginalTitle,
                item.Year,
                null,
                null,
                null,
                (item.Aliases.IsDefault ? [] : item.Aliases)
                    .AddRange(new[] { item.Title, item.OriginalTitle }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToImmutableArray(),
                ImmutableDictionary<string, string>.Empty,
                item.SourceUrl,
                item.PosterUrl,
                item.BackdropUrl),
            new VideoDiscoveryArtwork(
                item.LocalPosterPath,
                item.LocalBackdropPath,
                null));
}

public enum VideoDiscoveryResourceRouteMode
{
    Download,
    Subscription,
}

public sealed record VideoDiscoveryResourceSearchTarget(
    VideoDiscoveryNavigationTarget Work,
    VideoDiscoveryResourceRouteMode Mode);

public enum VideoDiscoverySubtitleDestination
{
    SaveAs,
    ExistingVideo,
    Directory,
}
