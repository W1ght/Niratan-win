using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;

namespace Niratan.ViewModels.Components;

public sealed class VideoSeriesViewModel : ObservableObject
{
    private readonly IReadOnlyList<VideoItem> _sourceVideos;
    private ObservableCollection<VideoItemViewModel> _episodes = new();
    private VideoSeasonViewModel? _selectedSeason;

    public VideoSeriesViewModel(Guid id, IEnumerable<VideoItem> videos)
    {
        Id = id;
        _sourceVideos = videos.ToList();
        var representative = _sourceVideos
            .OrderByDescending(MetadataWeight)
            .ThenByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
            .First();
        var hasSeriesOwner = representative.CatalogSeriesNodeId.HasValue;
        Title = representative.CatalogSeriesTitle ?? representative.Title;
        OriginalTitle = hasSeriesOwner
            ? representative.CatalogSeriesOriginalTitle
            : representative.OriginalTitle;
        Overview = hasSeriesOwner
            ? representative.CatalogSeriesOverview
            : representative.Overview;
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
        PosterPath = FirstExisting(_sourceVideos.Select(video => video.SeriesPosterPath ?? video.PosterPath));
        BackdropPath = FirstExisting(_sourceVideos.Select(video => video.SeriesThumbPath ?? video.BackdropPath));
        LogoPath = FirstExisting(_sourceVideos.Select(video => video.LogoPath));
        var identitySource = _sourceVideos
            .OrderByDescending(video => video.ExternalIds.Count)
            .ThenByDescending(video => video.MatchCandidates.Count)
            .ThenByDescending(MetadataWeight)
            .FirstOrDefault() ?? representative;
        MetadataIdentity = BuildMetadataIdentity(identitySource);
        MetadataMediaKind = representative.LibraryMediaType == VideoLibraryMediaType.Anime
            ? VideoMetadataMediaKind.Anime
            : VideoMetadataMediaKind.Series;
        MetadataSearchTitles = new[] { Title, OriginalTitle }
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();

        RegularEpisodes = new ObservableCollection<VideoItemViewModel>(CollapseLogicalEntries(
                _sourceVideos.Where(video => !IsSpecialEntry(video)))
            .OrderBy(video => video.SeasonNumber ?? int.MaxValue)
            .ThenBy(video => video.EpisodeNumber ?? video.AbsoluteEpisodeNumber ?? int.MaxValue)
            .ThenBy(video => video.AbsoluteEpisodeNumber ?? int.MaxValue)
            .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(video => new VideoItemViewModel(video)));
        SpecialFeatures = new ObservableCollection<VideoItemViewModel>(CollapseLogicalEntries(
                _sourceVideos.Where(IsSpecialEntry))
            .OrderBy(video => video.EpisodeNumber ?? video.AbsoluteEpisodeNumber ?? int.MaxValue)
            .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(video => new VideoItemViewModel(video)));
        Seasons = new ObservableCollection<VideoSeasonViewModel>(RegularEpisodes
            .GroupBy(item => item.Video.SeasonNumber)
            .OrderBy(group => group.Key ?? int.MaxValue)
            .Select(group => new VideoSeasonViewModel(group.Key, group, PosterPath)));
        FirstEpisode = RegularEpisodes.FirstOrDefault();
        ContinueEpisode = RegularEpisodes
            .Where(item => item.Video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds
                           && !item.Video.IsWatched)
            .OrderByDescending(item => item.Video.LastOpenedAt ?? item.Video.ImportedAt)
            .FirstOrDefault();
        PrimaryPlayItem = ContinueEpisode ?? FirstEpisode;

        if (Seasons.Count > 0)
            SelectSeason(Seasons[0]);
        else
            Episodes = new ObservableCollection<VideoItemViewModel>(RegularEpisodes);
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
    public ObservableCollection<VideoItemViewModel> RegularEpisodes { get; }
    public ObservableCollection<VideoItemViewModel> Episodes
    {
        get => _episodes;
        private set => SetProperty(ref _episodes, value);
    }
    public ObservableCollection<VideoItemViewModel> SpecialFeatures { get; }
    public ObservableCollection<VideoSeasonViewModel> Seasons { get; private set; }
    public VideoSeasonViewModel? SelectedSeason
    {
        get => _selectedSeason;
        private set => SetProperty(ref _selectedSeason, value);
    }
    public ObservableCollection<VideoEpisodeSlotViewModel> SelectedEpisodeSlots =>
        SelectedSeason?.EpisodeSlots ?? [];
    public VideoItemViewModel? FirstEpisode { get; }
    public VideoItemViewModel? ContinueEpisode { get; }
    public VideoItemViewModel? PrimaryPlayItem { get; }
    public int EpisodeCount => RegularEpisodes.Count;
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
    public bool HasSeasons => Seasons.Any(season => season.SeasonNumber.HasValue);
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
            var years = _sourceVideos
                .Select(video => video.CatalogSeriesReleaseYear)
                .Where(year => year.HasValue)
                .Select(year => year!.Value)
                .Distinct()
                .ToList();
            if (years.Count == 0 && !_sourceVideos.Any(video => video.CatalogSeriesNodeId.HasValue))
            {
                years = _sourceVideos
                    .Select(video => video.ReleaseYear)
                    .Where(year => year.HasValue)
                    .Select(year => year!.Value)
                    .ToList();
            }
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
    public VideoMetadataCandidate? MetadataIdentity { get; private set; }
    public VideoMetadataMediaKind MetadataMediaKind { get; }
    public IReadOnlyList<string> MetadataSearchTitles { get; }
    public int? MetadataYear => _sourceVideos
        .Select(video => video.CatalogSeriesReleaseYear ?? video.ReleaseYear)
        .FirstOrDefault(year => year.HasValue);

    public void ApplyRemoteMetadata(VideoMetadataDetails metadata)
    {
        var aliases = (metadata.Aliases.IsDefault ? [] : metadata.Aliases)
            .Concat(MetadataSearchTitles)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        MetadataIdentity = new VideoMetadataCandidate(
            metadata.ProviderId,
            metadata.ProviderItemId,
            metadata.MediaKind,
            metadata.Title,
            metadata.OriginalTitle,
            metadata.Year,
            null,
            null,
            null,
            aliases,
            metadata.ExternalIds,
            metadata.SourceUrl);
        OnPropertyChanged(nameof(MetadataIdentity));
    }

    public void SelectSeason(int? seasonNumber)
    {
        var season = Seasons.FirstOrDefault(candidate => candidate.SeasonNumber == seasonNumber);
        if (season != null)
            SelectSeason(season);
    }

    public void ApplyRemoteSeasons(IEnumerable<VideoDiscoverySeason> remoteSeasons)
    {
        var remoteByNumber = remoteSeasons
            .GroupBy(season => season.SeasonNumber)
            .ToDictionary(group => group.Key, group => group.First());
        var localByNumber = RegularEpisodes
            .Where(item => item.Video.SeasonNumber.HasValue)
            .GroupBy(item => item.Video.SeasonNumber!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var seasonNumbers = remoteByNumber.Keys
            .Concat(localByNumber.Keys)
            .Distinct()
            .OrderBy(number => number)
            .ToList();
        if (seasonNumbers.Count == 0)
            return;

        var selectedNumber = SelectedSeason?.SeasonNumber;
        Seasons = new ObservableCollection<VideoSeasonViewModel>(seasonNumbers.Select(number =>
        {
            localByNumber.TryGetValue(number, out var localEpisodes);
            remoteByNumber.TryGetValue(number, out var remoteSeason);
            return new VideoSeasonViewModel(
                number,
                localEpisodes ?? [],
                PosterPath,
                remoteSeason);
        }));
        OnPropertyChanged(nameof(Seasons));
        OnPropertyChanged(nameof(HasSeasons));

        var selected = Seasons.FirstOrDefault(season => season.SeasonNumber == selectedNumber)
                       ?? Seasons.FirstOrDefault();
        if (selected is not null)
            SelectSeason(selected);
    }

    public bool ContainsRegularEpisode(VideoItem video) => RegularEpisodes.Any(item =>
        string.Equals(item.Video.Id, video.Id, StringComparison.OrdinalIgnoreCase));

    public bool ContainsSpecialFeature(VideoItem video) => SpecialFeatures.Any(item =>
        string.Equals(item.Video.Id, video.Id, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSpecialEntry(VideoItem video) =>
        video.IsSpecialEpisode || video.SeasonNumber == 0;

    private void SelectSeason(VideoSeasonViewModel season)
    {
        foreach (var candidate in Seasons)
            candidate.IsSelected = false;
        season.IsSelected = true;
        SelectedSeason = season;
        Episodes = new ObservableCollection<VideoItemViewModel>(season.Episodes);
        OnPropertyChanged(nameof(SelectedEpisodeSlots));
    }

    internal static IEnumerable<VideoItem> CollapseLogicalEntries(IEnumerable<VideoItem> videos) =>
        videos
            .GroupBy(
                video => video.CatalogNodeId.HasValue
                    ? $"node:{video.CatalogNodeId.Value:D}"
                    : $"asset:{video.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(video => video.IsAvailable)
                .ThenByDescending(video => video.IsWatched)
                .ThenByDescending(video =>
                    video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds)
                .ThenByDescending(video => video.LastOpenedAt ?? DateTime.MinValue)
                .ThenByDescending(video => video.ImportedAt)
                .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase)
                .First());

    private static int MetadataWeight(VideoItem video) =>
        (video.HasOverview ? 2 : 0)
        + (video.Genres.Count > 0 ? 1 : 0)
        + (video.Actors.Count > 0 ? 1 : 0)
        + (!string.IsNullOrWhiteSpace(video.SeriesPosterPath) ? 2 : 0)
        + (!string.IsNullOrWhiteSpace(video.SeriesThumbPath) ? 1 : 0);

    private static VideoMetadataCandidate? BuildMetadataIdentity(VideoItem video)
    {
        var reviewCandidate = video.MatchCandidates
            .Where(candidate => !candidate.HasHardConflict)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault(candidate => candidate.Score > 0.25
                && (candidate.ProviderId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                    || candidate.ProviderId.Equals("tvmaze", StringComparison.OrdinalIgnoreCase)
                    || candidate.ProviderId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
                    || candidate.ProviderId.Equals("bangumi", StringComparison.OrdinalIgnoreCase)));
        var providerId = video.ProviderId
                         ?? video.ExternalIds.Keys.FirstOrDefault(key =>
                             key.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                             || key.Equals("tvmaze", StringComparison.OrdinalIgnoreCase)
                             || key.Equals("anilist", StringComparison.OrdinalIgnoreCase)
                             || key.Equals("bangumi", StringComparison.OrdinalIgnoreCase));
        var providerItemId = string.IsNullOrWhiteSpace(providerId)
            ? null
            : video.ExternalIds.FirstOrDefault(pair =>
                pair.Key.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(providerItemId))
        {
            providerId = reviewCandidate?.ProviderId;
            providerItemId = reviewCandidate?.ProviderItemId;
        }
        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(providerItemId))
        {
            return null;
        }

        var mediaKind = video.LibraryMediaType == VideoLibraryMediaType.Anime
            ? VideoMetadataMediaKind.Anime
            : VideoMetadataMediaKind.Series;
        var title = video.CatalogSeriesTitle ?? video.Title;
        var aliases = new[] { title, video.CatalogSeriesOriginalTitle, video.OriginalTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Concat(reviewCandidate is null ? [] : [reviewCandidate.Title])
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        return new VideoMetadataCandidate(
            providerId,
            providerItemId,
            mediaKind,
            title,
            video.CatalogSeriesOriginalTitle ?? video.OriginalTitle,
            video.CatalogSeriesReleaseYear ?? video.ReleaseYear,
            null,
            null,
            null,
            aliases,
            video.ExternalIds.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            video.ProviderSourceUrls.FirstOrDefault(pair =>
                pair.Key.Equals(providerId, StringComparison.OrdinalIgnoreCase)).Value
                is { Length: > 0 } sourceUrl
                ? sourceUrl
                : null);
    }

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

public sealed class VideoSeasonViewModel : ObservableObject
{
    private bool _isSelected;

    public VideoSeasonViewModel(
        int? seasonNumber,
        IEnumerable<VideoItemViewModel> episodes,
        string? posterPath,
        VideoDiscoverySeason? remoteSeason = null)
    {
        SeasonNumber = seasonNumber;
        Episodes = episodes.ToList();
        RemoteSeason = remoteSeason;
        EpisodeSlots = new ObservableCollection<VideoEpisodeSlotViewModel>(
            BuildEpisodeSlots(Episodes, remoteSeason));
        EpisodeCount = remoteSeason?.EpisodeCount > 0
            ? remoteSeason.EpisodeCount
            : EpisodeSlots.Count;
        Title = remoteSeason?.Title ?? seasonNumber switch
        {
            0 => ResourceStringHelper.GetString("VideoLibrarySpecialsHeading", "Specials"),
            null => ResourceStringHelper.GetString("VideoLibraryAbsoluteOrderHeading", "Absolute order"),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                ResourceStringHelper.GetString("VideoLibrarySeasonTitleFormat", "Season {0}"),
                seasonNumber.Value),
        };
        var seasonPosterPath = remoteSeason?.LocalPosterPath ?? posterPath;
        PosterImage = !string.IsNullOrWhiteSpace(seasonPosterPath) && File.Exists(seasonPosterPath)
            ? new BitmapImage(new Uri(seasonPosterPath))
            : null;
    }

    public int? SeasonNumber { get; }
    public IReadOnlyList<VideoItemViewModel> Episodes { get; }
    public ObservableCollection<VideoEpisodeSlotViewModel> EpisodeSlots { get; }
    public VideoDiscoverySeason? RemoteSeason { get; }
    public int EpisodeCount { get; }
    public string Title { get; }
    public BitmapImage? PosterImage { get; }
    public bool HasPoster => PosterImage != null;
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
    public string AutomationId => SeasonNumber.HasValue
        ? $"VideoLibrarySeason_{SeasonNumber.Value}"
        : "VideoLibrarySeason_Absolute";
    public string EpisodeCountText => string.Format(
        CultureInfo.CurrentCulture,
        ResourceStringHelper.GetString("VideoLibrarySeriesEpisodeCountFormat", "{0} episodes"),
        EpisodeCount);

    private static IEnumerable<VideoEpisodeSlotViewModel> BuildEpisodeSlots(
        IReadOnlyList<VideoItemViewModel> localEpisodes,
        VideoDiscoverySeason? remoteSeason)
    {
        var localByNumber = localEpisodes
            .Where(item => item.Video.EpisodeNumber.HasValue)
            .GroupBy(item => item.Video.EpisodeNumber!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var remoteEpisodes = remoteSeason?.Episodes.IsDefaultOrEmpty == false
            ? remoteSeason.Episodes
            : Enumerable.Range(1, Math.Max(0, remoteSeason?.EpisodeCount ?? 0))
                .Select(number => new VideoDiscoveryEpisode(
                    number,
                    $"Episode {number}",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null))
                .ToImmutableArray();
        var numbers = remoteEpisodes.Select(episode => episode.EpisodeNumber)
            .Concat(localByNumber.Keys)
            .Distinct()
            .OrderBy(number => number);
        var remoteByNumber = remoteEpisodes.ToDictionary(episode => episode.EpisodeNumber);
        var slots = new List<VideoEpisodeSlotViewModel>();
        foreach (var number in numbers)
        {
            localByNumber.TryGetValue(number, out var local);
            remoteByNumber.TryGetValue(number, out var remote);
            slots.Add(new VideoEpisodeSlotViewModel(
                remoteSeason?.SeasonNumber ?? 0,
                number,
                remote,
                local));
        }

        slots.AddRange(localEpisodes
            .Where(item => !item.Video.EpisodeNumber.HasValue)
            .Select(item => new VideoEpisodeSlotViewModel(
                remoteSeason?.SeasonNumber ?? item.Video.SeasonNumber ?? 0,
                item.Video.AbsoluteEpisodeNumber ?? slots.Count + 1,
                null,
                item)));
        return slots;
    }
}

public sealed partial class VideoEpisodeSlotViewModel : ObservableObject
{
    public VideoEpisodeSlotViewModel(
        int seasonNumber,
        int episodeNumber,
        VideoDiscoveryEpisode? remoteEpisode,
        VideoItemViewModel? downloadedEpisode)
    {
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        RemoteEpisode = remoteEpisode;
        DownloadedEpisode = downloadedEpisode;
        Title = remoteEpisode?.Title
                ?? downloadedEpisode?.Video.Title
                ?? $"Episode {episodeNumber}";
        Overview = remoteEpisode?.Overview ?? "";
        AirDate = remoteEpisode?.AirDate ?? "";
        var imagePath = remoteEpisode?.LocalThumbnailPath;
        if (string.IsNullOrWhiteSpace(imagePath))
            ArtworkImage = downloadedEpisode?.LandscapeArtworkImage;
        else
        {
            try { ArtworkImage = new BitmapImage(new Uri(imagePath)); }
            catch { }
        }
    }

    public int EpisodeNumber { get; }
    public int SeasonNumber { get; }
    public string NumberText => $"{EpisodeNumber}.";
    public string Title { get; }
    public string Overview { get; }
    public string AirDate { get; }
    public VideoDiscoveryEpisode? RemoteEpisode { get; }
    public VideoItemViewModel? DownloadedEpisode { get; }
    public BitmapImage? ArtworkImage { get; }
    public bool HasArtwork => ArtworkImage is not null;
    public bool IsDownloaded => DownloadedEpisode?.Video.IsAvailable == true
                                && !DownloadedEpisode.IsMissing;
    public bool HasOverview => !string.IsNullOrWhiteSpace(Overview);
    public bool HasAirDate => !string.IsNullOrWhiteSpace(AirDate);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsQueued { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string DownloadStatus { get; set; } = "";

    public bool CanDownload => !IsDownloaded && !IsDownloading && !IsQueued;
    public string StatusText => IsDownloaded
        ? DownloadedEpisode!.WatchStatusText
        : !string.IsNullOrWhiteSpace(DownloadStatus)
            ? DownloadStatus
            : ResourceStringHelper.GetString("VideoEpisodeNotDownloaded", "Not downloaded");
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
