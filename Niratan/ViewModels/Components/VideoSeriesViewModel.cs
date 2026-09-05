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
    private readonly int? _metadataYear;
    private ObservableCollection<VideoItemViewModel> _episodes = new();
    private VideoSeasonViewModel? _selectedSeason;

    public VideoSeriesViewModel(Guid id, IEnumerable<VideoItem> videos)
    {
        Id = id;
        _sourceVideos = videos.ToList();
        // A merged series can contain provider nodes for later cours/seasons.
        // Keep the explicit group id as the root identity, like Shoko's
        // AnimeSeries -> AnimeGroup relationship, instead of letting the newest
        // or richest child season replace the series title and provider id.
        var rootVideos = _sourceVideos
            .Where(video => video.CatalogSeriesNodeId == id)
            .ToList();
        var metadataVideos = rootVideos.Count > 0 ? rootVideos : _sourceVideos;
        var representative = metadataVideos
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
        var identitySource = metadataVideos
            .OrderByDescending(video => video.ExternalIds.Count)
            .ThenByDescending(video => video.MatchCandidates.Count)
            .ThenByDescending(MetadataWeight)
            .FirstOrDefault() ?? representative;
        MetadataIdentity = BuildMetadataIdentity(identitySource);
        var metadataYears = metadataVideos
            .Select(video => video.CatalogSeriesReleaseYear ?? video.ReleaseYear)
            .Where(year => year.HasValue)
            .Select(year => year!.Value)
            .ToList();
        _metadataYear = metadataYears.Count > 0 ? metadataYears.Min() : null;
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
            .Select(group => new VideoSeasonViewModel(
                group.Key,
                group,
                ResolveSeasonPosterPath(
                    group,
                    remotePosterPath: null,
                    seriesPosterPath: PosterPath))));
        FirstEpisode = RegularEpisodes.FirstOrDefault();
        ContinueEpisode = RegularEpisodes
            .Where(item => item.Video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds
                           && !item.Video.IsWatched)
            .OrderByDescending(item => item.Video.LastOpenedAt ?? item.Video.ImportedAt)
            .FirstOrDefault();
        PrimaryPlayItem = ContinueEpisode ?? FirstEpisode;

        var catalogSeasons = MergeRemoteSeasons(
            _sourceVideos.SelectMany(video => video.CatalogSeriesSeasons));
        if (catalogSeasons.Count > 0)
            ApplyRemoteSeasons(catalogSeasons);
        else if (Seasons.Count > 0)
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
    public bool HasRemoteSeasonMetadata => Seasons.Any(season => season.RemoteSeason != null);
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
    public int? MetadataYear => _metadataYear;

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
        var remoteByNumber = MergeRemoteSeasons(remoteSeasons)
            .ToDictionary(season => season.SeasonNumber);
        var localByNumber = RegularEpisodes
            .Where(item => item.Video.SeasonNumber.HasValue)
            .GroupBy(item => item.Video.SeasonNumber!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (remoteByNumber.ContainsKey(0) && SpecialFeatures.Count > 0)
            localByNumber[0] = SpecialFeatures.ToList();
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
                ResolveSeasonPosterPath(
                    localEpisodes ?? [],
                    remoteSeason?.LocalPosterPath,
                    PosterPath),
                remoteSeason);
        }));
        OnPropertyChanged(nameof(Seasons));
        OnPropertyChanged(nameof(HasSeasons));
        OnPropertyChanged(nameof(HasRemoteSeasonMetadata));

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
            .ThenBy(candidate => MetadataIdentityPriority(candidate.ProviderId))
            .FirstOrDefault(candidate => candidate.Score > 0.25
                && (candidate.ProviderId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
                    || candidate.ProviderId.Equals("tvmaze", StringComparison.OrdinalIgnoreCase)
                    || candidate.ProviderId.Equals("anidb", StringComparison.OrdinalIgnoreCase)));
        var providerId = video.ProviderId ?? video.ExternalIds.Keys
            .Where(key => MetadataIdentityPriority(key) < int.MaxValue)
            .OrderBy(MetadataIdentityPriority)
            .FirstOrDefault();
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

    private static int MetadataIdentityPriority(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "anidb" => 0,
            "tmdb" => 1,
            "tvmaze" => 2,
            _ => int.MaxValue,
        };

    private static IReadOnlyList<VideoDiscoverySeason> MergeRemoteSeasons(
        IEnumerable<VideoDiscoverySeason> seasons) => seasons
        .GroupBy(season => season.SeasonNumber)
        .Select(group =>
        {
            var candidates = group.ToList();
            var primary = candidates
                .OrderByDescending(season => !string.IsNullOrWhiteSpace(season.LocalPosterPath))
                .ThenByDescending(season => !string.IsNullOrWhiteSpace(season.PosterPath))
                .ThenByDescending(season => season.Episodes.IsDefaultOrEmpty ? 0 : season.Episodes.Length)
                .ThenByDescending(season => !string.IsNullOrWhiteSpace(season.Title))
                .ThenByDescending(season => !string.IsNullOrWhiteSpace(season.Overview))
                .ThenByDescending(season => season.EpisodeCount)
                .ThenBy(season => season.Title, StringComparer.CurrentCultureIgnoreCase)
                .First();
            var episodes = candidates
                .SelectMany(season => season.Episodes.IsDefault
                    ? ImmutableArray<VideoDiscoveryEpisode>.Empty
                    : season.Episodes)
                .GroupBy(RemoteEpisodeIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(episodeGroup => episodeGroup
                    .OrderByDescending(RemoteEpisodeMetadataWeight)
                    .ThenBy(episode => episode.SourceUrl, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(episode => episode.DisplayNumber, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(episode => episode.Title, StringComparer.CurrentCultureIgnoreCase)
                    .First())
                .OrderBy(episode => SpecialEpisodeTypeOrder(episode.DisplayNumber))
                .ThenBy(episode => episode.EpisodeNumber)
                .ThenBy(episode => episode.SourceUrl, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            return primary with
            {
                EpisodeCount = Math.Max(
                    primary.EpisodeCount,
                    group.Key == 0
                        ? episodes.Length
                        : episodes.Select(episode => episode.EpisodeNumber).Distinct().Count()),
                Episodes = episodes,
            };
        })
        .OrderBy(season => season.SeasonNumber)
        .ToList();

    private static string RemoteEpisodeIdentityKey(VideoDiscoveryEpisode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.SourceUrl))
            return $"url:{episode.SourceUrl.Trim().TrimEnd('/')}";
        return string.Join(
            "\u001f",
            episode.DisplayNumber ?? episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture),
            episode.Title,
            episode.OriginalTitle,
            episode.AirDate);
    }

    private static int RemoteEpisodeMetadataWeight(VideoDiscoveryEpisode episode) =>
        (string.IsNullOrWhiteSpace(episode.Title) ? 0 : 4)
        + (!string.IsNullOrWhiteSpace(episode.OriginalTitle) ? 2 : 0)
        + (!string.IsNullOrWhiteSpace(episode.Overview) ? 4 : 0)
        + (!string.IsNullOrWhiteSpace(episode.AirDate) ? 2 : 0)
        + (episode.RuntimeMinutes is > 0 ? 1 : 0)
        + (!string.IsNullOrWhiteSpace(episode.LocalThumbnailPath) ? 3 : 0)
        + (!string.IsNullOrWhiteSpace(episode.ThumbnailPath) ? 1 : 0);

    private static int SpecialEpisodeTypeOrder(string? displayNumber) =>
        string.IsNullOrWhiteSpace(displayNumber)
            ? int.MaxValue
            : char.ToUpperInvariant(displayNumber[0]) switch
            {
                'S' => 0,
                'C' => 1,
                'T' => 2,
                'P' => 3,
                'O' => 4,
                _ => int.MaxValue,
            };

    private static string? FirstExisting(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

    private static string? ResolveSeasonPosterPath(
        IEnumerable<VideoItemViewModel> episodes,
        string? remotePosterPath,
        string? seriesPosterPath) =>
        FirstExisting(episodes.Select(item => item.Video.SeriesPosterPath))
        ?? FirstExisting([remotePosterPath, seriesPosterPath]);

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
            BuildEpisodeSlots(seasonNumber, Episodes, remoteSeason));
        EpisodeCount = remoteSeason?.EpisodeCount > 0
            ? Math.Max(remoteSeason.EpisodeCount, EpisodeSlots.Count)
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
        PosterPath = !string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath)
            ? posterPath
            : null;
        PosterImage = LoadLocalImage(PosterPath);
    }

    public int? SeasonNumber { get; }
    public IReadOnlyList<VideoItemViewModel> Episodes { get; }
    public ObservableCollection<VideoEpisodeSlotViewModel> EpisodeSlots { get; }
    public VideoDiscoverySeason? RemoteSeason { get; }
    public int EpisodeCount { get; }
    public string Title { get; }
    public string? PosterPath { get; }
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
            // BitmapImage needs a live WinUI apartment. Preserve PosterPath for
            // later UI materialization and keep headless/catalog rebuilds safe.
            return null;
        }
    }

    private static IEnumerable<VideoEpisodeSlotViewModel> BuildEpisodeSlots(
        int? seasonNumber,
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
        var resolvedSeasonNumber = remoteSeason?.SeasonNumber
                                   ?? seasonNumber
                                   ?? localEpisodes.Select(item => item.Video.SeasonNumber)
                                       .FirstOrDefault(number => number.HasValue)
                                   ?? 0;
        if (resolvedSeasonNumber == 0)
            return BuildSpecialEpisodeSlots(remoteEpisodes, localEpisodes);

        var numbers = remoteEpisodes.Select(episode => episode.EpisodeNumber)
            .Concat(localByNumber.Keys)
            .Distinct()
            .OrderBy(number => number);
        var remoteByNumber = remoteEpisodes
            .GroupBy(episode => episode.EpisodeNumber)
            .ToDictionary(
                group => group.Key,
                group => SelectRegularRemoteEpisode(
                    group,
                    localByNumber.GetValueOrDefault(group.Key)));
        var slots = new List<VideoEpisodeSlotViewModel>();
        foreach (var number in numbers)
        {
            localByNumber.TryGetValue(number, out var local);
            remoteByNumber.TryGetValue(number, out var remote);
            slots.Add(new VideoEpisodeSlotViewModel(
                resolvedSeasonNumber,
                number,
                remote,
                local));
        }

        slots.AddRange(localEpisodes
            .Where(item => !item.Video.EpisodeNumber.HasValue)
            .Select(item => new VideoEpisodeSlotViewModel(
                resolvedSeasonNumber,
                item.Video.AbsoluteEpisodeNumber ?? slots.Count + 1,
                null,
                item)));
        return slots;
    }

    private static IEnumerable<VideoEpisodeSlotViewModel> BuildSpecialEpisodeSlots(
        IEnumerable<VideoDiscoveryEpisode> remoteEpisodes,
        IReadOnlyList<VideoItemViewModel> localEpisodes)
    {
        var remainingLocal = localEpisodes.ToList();
        var slots = new List<VideoEpisodeSlotViewModel>();
        foreach (var remote in remoteEpisodes
                     .OrderBy(episode => SpecialEpisodeTypeOrder(episode.DisplayNumber))
                     .ThenBy(episode => episode.EpisodeNumber)
                     .ThenBy(episode => episode.DisplayNumber, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(episode => episode.SourceUrl, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(episode => episode.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var local = FindSpecialLocalMatch(remote, remainingLocal);
            if (local != null)
                remainingLocal.Remove(local);
            slots.Add(new VideoEpisodeSlotViewModel(0, remote.EpisodeNumber, remote, local));
        }

        slots.AddRange(remainingLocal
            .OrderBy(item => item.Video.EpisodeNumber ?? int.MaxValue)
            .ThenBy(item => item.Video.AbsoluteEpisodeNumber ?? int.MaxValue)
            .ThenBy(item => item.Video.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new VideoEpisodeSlotViewModel(
                0,
                item.Video.EpisodeNumber ?? item.Video.AbsoluteEpisodeNumber ?? slots.Count + index + 1,
                null,
                item)));
        return slots;
    }

    private static VideoItemViewModel? FindSpecialLocalMatch(
        VideoDiscoveryEpisode remote,
        IReadOnlyList<VideoItemViewModel> localEpisodes) =>
        localEpisodes.FirstOrDefault(local => RemoteIdentityMatchesLocal(remote, local))
        ?? localEpisodes.FirstOrDefault(local =>
            local.Video.EpisodeNumber == remote.EpisodeNumber
            && (string.Equals(local.Video.Title, remote.Title, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(
                    local.Video.OriginalTitle,
                    remote.OriginalTitle,
                    StringComparison.CurrentCultureIgnoreCase)));

    private static VideoDiscoveryEpisode SelectRegularRemoteEpisode(
        IEnumerable<VideoDiscoveryEpisode> candidates,
        VideoItemViewModel? local) =>
        candidates
            .OrderByDescending(candidate => local != null && RemoteIdentityMatchesLocal(candidate, local))
            .ThenByDescending(RemoteEpisodeMetadataWeight)
            .ThenBy(candidate => candidate.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.OriginalTitle, StringComparer.CurrentCultureIgnoreCase)
            .First();

    private static bool RemoteIdentityMatchesLocal(
        VideoDiscoveryEpisode remote,
        VideoItemViewModel local)
    {
        if (!local.Video.ExternalIds.TryGetValue("anidb-episode", out var localEpisodeId)
            || string.IsNullOrWhiteSpace(localEpisodeId)
            || string.IsNullOrWhiteSpace(remote.SourceUrl))
        {
            return false;
        }

        var separator = remote.SourceUrl.LastIndexOf('/');
        var remoteEpisodeId = separator >= 0
            ? remote.SourceUrl[(separator + 1)..]
            : remote.SourceUrl;
        return string.Equals(
            remoteEpisodeId.TrimEnd('/'),
            localEpisodeId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int RemoteEpisodeMetadataWeight(VideoDiscoveryEpisode episode)
    {
        var genericTitle = string.IsNullOrWhiteSpace(episode.Title)
                           || string.Equals(
                               episode.Title,
                               $"Episode {episode.EpisodeNumber}",
                               StringComparison.OrdinalIgnoreCase);
        return (genericTitle ? 0 : 4)
               + (!string.IsNullOrWhiteSpace(episode.OriginalTitle) ? 2 : 0)
               + (!string.IsNullOrWhiteSpace(episode.Overview) ? 4 : 0)
               + (!string.IsNullOrWhiteSpace(episode.AirDate) ? 2 : 0)
               + (episode.RuntimeMinutes is > 0 ? 1 : 0)
               + (!string.IsNullOrWhiteSpace(episode.LocalThumbnailPath) ? 3 : 0)
               + (!string.IsNullOrWhiteSpace(episode.ThumbnailPath) ? 1 : 0)
               + (!string.IsNullOrWhiteSpace(episode.SourceUrl) ? 1 : 0);
    }

    private static int SpecialEpisodeTypeOrder(string? displayNumber) =>
        string.IsNullOrWhiteSpace(displayNumber)
            ? int.MaxValue
            : char.ToUpperInvariant(displayNumber[0]) switch
            {
                'S' => 0,
                'C' => 1,
                'T' => 2,
                'P' => 3,
                'O' => 4,
                _ => int.MaxValue,
            };
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
    public string NumberText => $"{(string.IsNullOrWhiteSpace(RemoteEpisode?.DisplayNumber)
        ? EpisodeNumber.ToString(CultureInfo.CurrentCulture)
        : RemoteEpisode.DisplayNumber)}.";
    public string Title { get; }
    public string Overview { get; }
    public string AirDate { get; }
    public VideoDiscoveryEpisode? RemoteEpisode { get; }
    public VideoItemViewModel? DownloadedEpisode { get; }
    public BitmapImage? ArtworkImage { get; }
    public bool HasArtwork => ArtworkImage is not null;
    public bool IsDownloaded => DownloadedEpisode?.Video.IsAvailable == true
                                && !DownloadedEpisode.IsMissing;
    public bool IsSupplemental => SeasonNumber == 0
                                  && RemoteEpisode?.DisplayNumber is { Length: > 0 } displayNumber
                                  && char.ToUpperInvariant(displayNumber[0]) is 'C' or 'T' or 'P' or 'O';
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

    public bool CanDownload => !IsSupplemental && !IsDownloaded && !IsDownloading && !IsQueued;
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
