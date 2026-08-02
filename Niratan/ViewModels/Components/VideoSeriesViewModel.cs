using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;

namespace Niratan.ViewModels.Components;

public sealed class VideoSeriesViewModel
{
    private readonly IReadOnlyList<VideoItem> _episodes;

    public VideoSeriesViewModel(Guid id, IEnumerable<VideoItem> videos)
    {
        Id = id;
        _episodes = videos
            .OrderBy(video => video.AbsoluteEpisodeNumber ?? int.MaxValue)
            .ThenBy(video => video.SeasonNumber ?? int.MaxValue)
            .ThenBy(video => video.EpisodeNumber ?? int.MaxValue)
            .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var representative = _episodes
            .OrderByDescending(MetadataWeight)
            .ThenByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
            .First();
        Title = representative.CatalogSeriesTitle ?? representative.Title;
        OriginalTitle = representative.OriginalTitle;
        Overview = representative.Overview;
        Genres = representative.Genres;
        Actors = representative.Actors;
        Tags = representative.MetadataTags;
        Studios = representative.Studios;
        People = new ObservableCollection<VideoPersonViewModel>(
            representative.People.Select(person => new VideoPersonViewModel(person)));
        RelatedItems = new ObservableCollection<VideoRelatedItemViewModel>(
            representative.RelatedItems.Select(item => new VideoRelatedItemViewModel(item)));
        Tagline = representative.Tagline;
        OfficialRating = representative.OfficialRating;
        CommunityRating = representative.CommunityRating;
        Status = representative.SeriesStatus;
        EndYear = representative.EndYear;
        ProviderSourceUrls = representative.ProviderSourceUrls;
        PosterPath = FirstExisting(_episodes.Select(video => video.SeriesPosterPath ?? video.PosterPath));
        BackdropPath = FirstExisting(_episodes.Select(video => video.SeriesThumbPath ?? video.BackdropPath));
        LogoPath = FirstExisting(_episodes.Select(video => video.LogoPath));

        Episodes = new ObservableCollection<VideoItemViewModel>(_episodes
            .Where(video => !video.IsSpecialEpisode)
            .Select(video => new VideoItemViewModel(video)));
        SpecialFeatures = new ObservableCollection<VideoItemViewModel>(_episodes
            .Where(video => video.IsSpecialEpisode)
            .Select(video => new VideoItemViewModel(video)));
        Seasons = new ObservableCollection<VideoSeasonViewModel>(_episodes
            .Where(video => !video.IsSpecialEpisode && video.SeasonNumber.HasValue)
            .GroupBy(video => video.SeasonNumber)
            .OrderBy(group => group.Key ?? int.MaxValue)
            .Select(group => new VideoSeasonViewModel(group.Key, group.Count(), PosterPath)));
        FirstEpisode = Episodes.FirstOrDefault();
        ContinueEpisode = Episodes
            .Where(item => item.Video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds
                           && !item.Video.IsWatched)
            .OrderByDescending(item => item.Video.LastOpenedAt ?? item.Video.ImportedAt)
            .FirstOrDefault();
        PrimaryPlayItem = ContinueEpisode ?? FirstEpisode;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string? OriginalTitle { get; }
    public string? Overview { get; }
    public IReadOnlyList<string> Genres { get; }
    public IReadOnlyList<string> Actors { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> Studios { get; }
    public ObservableCollection<VideoPersonViewModel> People { get; }
    public ObservableCollection<VideoRelatedItemViewModel> RelatedItems { get; }
    public IReadOnlyDictionary<string, string> ProviderSourceUrls { get; }
    public string? Tagline { get; }
    public string? OfficialRating { get; }
    public double? CommunityRating { get; }
    public string? Status { get; }
    public int? EndYear { get; }
    public string? PosterPath { get; }
    public string? BackdropPath { get; }
    public string? LogoPath { get; }
    public ObservableCollection<VideoItemViewModel> Episodes { get; }
    public ObservableCollection<VideoItemViewModel> SpecialFeatures { get; }
    public ObservableCollection<VideoSeasonViewModel> Seasons { get; }
    public VideoItemViewModel? FirstEpisode { get; }
    public VideoItemViewModel? ContinueEpisode { get; }
    public VideoItemViewModel? PrimaryPlayItem { get; }
    public int EpisodeCount => _episodes.Count;
    public bool HasOverview => !string.IsNullOrWhiteSpace(Overview);
    public bool HasOriginalTitle => !string.IsNullOrWhiteSpace(OriginalTitle)
                                    && !string.Equals(OriginalTitle, Title, StringComparison.CurrentCultureIgnoreCase);
    public bool HasGenres => Genres.Count > 0;
    public bool HasActors => Actors.Count > 0;
    public bool HasPeople => People.Count > 0;
    public bool HasActorsOnly => People.Count == 0 && Actors.Count > 0;
    public bool HasTags => Tags.Count > 0;
    public bool HasStudios => Studios.Count > 0;
    public bool HasTagline => !string.IsNullOrWhiteSpace(Tagline);
    public bool HasCommunityRating => CommunityRating.HasValue;
    public bool HasOfficialRating => !string.IsNullOrWhiteSpace(OfficialRating);
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasRelatedItems => RelatedItems.Count > 0;
    public bool HasProviderSources => ProviderSourceUrls.Count > 0;
    public bool HasSeasons => Seasons.Count > 0;
    public bool HasSpecialFeatures => SpecialFeatures.Count > 0;
    public bool HasBackdrop => BackdropImage != null;
    public bool HasPoster => PosterImage != null;
    public bool HasLogo => LogoImage != null;
    public string GenresText => string.Join(", ", Genres);
    public string ActorsText => string.Join(", ", Actors);
    public string TagsText => string.Join(", ", Tags);
    public string StudiosText => string.Join(", ", Studios);
    public string ProviderAttributionText => string.Join(
        " · ", ProviderSourceUrls.Keys.Select(id => id.ToUpperInvariant()));
    public Uri? PrimaryProviderSourceUri => Uri.TryCreate(
        ProviderSourceUrls.Values.FirstOrDefault(), UriKind.Absolute, out var uri)
        ? uri
        : null;
    public string RatingText => CommunityRating.HasValue
        ? $"★ {CommunityRating.Value:0.0}"
        : "";
    public string EpisodeCountText => string.Format(
        CultureInfo.CurrentCulture,
        ResourceStringHelper.GetString("VideoLibrarySeriesEpisodeCountFormat", "{0} episodes"),
        EpisodeCount);
    public string YearRangeText
    {
        get
        {
            var years = _episodes.Select(video => video.ReleaseYear).Where(year => year.HasValue).Select(year => year!.Value).ToList();
            if (years.Count == 0)
                return "";
            var first = years.Min();
            var last = EndYear ?? years.Max();
            return first == last ? first.ToString(CultureInfo.CurrentCulture) : $"{first}–{last}";
        }
    }
    public string FactsText => string.Join(" · ", new[] { YearRangeText, EpisodeCountText, Genres.FirstOrDefault() }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public BitmapImage? PosterImage => LoadLocalImage(PosterPath);
    public BitmapImage? BackdropImage => LoadLocalImage(BackdropPath);
    public BitmapImage? LogoImage => LoadLocalImage(LogoPath);

    private static int MetadataWeight(VideoItem video) =>
        (video.HasOverview ? 2 : 0)
        + (video.Genres.Count > 0 ? 1 : 0)
        + (video.Actors.Count > 0 ? 1 : 0)
        + (!string.IsNullOrWhiteSpace(video.SeriesPosterPath) ? 2 : 0)
        + (!string.IsNullOrWhiteSpace(video.SeriesThumbPath) ? 1 : 0);

    private static string? FirstExisting(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static BitmapImage? LoadLocalImage(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new BitmapImage(new Uri(path))
                : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class VideoSeasonViewModel
{
    public VideoSeasonViewModel(int? seasonNumber, int episodeCount, string? posterPath)
    {
        SeasonNumber = seasonNumber;
        EpisodeCount = episodeCount;
        Title = seasonNumber switch
        {
            0 => ResourceStringHelper.GetString("VideoLibrarySpecialsHeading", "Specials"),
            null => ResourceStringHelper.GetString("VideoLibraryAbsoluteOrderHeading", "Absolute order"),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                ResourceStringHelper.GetString("VideoLibrarySeasonTitleFormat", "Season {0}"),
                seasonNumber.Value),
        };
        PosterImage = !string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath)
            ? new BitmapImage(new Uri(posterPath))
            : null;
    }

    public int? SeasonNumber { get; }
    public int EpisodeCount { get; }
    public string Title { get; }
    public BitmapImage? PosterImage { get; }
    public bool HasPoster => PosterImage != null;
    public string EpisodeCountText => string.Format(
        CultureInfo.CurrentCulture,
        ResourceStringHelper.GetString("VideoLibrarySeriesEpisodeCountFormat", "{0} episodes"),
        EpisodeCount);
}

public sealed class VideoPersonViewModel
{
    public VideoPersonViewModel(VideoPersonCredit person)
    {
        Name = person.Name;
        Role = person.Role;
        Type = person.Type;
        Image = LoadLocalImage(person.LocalImagePath);
    }

    public string Name { get; }
    public string? Role { get; }
    public string Type { get; }
    public BitmapImage? Image { get; }
    public bool HasImage => Image != null;

    private static BitmapImage? LoadLocalImage(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new BitmapImage(new Uri(path))
                : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class VideoRelatedItemViewModel
{
    public VideoRelatedItemViewModel(VideoRelatedItem item)
    {
        Title = item.Title;
        OriginalTitle = item.OriginalTitle;
        Year = item.Year;
        Image = LoadLocalImage(item.LocalPosterPath ?? item.LocalBackdropPath);
        SourceUri = Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var uri) ? uri : null;
    }

    public string Title { get; }
    public string? OriginalTitle { get; }
    public int? Year { get; }
    public BitmapImage? Image { get; }
    public bool HasImage => Image != null;
    public Uri? SourceUri { get; }

    private static BitmapImage? LoadLocalImage(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new BitmapImage(new Uri(path))
                : null;
        }
        catch
        {
            return null;
        }
    }
}
