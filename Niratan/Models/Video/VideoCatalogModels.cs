using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Niratan.Services.Video;

namespace Niratan.Models.Video;

public enum VideoLibraryMediaType
{
    Auto,
    Anime,
    JapaneseDramaTv,
    Movie,
}

public enum VideoCatalogNodeKind
{
    Unmatched,
    Movie,
    Series,
    Season,
    Episode,
}

public enum VideoMediaAssetKind
{
    LocalFile,
    RemoteResource,
}

public enum VideoMediaAvailability
{
    Available,
    Unavailable,
    Unknown,
}

public enum VideoCatalogJobKind
{
    IncrementalScan,
    FullScan,
    MetadataRefresh,
}

public enum VideoCatalogJobState
{
    Queued,
    Running,
    Paused,
    Completed,
    Cancelled,
    Interrupted,
    Failed,
}

public enum VideoCatalogMode
{
    Sqlite,
    LegacyReadOnly,
}

public sealed record VideoCatalogSourceSnapshot(
    Guid Id,
    string Name,
    string FolderPath,
    string NormalizedFolderPath,
    VideoLibraryMediaType MediaType,
    string Language,
    string Region,
    ImmutableArray<string> ProviderOrder,
    long ScanGeneration,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastScannedAt,
    string? LastError);

