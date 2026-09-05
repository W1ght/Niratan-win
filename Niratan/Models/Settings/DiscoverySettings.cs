using System;
using System.Collections.Generic;
using Niratan.Enums;
using Niratan.Models.Video;

namespace Niratan.Models.Settings;

public sealed class DiscoverySettings
{
    public List<string> ExploreProviderOrder { get; set; } = ["tmdb", "anilist"];
    public Dictionary<string, bool> EnabledRecommendationFeeds { get; set; } = new();
    public List<string> SubscribedVideoKeys { get; set; } = new();
    public List<NyaaVideoSubscription> NyaaSubscriptions { get; set; } = new();

    public DiscoverySettings Clone() => new()
    {
        ExploreProviderOrder = new List<string>(ExploreProviderOrder),
        EnabledRecommendationFeeds = new Dictionary<string, bool>(
            EnabledRecommendationFeeds,
            System.StringComparer.OrdinalIgnoreCase),
        SubscribedVideoKeys = new List<string>(SubscribedVideoKeys ?? []),
        NyaaSubscriptions = (NyaaSubscriptions ?? []).ConvertAll(subscription => subscription.Clone()),
    };
}

public sealed class NyaaVideoSubscription
{
    public string Key { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderItemId { get; set; } = "";
    public VideoMetadataMediaKind MediaKind { get; set; }
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string? PosterUrl { get; set; }
    public string? PosterPath { get; set; }
    public int? Year { get; set; }
    public int? SeasonNumber { get; set; }
    public int? StartAfterEpisode { get; set; }
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string> ExternalIds { get; set; } = new();
    public string Query { get; set; } = "";
    public string CategoryCode { get; set; } = "0_0";
    public string ReleaseGroup { get; set; } = "";
    public string Resolution { get; set; } = "";
    public bool RequireTrusted { get; set; }
    public bool? Trusted { get; set; }
    public string? SelectedCategory { get; set; }
    public bool Enabled { get; set; } = true;
    public DownloadBackendKind DownloadBackend { get; set; } = DownloadBackendKind.MonoTorrent;
    public List<string> SeenItemIds { get; set; } = new();
    public List<string> ProcessedLogicalItemKeys { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }

    public NyaaVideoSubscription Clone() => new()
    {
        Key = Key,
        ProviderId = ProviderId,
        ProviderItemId = ProviderItemId,
        MediaKind = MediaKind,
        Title = Title,
        OriginalTitle = OriginalTitle,
        PosterUrl = PosterUrl,
        PosterPath = PosterPath,
        Year = Year,
        SeasonNumber = SeasonNumber,
        StartAfterEpisode = StartAfterEpisode,
        Aliases = new List<string>(Aliases ?? []),
        ExternalIds = new Dictionary<string, string>(ExternalIds ?? [], StringComparer.OrdinalIgnoreCase),
        Query = Query,
        CategoryCode = CategoryCode,
        ReleaseGroup = ReleaseGroup,
        Resolution = Resolution,
        RequireTrusted = RequireTrusted,
        Trusted = Trusted,
        SelectedCategory = SelectedCategory,
        Enabled = Enabled,
        DownloadBackend = DownloadBackend,
        SeenItemIds = new List<string>(SeenItemIds ?? []),
        ProcessedLogicalItemKeys = new List<string>(ProcessedLogicalItemKeys ?? []),
        CreatedAt = CreatedAt,
        LastCheckedAt = LastCheckedAt,
        LastError = LastError,
    };
}
