using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public enum AniDbEpisodeType
{
    Regular,
    Special,
    Credits,
    Trailer,
    Parody,
    Other,
}

public enum AniDbMyListState
{
    Unknown = 0,
    OnHdd = 1,
    OnCd = 2,
    Deleted = 3,
}

public enum AniDbClientConnectionState
{
    Disabled,
    MissingConfiguration,
    Ready,
    Authenticating,
    Connected,
    LoginFailed,
    Banned,
    BackingOff,
    Error,
}

public sealed record AniDbClientConfiguration(
    string ClientId,
    int ClientVersion,
    string Username,
    string Password,
    int UdpLocalPort,
    bool HashMatchingEnabled,
    bool MyListSyncEnabled,
    bool AutoAddToMyList,
    AniDbMyListState DefaultMyListState,
    int RelationDepth)
{
    public string UdpServerHost { get; init; } = "api.anidb.net";
    public int UdpServerPort { get; init; } = 9000;
    public string? UdpBindAddress { get; init; }

    /// <summary>
    /// AniDB registers UDP and HTTP API clients independently. Older settings used
    /// <see cref="ClientId"/> for both; keep that as the compatibility fallback.
    /// </summary>
    public string? HttpClientId { get; init; }
    public int? HttpClientVersion { get; init; }
    public string EffectiveHttpClientId => string.IsNullOrWhiteSpace(HttpClientId)
        ? ClientId
        : HttpClientId.Trim();
    public int EffectiveHttpClientVersion => HttpClientVersion is > 0
        ? HttpClientVersion.Value
        : ClientVersion;
    public bool HasExplicitHttpClientIdentity =>
        !string.IsNullOrWhiteSpace(HttpClientId) && HttpClientVersion is > 0;
    public bool MyListReadWatched { get; init; } = true;
    public bool MyListReadUnwatched { get; init; } = true;
    public bool MyListSetWatched { get; init; } = true;
    public bool MyListSetUnwatched { get; init; } = true;
}

/// <summary>
/// A syntactically valid AniDB HTTP response that rejected the API request.
/// This is deliberately distinct from an empty/missing entity so persistent
/// imports cannot silently complete after an API configuration failure.
/// </summary>
internal sealed class AniDbHttpApiException : Exception
{
    public AniDbHttpApiException(int code, string? serverMessage = null)
        : base(BuildMessage(code, serverMessage))
    {
        Code = code;
    }

    public int Code { get; }
    public bool IsClientConfigurationError => Code == 302;

    private static string BuildMessage(int code, string? serverMessage)
    {
        if (code == 302)
        {
            return "AniDB rejected the HTTP API client ID/version. Configure a client and version registered for AniDB's HTTP API, then retry.";
        }

        var normalized = string.IsNullOrWhiteSpace(serverMessage)
            ? null
            : new string(serverMessage.Trim().Where(character => !char.IsControl(character)).Take(160).ToArray());
        return normalized is { Length: > 0 }
            ? $"AniDB HTTP API rejected the request (error {code}): {normalized}"
            : $"AniDB HTTP API rejected the request (error {code}).";
    }
}

public sealed record AniDbClientStatus(
    AniDbClientConnectionState State,
    string? Message,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RetryAt = null);

