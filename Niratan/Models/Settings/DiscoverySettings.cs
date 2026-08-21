using System.Collections.Generic;

namespace Niratan.Models.Settings;

public sealed class DiscoverySettings
{
    public List<string> ExploreProviderOrder { get; set; } = ["tmdb", "bangumi", "anilist"];
    public Dictionary<string, bool> EnabledRecommendationFeeds { get; set; } = new();
    public List<string> SubscribedVideoKeys { get; set; } = new();

    public DiscoverySettings Clone() => new()
    {
        ExploreProviderOrder = new List<string>(ExploreProviderOrder),
        EnabledRecommendationFeeds = new Dictionary<string, bool>(
            EnabledRecommendationFeeds,
            System.StringComparer.OrdinalIgnoreCase),
        SubscribedVideoKeys = new List<string>(SubscribedVideoKeys ?? []),
    };
}