public sealed record VideoCatalogNodeSnapshot(
    Guid Id,
    Guid? ParentId,
    VideoCatalogNodeKind Kind,
    string PrimaryTitle,
    string? OriginalTitle,
    string? Subtitle,
    string? Overview,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    bool IsSpecial,
    bool IdentityLocked,
    ImmutableArray<string> Aliases,
    ImmutableDictionary<string, string> ExternalIds,
    DateTimeOffset? MetadataExpiresAt = null,
    ImmutableArray<string> Genres = default,
    ImmutableArray<string> Actors = default,
    ImmutableDictionary<string, string>? ProviderSourceUrls = null,
    string? BackdropPath = null,
    string? PosterPath = null,
    string? ThumbPath = null,
    string? LogoPath = null,
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
    public ImmutableHashSet<string> IdentityLockedProviders { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    public ImmutableArray<VideoCatalogArtworkSnapshot> ArtworkCandidates { get; init; } = [];
    public ImmutableArray<VideoMetadataSeason> Seasons { get; init; } = [];
    public ImmutableArray<VideoTmdbShowCrossReference> TmdbShowCrossReferences { get; init; } = [];
    public ImmutableArray<VideoTmdbEpisodeCrossReference> TmdbEpisodeCrossReferences { get; init; } = [];
    public ImmutableArray<VideoTmdbOrderingSnapshot> TmdbOrderings { get; init; } = [];
}

public sealed record VideoTmdbShowCrossReference(
    Guid SeriesNodeId,
    int AniDbAnimeId,
    int TmdbShowId,
    string? ChosenOrderingId,
    VideoTmdbOrderingType ChosenOrderingType,
    VideoMetadataMatchRating MatchRating,
    DateTimeOffset UpdatedAt);

public sealed record VideoTmdbEpisodeCrossReference(
    Guid EpisodeNodeId,
    Guid SeriesNodeId,
    int AniDbAnimeId,
    int AniDbEpisodeId,
    int TmdbShowId,
    int TmdbEpisodeId,
    string OrderingId,
    string? SeasonId,
    int SeasonNumber,
    int EpisodeNumber,
    int Ordinal,
    VideoMetadataMatchRating MatchRating,
    DateTimeOffset UpdatedAt);

public sealed record VideoTmdbOrderingSnapshot(
    Guid SeriesNodeId,
    int TmdbShowId,
    string OrderingId,
    VideoTmdbOrderingType Type,
    bool IsPreferred,
    bool IsUserPreferred,
    DateTimeOffset UpdatedAt);

public sealed record VideoCatalogArtworkSnapshot(
    Guid Id,
    Guid NodeId,
    string ProviderId,
    string Kind,
    string? RemoteUrl,
    string? LocalPath,
    string? Language,
    int? Width,
    int? Height,
    string? AttributionUrl,
    bool IsEnabled,
    bool IsDesired,
    bool IsPreferred,
    bool IsSelected,
    bool IsUserPreferred,
    int Ordinal,
    int DownloadAttempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VideoCatalogAssetSnapshot(
    Guid Id,
    string IdentityKey,
    VideoMediaAssetKind Kind,
    string Location,
    string Title,
    string ParentFolder,
    long FileSize,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset ImportedAt,
    DateTimeOffset LastSeenAt,
    VideoMediaAvailability Availability,
    int? EpisodeStart,
    int? EpisodeEnd,
    string? ProviderId,
    string? RemoteId,
    string? OriginalUrl,
    string? CanonicalUrl,
    string? RemoteThumbnailUrl,
    string? RemoteSubtitleLanguage,
    double? DurationSeconds,
    string? DisplayTitle,
    bool IsFavorite,
    ImmutableArray<string> Tags,
    string? BoundSubtitlePath,
    string? PosterPath,
    string? ProfileId,
    ImmutableArray<Guid> SourceIds,
    ImmutableArray<Guid> NodeIds,
    ImmutableArray<Guid> CollectionIds,
    bool IsHidden = false,
    bool CatalogResetPending = false);

public sealed record VideoCatalogCollectionSnapshot(
    Guid Id,
    string Name,
    VideoCollectionKind Kind,
    int ManualSortOrder,
    ImmutableArray<VideoSmartRule> SmartRules,
    ImmutableArray<Guid> AssetIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VideoMatchCandidateSnapshot(
    Guid Id,
    Guid AssetId,
    string ProviderId,
    string ProviderItemId,
    string Title,
    int? Year,
    double Score,
    double TitleScore,
    string Evidence,
    bool HasHardConflict,
    DateTimeOffset CreatedAt);

public sealed record VideoCatalogJobSnapshot(
    Guid Id,
    Guid? SourceId,
    VideoCatalogJobKind Kind,
    VideoCatalogJobState State,
    long Generation,
    int ProcessedCount,
    int TotalCount,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MatchedCount = 0,
    int NeedsReviewCount = 0,
    int FailedCount = 0);

public sealed record VideoCatalogSnapshot(
    VideoCatalogMode Mode,
    ImmutableArray<VideoCatalogSourceSnapshot> Sources,
    ImmutableArray<VideoCatalogNodeSnapshot> Nodes,
    ImmutableArray<VideoCatalogAssetSnapshot> Assets,
    ImmutableArray<VideoCatalogCollectionSnapshot> Collections,
    ImmutableArray<VideoMatchCandidateSnapshot> MatchCandidates,
    ImmutableArray<VideoCatalogJobSnapshot> Jobs,
    string? PersistentError,
    DateTimeOffset LoadedAt)
{
    public static VideoCatalogSnapshot Empty(VideoCatalogMode mode = VideoCatalogMode.Sqlite) =>
        new(
            mode,
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            DateTimeOffset.UtcNow);
}

public sealed record VideoCatalogInitializationResult(
    VideoCatalogMode Mode,
    VideoCatalogSnapshot Snapshot,
    string? MigrationError = null,
    string? LegacyCatalogSha256 = null);

public sealed record VideoProviderCacheEntry(
    string CacheKey,
    string ProviderId,
    string? ETag,
    DateTimeOffset? LastModified,
    byte[] Payload,
    string? ContentType,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt);

public sealed record VideoCatalogAssetUpsert(
    string IdentityKey,
    VideoMediaAssetKind Kind,
    string Location,
    string Title,
    string ParentFolder,
    long FileSize,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset ImportedAt,
    DateTimeOffset LastSeenAt,
    VideoMediaAvailability Availability,
    Guid? SourceId = null,
    int? EpisodeStart = null,
    int? EpisodeEnd = null,
    string? ProviderId = null,
    string? RemoteId = null,
    string? OriginalUrl = null,
    string? CanonicalUrl = null,
    string? RemoteThumbnailUrl = null,
    string? RemoteSubtitleLanguage = null,
    double? DurationSeconds = null,
    string? BoundSubtitlePath = null,
    string? PosterPath = null,
    string? ProfileId = null,
    string? Tags = null,
    bool IsFavorite = false);

/// <summary>
/// A single authoritative AniDB episode binding returned by the FILE command.
/// AniDB EIDs belong to episode nodes; release percentages and ordering stay on
/// the file-to-episode cross reference instead of being flattened onto a series.
/// </summary>
public sealed record VideoAniDbEpisodeProjection(
    int EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title,
    string? OriginalTitle,
    string? Overview,
    int Ordinal,
    byte Percentage,
    bool IsOther,
    DateOnly? AirDate)
{
    /// <summary>The authoritative AID which owns this EID.</summary>
    public int AnimeId { get; init; }
    public string? AnimeGroupId { get; init; }
    public VideoMetadataDetails? AnimeMetadata { get; init; }
}

/// <summary>
/// Desktop catalog projection of Shoko's release -> AniDB series/episode graph.
/// FID remains release-level data in the AniDB store; AID and EID are projected
/// to their own catalog node levels and the persistent group key only controls
/// presentation grouping.
/// </summary>
public sealed record VideoAniDbIdentityProjection(
    int AnimeId,
    int FileId,
    string GroupId,
    VideoMetadataDetails SeriesMetadata,
    ImmutableArray<VideoAniDbEpisodeProjection> Episodes);

/// <summary>
/// Exact user-authored AniDB identity allowed to survive a global scrape reset.
/// Asset scoping prevents a not-yet-projected manual release from protecting an
/// unrelated stale automatic catalog ancestry.
/// </summary>
public sealed record VideoManualAniDbIdentity(
    Guid AssetId,
    ImmutableHashSet<int> AnimeIds,
    ImmutableHashSet<int> EpisodeIds);

public sealed record VideoCatalogUserDataUpdate(
    string IdentityKey,
    string? DisplayTitle,
    IReadOnlyList<string> Tags,
    string? BoundSubtitlePath,
    string? PosterPath,
    string? ProfileId,
    bool? IsFavorite = null);

public sealed record VideoScanAsset(
    VideoCatalogAssetUpsert Asset,
    ParsedVideoIdentity ParsedIdentity,
    LocalVideoMetadata? LocalMetadata = null,
    bool SkipMetadataProcessing = false,
    bool RebuildHierarchy = false);

public sealed record VideoScanBatch(
    Guid SourceId,
    long ExpectedGeneration,
    DateTimeOffset ScannedAt,
    IReadOnlyList<VideoScanAsset> Assets,
    bool EnumerationCompleted,
    string? Error = null,
    bool IsFinal = true,
    int? TotalCount = null);