public sealed record AniDbEd2kHash(
    string Value,
    long FileSize,
    DateTimeOffset ModifiedAt,
    DateTimeOffset HashedAt)
{
    public string? Crc32 { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
}

public sealed record AniDbFileEpisodeLink(
    int EpisodeId,
    byte Percentage,
    bool IsOther,
    int Ordinal)
{
    public int AnimeId { get; init; }
    public bool IsManual { get; init; }
}

public sealed record AniDbEpisodeIdentity(int EpisodeId, int AnimeId);

public sealed record AniDbFileMatch(
    int FileId,
    int AnimeId,
    int? GroupId,
    string? GroupName,
    string? GroupShortName,
    bool Deprecated,
    int Version,
    bool? Censored,
    bool? CrcMatches,
    bool Chaptered,
    string? Quality,
    string? Source,
    ImmutableArray<string> AudioLanguages,
    ImmutableArray<string> SubtitleLanguages,
    string? Description,
    string? FileName,
    DateOnly? ReleasedAt,
    ImmutableArray<AniDbFileEpisodeLink> Episodes);

public sealed record AniDbTitle(string Language, string Type, string Value);

public sealed record AniDbEpisode(
    int EpisodeId,
    int AnimeId,
    AniDbEpisodeType Type,
    int Number,
    string RawNumber,
    int RuntimeMinutes,
    string? AirDate,
    string? Overview,
    double? Rating,
    ImmutableArray<AniDbTitle> Titles);

public sealed record AniDbRelation(
    int AnimeId,
    int RelatedAnimeId,
    string Type,
    string? Title)
{
    public bool? Verified { get; init; }
}

public sealed record AniDbTag(
    int TagId,
    int? ParentTagId,
    string Name,
    string? Description,
    int Weight,
    bool LocalSpoiler,
    bool GlobalSpoiler)
{
    public bool Verified { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record AniDbCreator(
    int CreatorId,
    string Name,
    string Role);

public sealed record AniDbVoiceActor(
    int CreatorId,
    string Name,
    string? Picture);

public sealed record AniDbCharacter(
    int CharacterId,
    string Name,
    string? Type,
    string? AppearanceType,
    string? Gender,
    string? Description,
    string? Picture,
    ImmutableArray<AniDbVoiceActor> VoiceActors);

public sealed record AniDbResource(
    int Type,
    string Identifier);

public sealed record AniDbSimilarAnime(
    int AnimeId,
    int Approval,
    int Total);

public sealed record AniDbAnime(
    int AnimeId,
    string Type,
    string Title,
    string? OriginalTitle,
    string? Overview,
    string? StartDate,
    string? EndDate,
    string? Picture,
    int EpisodeCount,
    bool Restricted,
    double? Rating,
    ImmutableArray<AniDbTitle> Titles,
    ImmutableArray<AniDbEpisode> Episodes,
    ImmutableArray<AniDbRelation> Relations,
    ImmutableArray<AniDbTag> Tags,
    ImmutableArray<AniDbCreator> Creators,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// True when the entity was assembled from the authenticated UDP ANIME and
    /// EPISODE commands because the separately registered HTTP client identity
    /// was rejected. The AID/EID identity is still authoritative, but HTTP XML
    /// should replace this reduced snapshot after the client is corrected.
    /// </summary>
    public bool IsDegraded { get; init; }
    public string? Url { get; init; }
    public ImmutableArray<AniDbCharacter> Characters { get; init; } = [];
    public ImmutableArray<AniDbResource> Resources { get; init; } = [];
    public ImmutableArray<AniDbSimilarAnime> SimilarAnime { get; init; } = [];
}

public sealed record AniDbMyListEntry(
    int? MyListId,
    int? FileId,
    int? EpisodeId,
    int? AnimeId,
    AniDbMyListState State,
    bool Watched,
    DateTimeOffset? WatchedAt,
    DateTimeOffset? UpdatedAt)
{
    public int FileState { get; init; }
}

public sealed record AniDbAssetSnapshot(
    Guid AssetId,
    string? Ed2k,
    long FileSize,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset? HashedAt,
    AniDbFileMatch? FileMatch,
    AniDbMyListEntry? MyList,
    string? LastError)
{
    public string? Crc32 { get; init; }
    public string? Md5 { get; init; }
    public string? Sha1 { get; init; }
}

public enum AniDbReleaseStatus
{
    Never,
    Matched,
    Unrecognized,
    Ignored,
    Manual,
}

/// <summary>
/// Persistent release-provider state for one content identity. AniDB/Shoko
/// identify the content by ED2K plus file size; a path or catalog asset id is
/// deliberately not part of this key.
/// </summary>
public sealed record AniDbReleaseState(
    string Ed2k,
    long FileSize,
    AniDbReleaseStatus Status,
    AniDbFileMatch? Match,
    DateTimeOffset? NextRetryAt,
    bool PreventRescan,
    string? LastError,
    DateTimeOffset? UpdatedAt)
{
    public bool IsAutomaticLookupDue(DateTimeOffset now) => Status switch
    {
        AniDbReleaseStatus.Never => true,
        AniDbReleaseStatus.Unrecognized => !PreventRescan
            && (NextRetryAt == null || NextRetryAt <= now),
        _ => false,
    };
}

/// <summary>
/// User-authored authoritative AniDB mapping. Every EID retains its owning AID,
/// percentage, and release order, including cross-anime combined files.
/// </summary>
public sealed record AniDbManualReleaseLink(
    int FileId,
    int AnimeId,
    ImmutableArray<AniDbFileEpisodeLink> Episodes);

public enum AniDbImportJobStage
{
    Queued,
    Hashing,
    FileLookup,
    AnimeMetadata,
    Grouping,
    CatalogProjection,
    MyList,
    Completed,
}

public enum AniDbImportJobState
{
    Queued,
    Running,
    Retry,
    Completed,
    Failed,
}

public sealed record AniDbImportJob(
    Guid AssetId,
    AniDbImportJobStage Stage,
    AniDbImportJobState State,
    int Attempts,
    DateTimeOffset ScheduledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError);

public sealed record AniDbReleaseMatchAttempt(
    Guid Id,
    Guid AssetId,
    string ProviderId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Result,
    string? Error)
{
    public string? Ed2k { get; init; }
    public long FileSize { get; init; }
}

public sealed record AniDbAnimeGroup(
    Guid GroupId,
    int MainAnimeId,
    ImmutableArray<int> AnimeIds,
    bool IsManual,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AniDbMyListJob(
    Guid AssetId,
    bool Watched,
    AniDbImportJobState State,
    int Attempts,
    DateTimeOffset ScheduledAt,
    DateTimeOffset UpdatedAt,
    string? LastError);

public interface IAniDbConfigurationProvider
{
    Task<AniDbClientConfiguration?> GetAsync(CancellationToken ct = default);
}

public interface IAniDbEd2kHasher
{
    Task<AniDbEd2kHash> HashAsync(string path, CancellationToken ct = default);
}

public interface IAniDbUdpTransport : IAsyncDisposable
{
    Task<string> SendAsync(
        string host,
        int serverPort,
        int localPort,
        string? bindAddress,
        string command,
        CancellationToken ct = default);
}

public interface IAniDbUdpClient : IAsyncDisposable
{
    event EventHandler<AniDbClientStatus>? StatusChanged;
    AniDbClientStatus Status { get; }
    Task<bool> TestLoginAsync(CancellationToken ct = default);
    Task<AniDbFileMatch?> GetFileAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task<AniDbAnime?> GetAnimeMetadataAsync(int animeId, CancellationToken ct = default);
    Task<AniDbEpisode?> GetEpisodeMetadataAsync(int episodeId, CancellationToken ct = default);
    Task<AniDbEpisodeIdentity?> GetEpisodeIdentityAsync(int episodeId, CancellationToken ct = default);
    Task<AniDbMyListEntry?> GetMyListAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task<AniDbMyListEntry?> AddOrUpdateMyListAsync(
        string ed2k,
        long fileSize,
        AniDbMyListState state,
        bool watched,
        DateTimeOffset? watchedAt,
        CancellationToken ct = default);
    Task DeleteMyListAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

public interface IAniDbHttpClient
{
    DateTimeOffset? RetryAt { get; }
    Task<AniDbAnime?> GetAnimeAsync(int animeId, CancellationToken ct = default);
    Task<AniDbAnime?> ProbeAnimeAsync(int animeId, CancellationToken ct = default);
    Task<ImmutableArray<AniDbMyListEntry>> GetMyListAsync(CancellationToken ct = default);
}

public readonly record struct AniDbScrapeAdmissionStamp(
    long Generation,
    bool StartedDuringReset);

public enum AniDbAssetIdentificationResult
{
    Unrecognized,
    ProjectedDegraded,
    ProjectedComplete,
}

public sealed record AniDbAssetIdentificationSettledEventArgs(
    Guid AssetId,
    AniDbAssetIdentificationResult Result);

public interface IAniDbImportService
{
    event EventHandler<AniDbClientStatus>? StatusChanged;
    event EventHandler<AniDbAssetIdentificationSettledEventArgs>? AssetIdentificationSettled;
    AniDbClientStatus Status { get; }
    long ScrapeGeneration { get; }
    AniDbScrapeAdmissionStamp CaptureScrapeAdmission();
    Task QueueSourceAsync(Guid sourceId, CancellationToken ct = default);
    Task QueueSourceAsync(
        Guid sourceId,
        long expectedScrapeGeneration,
        CancellationToken ct = default);
    Task QueueSourceAsync(
        Guid sourceId,
        AniDbScrapeAdmissionStamp admission,
        CancellationToken ct = default);
    Task QueueAssetAsync(Guid assetId, CancellationToken ct = default);
    Task QueueMyListStateAsync(string identityKey, bool watched, CancellationToken ct = default);
    Task SyncMyListAsync(CancellationToken ct = default);
    Task<bool> TestLoginAsync(CancellationToken ct = default);
    Task<AniDbReleaseState> GetReleaseStateAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default);
    Task LinkManualReleaseAsync(
        string ed2k,
        long fileSize,
        AniDbManualReleaseLink link,
        CancellationToken ct = default);
    Task UnlinkReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task IgnoreReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task ClearReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task RescanReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task ClearScrapingRecordsAsync(CancellationToken ct = default);
    Task ClearScrapingRecordsAsync(
        Func<IReadOnlyCollection<VideoManualAniDbIdentity>, CancellationToken, Task> synchronizedCleanup,
        CancellationToken ct = default);
}

public interface IAniDbCatalogStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<AniDbAssetSnapshot?> GetAssetAsync(Guid assetId, CancellationToken ct = default);
    Task<ImmutableArray<AniDbAssetSnapshot>> GetAssetsAsync(CancellationToken ct = default);
    Task UpsertHashAsync(Guid assetId, string identityKey, AniDbEd2kHash hash, CancellationToken ct = default);
    Task UpsertFileMatchAsync(Guid assetId, AniDbFileMatch? match, string? error, CancellationToken ct = default);
    Task<AniDbFileMatch?> GetFileMatchByHashAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task<AniDbReleaseState> GetReleaseStateAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default);
    Task LinkManualReleaseAsync(
        string ed2k,
        long fileSize,
        AniDbManualReleaseLink link,
        CancellationToken ct = default);
    Task UnlinkReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task IgnoreReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task ClearReleaseAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task ResetReleaseForRescanAsync(string ed2k, long fileSize, CancellationToken ct = default);
    Task UpsertAnimeAsync(AniDbAnime anime, CancellationToken ct = default);
    Task UpsertMyListAsync(Guid assetId, AniDbMyListEntry? entry, string? error, CancellationToken ct = default);
    Task ReplaceRemoteMyListAsync(
        ImmutableArray<AniDbMyListEntry> entries,
        DateTimeOffset fetchedAt,
        CancellationToken ct = default);
    Task<ImmutableArray<AniDbMyListEntry>> GetRemoteMyListAsync(CancellationToken ct = default);
    Task<AniDbAnime?> GetAnimeAsync(int animeId, CancellationToken ct = default);
    Task<AniDbAnime?> GetAnimeByEpisodeAsync(int episodeId, CancellationToken ct = default);
    Task<AniDbAnimeGroup> MaterializeGroupAsync(int animeId, CancellationToken ct = default);
    Task EnqueueImportJobAsync(Guid assetId, CancellationToken ct = default);
    Task<AniDbImportJob?> ClaimImportJobAsync(DateTimeOffset now, CancellationToken ct = default);
    Task AdvanceImportJobAsync(Guid assetId, AniDbImportJobStage stage, CancellationToken ct = default);
    Task RetryImportJobAsync(
        Guid assetId,
        AniDbImportJobStage stage,
        int attempts,
        DateTimeOffset scheduledAt,
        string error,
        bool terminal,
        CancellationToken ct = default);
    Task CompleteImportJobAsync(Guid assetId, CancellationToken ct = default);
    Task<ImmutableArray<AniDbImportJob>> GetImportJobsAsync(CancellationToken ct = default);
    Task RecordMatchAttemptAsync(AniDbReleaseMatchAttempt attempt, CancellationToken ct = default);
    Task<ImmutableArray<AniDbReleaseMatchAttempt>> GetMatchAttemptsAsync(
        Guid assetId,
        CancellationToken ct = default);
    Task<ImmutableArray<AniDbReleaseMatchAttempt>> GetMatchAttemptsAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default);
    Task EnqueueMyListJobAsync(Guid assetId, bool watched, CancellationToken ct = default);
    Task<AniDbMyListJob?> ClaimMyListJobAsync(DateTimeOffset now, CancellationToken ct = default);
    Task RetryMyListJobAsync(
        Guid assetId,
        int attempts,
        DateTimeOffset scheduledAt,
        string error,
        bool terminal,
        CancellationToken ct = default);
    Task CompleteMyListJobAsync(Guid assetId, CancellationToken ct = default);
    Task<ImmutableArray<AniDbMyListJob>> GetMyListJobsAsync(CancellationToken ct = default);
    Task<IReadOnlyCollection<VideoManualAniDbIdentity>> GetManualCatalogIdentitiesAsync(
        CancellationToken ct = default);
    Task ClearScrapingRecordsAsync(CancellationToken ct = default);
}
