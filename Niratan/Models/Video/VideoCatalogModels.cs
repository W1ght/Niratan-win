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
    ImmutableArray<VideoRelatedItem> RelatedItems = default);

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
    bool IsHidden = false);

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
    DateTimeOffset UpdatedAt);

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
    bool SkipMetadataProcessing = false);

public sealed record VideoScanBatch(
    Guid SourceId,
    long ExpectedGeneration,
    DateTimeOffset ScannedAt,
    IReadOnlyList<VideoScanAsset> Assets,
    bool EnumerationCompleted,
    string? Error = null,
    bool IsFinal = true,
    int? TotalCount = null);
