using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Video;

namespace Niratan.ViewModels.Components;

public sealed class VideoDiscoveryCardViewModel
{
    public VideoDiscoveryItem Item { get; }
    public VideoDiscoveryNavigationTarget NavigationTarget { get; }
    public VideoMetadataCandidate Identity => Item.Identity;
    public string Title => Item.Identity.Title;
    public string OriginalTitle => Item.Identity.OriginalTitle ?? "";
    public string YearText => Item.Identity.Year?.ToString(CultureInfo.CurrentCulture) ?? "";
    public string RatingText => Item.CommunityRating is double rating ? $"★ {rating:0.0}" : "";
    public string FactsText => string.Join(" · ", new[] { YearText, RatingText, Item.Identity.MediaKind.ToString() }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Overview => Item.Overview ?? "";
    public string SourceText => Item.Identity.ProviderId.ToUpperInvariant();
    public bool HasPoster => PosterImage is not null;
    public BitmapImage? PosterImage { get; }

    public VideoDiscoveryCardViewModel(VideoDiscoveryItem item)
    {
        Item = item;
        NavigationTarget = VideoDiscoveryNavigationTarget.FromItem(item);
        PosterImage = CreateImage(item.LocalPosterPath);
    }

    private static BitmapImage? CreateImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return new BitmapImage(new Uri(path, UriKind.Absolute));
        }
        catch
        {
            return null;
        }
    }
}

public sealed class VideoDiscoveryProviderOption
{
    public string Id { get; }
    public string DisplayName { get; }

    public VideoDiscoveryProviderOption(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }
}

public sealed class VideoDiscoveryMediaKindOption
{
    public VideoMetadataMediaKind Value { get; }
    public string DisplayName { get; }

    public VideoDiscoveryMediaKindOption(VideoMetadataMediaKind value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
}

public sealed class VideoDiscoverySearchCategoryOption
{
    public VideoDiscoverySearchCategory Value { get; }
    public string DisplayName { get; }

    public VideoDiscoverySearchCategoryOption(
        VideoDiscoverySearchCategory value,
        string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
}

public sealed class VideoDiscoverySortOption
{
    public string Value { get; }
    public string DisplayName { get; }

    public VideoDiscoverySortOption(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
}

public sealed class VideoDiscoverySectionViewModel
{
    public VideoDiscoveryFeed Feed { get; }
    public string Title => ResourceStringHelper.GetString(
        $"DiscoverFeed_{Feed.ProviderId}_{Feed.Id}",
        Feed.DisplayName);
    public ObservableCollection<VideoDiscoveryCardViewModel> Items { get; }
    public bool HasItems => Items.Count > 0;

    public VideoDiscoverySectionViewModel(VideoDiscoveryFeed feed, IEnumerable<VideoDiscoveryItem> items)
    {
        Feed = feed;
        Items = new(items.Select(item => new VideoDiscoveryCardViewModel(item)));
    }
}

public sealed class VideoDiscoveryPersonViewModel
{
    public string Name { get; }
    public string RoleText { get; }
    public BitmapImage? Image { get; }

    public VideoDiscoveryPersonViewModel(VideoPersonCredit person)
    {
        Name = person.Name;
        RoleText = person.Role ?? person.Type;
        if (!string.IsNullOrWhiteSpace(person.LocalImagePath))
        {
            try { Image = new BitmapImage(new Uri(person.LocalImagePath, UriKind.Absolute)); }
            catch { }
        }
    }
}

public sealed class VideoDiscoveryRelatedItemViewModel
{
    public VideoDiscoveryNavigationTarget NavigationTarget { get; }
    public string Title { get; }
    public string FactsText { get; }
    public BitmapImage? PosterImage { get; }

