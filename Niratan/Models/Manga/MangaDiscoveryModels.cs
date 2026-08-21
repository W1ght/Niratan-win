using System.Collections.Generic;

namespace Niratan.Models.Manga;

public enum MangaDiscoveryFeedKind
{
    Explore,
    Recommendation,
}

public sealed record MangaDiscoveryProvider(
    string Id,
    string DisplayName);

public sealed record MangaDiscoveryFeed(
    string ProviderId,
    string Id,
    string DisplayName,
    MangaDiscoveryFeedKind Kind,
    bool SupportsPaging = true);

public sealed record MangaDiscoveryRequest(
    string FeedId,
    int Page = 1);

public sealed record MangaDiscoveryItem(
    string ProviderId,
    string ProviderItemId,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    double? Score,
    int? Rank,
    string? PosterUrl,
    string? SourceUrl,
    IReadOnlyList<string>? Aliases = null);

public sealed record MangaDiscoveryPage(
    string ProviderId,
    string FeedId,
    int Page,
    int? TotalPages,
    IReadOnlyList<MangaDiscoveryItem> Items)
{
    public bool HasMore => TotalPages is null || Page < TotalPages.Value;
}