    public VideoDiscoveryRelatedItemViewModel(
        VideoRelatedItem item,
        VideoMetadataMediaKind mediaKind)
    {
        NavigationTarget = VideoDiscoveryNavigationTarget.FromRelated(item, mediaKind);
        Title = item.Title;
        FactsText = item.Year?.ToString(CultureInfo.CurrentCulture) ?? item.ProviderId.ToUpperInvariant();
        var imagePath = item.LocalPosterPath ?? item.LocalBackdropPath;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            try { PosterImage = new BitmapImage(new Uri(imagePath, UriKind.Absolute)); }
            catch { }
        }
    }
}

public sealed class VideoDiscoveryDetailsViewModel
{
    public VideoMetadataDetails Metadata { get; }
    public VideoDiscoveryArtwork Artwork { get; }
    public VideoMetadataCandidate Identity => new(
        Metadata.ProviderId,
        Metadata.ProviderItemId,
        Metadata.MediaKind,
        Metadata.Title,
        Metadata.OriginalTitle,
        Metadata.Year,
        Metadata.SeasonNumber,
        Metadata.EpisodeNumber,
        Metadata.AbsoluteEpisodeNumber,
        Metadata.Aliases,
        Metadata.ExternalIds,
        Metadata.SourceUrl);
    public string Title => Metadata.Title;
    public string OriginalTitle => Metadata.OriginalTitle ?? "";
    public string Subtitle => Metadata.Subtitle ?? "";
    public string Tagline => Metadata.Tagline ?? "";
    public string Overview => Metadata.Overview ?? "";
    public string FactsText => string.Join(" · ", new[]
    {
        Metadata.Year?.ToString(CultureInfo.CurrentCulture) ?? "",
        Metadata.CommunityRating is double rating ? $"★ {rating:0.0}" : "",
        Metadata.Status ?? "",
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string GenresText => string.Join(" · ", Metadata.Genres);
    public string TagsText => string.Join(" · ", Metadata.Tags);
    public string StudiosText => string.Join(" · ", Metadata.Studios);
    public string RatingText => Metadata.CommunityRating is double rating ? $"★ {rating:0.0}" : "";
    public string OfficialRating => Metadata.OfficialRating ?? "";
    public string ActorsText => string.Join(" · ", Metadata.Actors);
    public string SourceText => Metadata.ProviderId.ToUpperInvariant();
    public BitmapImage? PosterImage { get; }
    public BitmapImage? BackdropImage { get; }
    public IReadOnlyList<VideoDiscoveryPersonViewModel> People { get; }
    public IReadOnlyList<VideoDiscoveryRelatedItemViewModel> RelatedItems { get; }
    public bool HasPeople => People.Count > 0;
    public bool HasRelatedItems => RelatedItems.Count > 0;

    public VideoDiscoveryDetailsViewModel(VideoMetadataCandidate identity)
        : this(CreatePlaceholderDetails(identity, null))
    {
    }

    public VideoDiscoveryDetailsViewModel(VideoDiscoveryItem item)
        : this(CreatePlaceholderDetails(
            item.Identity,
            new VideoDiscoveryArtwork(item.LocalPosterPath, item.LocalBackdropPath, null),
            item.Overview,
            item.CommunityRating))
    {
    }

    public VideoDiscoveryDetailsViewModel(VideoDiscoveryNavigationTarget target)
        : this(CreatePlaceholderDetails(
            target.Identity,
            target.Artwork,
            target.Overview,
            target.CommunityRating))
    {
    }

    private static VideoDiscoveryDetails CreatePlaceholderDetails(
        VideoMetadataCandidate identity,
        VideoDiscoveryArtwork? artwork,
        string? overview = null,
        double? communityRating = null)
    {
        var metadata = new VideoMetadataDetails(
                identity.ProviderId,
                identity.ProviderItemId,
                identity.MediaKind,
                identity.Title,
                identity.OriginalTitle,
                null,
                overview,
                identity.Year,
                identity.SeasonNumber,
                identity.EpisodeNumber,
                identity.AbsoluteEpisodeNumber,
                identity.Aliases,
                [],
                [],
                identity.ExternalIds,
                identity.SourceUrl,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                CommunityRating: communityRating)
            .WithInitializedCollections();

        return new VideoDiscoveryDetails(
            metadata,
            artwork ?? new VideoDiscoveryArtwork(null, null, null));
    }

    public VideoDiscoveryDetailsViewModel(VideoMetadataCandidate identity, VideoDiscoveryArtwork artwork)
        : this(CreatePlaceholderDetails(identity, artwork))
    {
    }

    public VideoDiscoveryDetailsViewModel(VideoDiscoveryDetails details)
    {
        // Provider responses and lightweight card placeholders may omit optional
        // immutable collections. Normalize them before the detail projection so
        // opening a card cannot crash the UI thread.
        Metadata = details.Metadata.WithInitializedCollections();
        Artwork = details.Artwork;
        PosterImage = CreateImage(Artwork.PosterPath);
        BackdropImage = CreateImage(Artwork.BackdropPath);
        People = Metadata.People.Select(person => new VideoDiscoveryPersonViewModel(person)).ToList();
        RelatedItems = Metadata.RelatedItems
            .Select(item => new VideoDiscoveryRelatedItemViewModel(item, Metadata.MediaKind))
            .ToList();
    }

    public VideoDiscoveryDetailsViewModel(
        VideoDiscoveryDetails details,
        VideoDiscoveryArtwork? fallbackArtwork)
        : this(details with { Artwork = MergeArtwork(details.Artwork, fallbackArtwork) })
    {
    }

    private static VideoDiscoveryArtwork MergeArtwork(
        VideoDiscoveryArtwork artwork,
        VideoDiscoveryArtwork? fallback)
    {
        if (fallback is null)
            return artwork;
        return artwork with
        {
            PosterPath = artwork.PosterPath ?? fallback.PosterPath,
            BackdropPath = artwork.BackdropPath ?? fallback.BackdropPath,
            LogoPath = artwork.LogoPath ?? fallback.LogoPath,
        };
    }

    private static BitmapImage? CreateImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try { return new BitmapImage(new Uri(path, UriKind.Absolute)); }
        catch { return null; }
    }
}
