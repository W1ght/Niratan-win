using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Nyaa;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.Views.Pages;

namespace Niratan.ViewModels.Pages;

public partial class DiscoverPageViewModel : ObservableObject, IDisposable
{
    private readonly IVideoDiscoveryService _discovery;
    private readonly IVideoResourceSearchService _resources;
    private readonly Lazy<INyaaDownloadManager> _nyaaDownloadManager;
    private readonly IQbittorrentCredentialStore _credentials;
    private readonly IQbittorrentDownloadCoordinator _downloads;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private CancellationTokenSource _cts = new();
    private bool _recommendationsLoaded;
    private bool _disposed;
    private bool _loadingMore;
    private bool _isRecommendationsTab;
    private CancellationTokenSource? _detailsCts;
    private Task<INyaaDownloadManager>? _nyaaDownloadManagerTask;
    private int _explorePage = 1;
    private int? _exploreTotalPages;

    [ObservableProperty]
    public partial bool IsExploreVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRecommendationsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsDetailsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingRecommendations { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingDetails { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial VideoDiscoveryProviderOption? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial VideoDiscoveryFeed? SelectedExploreFeed { get; set; }

    [ObservableProperty]
    public partial VideoDiscoveryMediaKindOption? SelectedMediaKind { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearchMode { get; set; }

    [ObservableProperty]
    public partial string YearText { get; set; } = "";

    [ObservableProperty]
    public partial string GenreId { get; set; } = "";

    [ObservableProperty]
    public partial VideoDiscoverySortOption? SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial string ResourceQuery { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourceSearchHeading))]
    [NotifyPropertyChangedFor(nameof(ResourceSearchButtonText))]
    public partial bool IsSubtitleSearch { get; set; }

    [ObservableProperty]
    public partial NyaaSearchCategory SelectedResourceCategory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedDetailsSubscribed))]
    [NotifyPropertyChangedFor(nameof(SubscriptionButtonText))]
    public partial VideoDiscoveryDetailsViewModel? SelectedDetails { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<VideoDiscoveryCardViewModel> ExploreItems { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<VideoDiscoverySectionViewModel> RecommendationSections { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NyaaTorrentItemViewModel> ResourceResults { get; set; } = [];

    public ObservableCollection<VideoDiscoveryProviderOption> Providers { get; } = [];
    public ObservableCollection<VideoDiscoveryFeed> ExploreFeeds { get; } = [];
    public ObservableCollection<VideoDiscoveryMediaKindOption> MediaKinds { get; } = [];
    public IReadOnlyList<VideoDiscoverySortOption> SortOptions { get; } =
    [
        new("popularity.desc", ResourceStringHelper.GetString("DiscoverSortPopularity", "Popularity")),
        new("vote_average.desc", ResourceStringHelper.GetString("DiscoverSortRating", "Rating")),
        new("primary_release_date.desc", ResourceStringHelper.GetString("DiscoverSortMovieRelease", "Movie release date")),
        new("first_air_date.desc", ResourceStringHelper.GetString("DiscoverSortSeriesAir", "Series first air date")),
        new("revenue.desc", ResourceStringHelper.GetString("DiscoverSortRevenue", "Revenue")),
    ];
    public IReadOnlyList<NyaaSearchCategory> ResourceCategories { get; } =
    [
        new("0_0", ResourceStringHelper.GetString("NyaaCategoryAll", "All categories")),
        new("1_0", ResourceStringHelper.GetString("NyaaCategoryAnime", "Anime")),
        new("4_0", ResourceStringHelper.GetString("NyaaCategoryLiveAction", "Live action")),
    ];
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsSelectedDetailsSubscribed =>
        SelectedDetails is not null && IsSubscribed(SelectedDetails.Identity);
    public string SubscriptionButtonText => IsSelectedDetailsSubscribed
        ? ResourceStringHelper.GetString("DiscoverUnsubscribeButton", "Unsubscribe")
        : ResourceStringHelper.GetString("DiscoverSubscribeButton", "Subscribe");
    public string ResourceSearchHeading => IsSubtitleSearch
        ? ResourceStringHelper.GetString("DiscoverSubtitleHeading", "Search subtitles")
        : ResourceStringHelper.GetString("DiscoverResourceHeading", "Search Nyaa resources");
    public string ResourceSearchButtonText => IsSubtitleSearch
        ? ResourceStringHelper.GetString("DiscoverSearchSubtitlesButton", "Search subtitles")
        : ResourceStringHelper.GetString("DiscoverSearchNyaaButton", "Search Nyaa");
    public string SearchResourcesButtonText =>
        ResourceStringHelper.GetString("DiscoverSearchResourcesButton", "Search resources");
    public string SearchSubtitlesButtonText =>
        ResourceStringHelper.GetString("DiscoverSearchSubtitlesButton", "Search subtitles");
    public bool HasMoreExplorePages =>
        SelectedExploreFeed?.SupportsPaging == true
        && ExploreItems.Count > 0
        && (_exploreTotalPages is null || _explorePage < _exploreTotalPages);

    public DiscoverPageViewModel(
        IVideoDiscoveryService discovery,
        IVideoResourceSearchService resources,
        Lazy<INyaaDownloadManager> nyaaDownloadManager,
        IQbittorrentCredentialStore credentials,
        IQbittorrentDownloadCoordinator downloads,
        ISettingsService settings,
        INavigationService navigation)
    {
        _discovery = discovery;
        _resources = resources;
        _nyaaDownloadManager = nyaaDownloadManager;
        _credentials = credentials;
        _downloads = downloads;
        _settings = settings;
        _navigation = navigation;
        SelectedResourceCategory = ResourceCategories[0];
        SelectedSortOption = SortOptions[0];
    }

    public async Task InitializeAsync()
    {
        if (_disposed)
            return;

        if (_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        ConfigureProviders();
        _recommendationsLoaded = false;
        IsSearchMode = false;
        if (SelectedProvider is not null)
            await LoadRecommendationsAsync();
    }

    public void OnNavigatedFrom()
    {
        if (!_disposed)
            _cts.Cancel();
    }

    private void ConfigureProviders()
    {
        Providers.Clear();
        var configuredOrder = _settings.Current.DiscoverySettings.ExploreProviderOrder;
        IEnumerable<string> order = configuredOrder.Count == 0
            ? new[] { "tmdb", "bangumi", "anilist" }
            : configuredOrder;
        foreach (var id in order.Concat(["tmdb", "bangumi", "anilist"]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsProviderEnabled(id) || _discovery.GetFeeds(id, VideoDiscoveryFeedKind.Explore).Count == 0)
                continue;
            Providers.Add(new VideoDiscoveryProviderOption(id, ProviderName(id)));
        }
        SelectedProvider = Providers.FirstOrDefault();
        UpdateExploreFeeds();
    }

    private bool IsProviderEnabled(string id) => id.ToLowerInvariant() switch
    {
        "tmdb" => _settings.Current.VideoSettings.Metadata.TmdbEnabled,
        "anilist" => _settings.Current.VideoSettings.Metadata.AniListEnabled,
        "bangumi" => _settings.Current.VideoSettings.Metadata.BangumiEnabled,
        _ => false,
    };

    private static string ProviderName(string id) => id.ToLowerInvariant() switch
    {
        "tmdb" => "TMDB",
        "bangumi" => "Bangumi",
        "anilist" => "AniList",
        _ => id,
    };

    partial void OnSelectedProviderChanged(
        VideoDiscoveryProviderOption? oldValue,
        VideoDiscoveryProviderOption? newValue)
    {
        UpdateExploreFeeds();
        OnPropertyChanged(nameof(HasMoreExplorePages));
    }

    private void UpdateExploreFeeds()
    {
        ExploreFeeds.Clear();
        if (SelectedProvider is null)
            return;
        ExploreItems.Clear();
        _explorePage = 1;
        _exploreTotalPages = null;
        foreach (var feed in _discovery.GetFeeds(SelectedProvider.Id, VideoDiscoveryFeedKind.Explore))
        {
            ExploreFeeds.Add(feed with
            {
                DisplayName = ResourceStringHelper.GetString(
                    $"DiscoverFeed_{feed.ProviderId}_{feed.Id}",
                    feed.DisplayName),
            });
        }
        SelectedExploreFeed = ExploreFeeds.FirstOrDefault();
        UpdateMediaKinds();
    }

    partial void OnSelectedExploreFeedChanged(VideoDiscoveryFeed? oldValue, VideoDiscoveryFeed? newValue)
    {
        UpdateMediaKinds();
        OnPropertyChanged(nameof(HasMoreExplorePages));
    }

    partial void OnSelectedSortOptionChanged(
        VideoDiscoverySortOption? oldValue,
        VideoDiscoverySortOption? newValue)
    {
        OnPropertyChanged(nameof(SortBy));
    }

    public string SortBy => SelectedSortOption?.Value ?? "popularity.desc";

    private void UpdateMediaKinds()
    {
        MediaKinds.Clear();
        if (SelectedExploreFeed is null)
            return;
        foreach (var kind in SelectedExploreFeed.SupportedMediaKinds.Distinct())
            MediaKinds.Add(new VideoDiscoveryMediaKindOption(kind, MediaKindName(kind)));
        SelectedMediaKind = MediaKinds.FirstOrDefault();
    }

    private static string MediaKindName(VideoMetadataMediaKind kind) => kind switch
    {
        VideoMetadataMediaKind.Movie => ResourceStringHelper.GetString("DiscoverMovie", "Movie"),
        VideoMetadataMediaKind.Series => ResourceStringHelper.GetString("DiscoverSeries", "Series"),
        VideoMetadataMediaKind.Anime => ResourceStringHelper.GetString("DiscoverAnime", "Anime"),
        _ => kind.ToString(),
    };

    [RelayCommand]
    private void SelectExplore()
    {
        IsExploreVisible = true;
        IsRecommendationsVisible = false;
        IsDetailsVisible = false;
        _isRecommendationsTab = false;
    }

    [RelayCommand]
    private void OpenVideoSettings() => _navigation.Navigate(typeof(VideoSettingsPage));

    [RelayCommand]
    private async Task SelectRecommendationsAsync()
    {
        IsExploreVisible = false;
        IsRecommendationsVisible = true;
        IsDetailsVisible = false;
        _isRecommendationsTab = true;
        if (!_recommendationsLoaded)
            await LoadRecommendationsAsync();
    }

    [RelayCommand]
    private Task ApplyFiltersAsync() => LoadExploreAsync();

    [RelayCommand]
    private Task RefreshAsync()
    {
        _discovery.ClearCache();
        return IsSearchMode ? SearchVideosAsync() : LoadRecommendationsAsync(true);
    }

    [RelayCommand]
    private async Task SearchVideosAsync()
    {
        if (_disposed || SelectedProvider is null || SelectedMediaKind is null)
            return;
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            IsSearchMode = false;
            await LoadRecommendationsAsync(true);
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _discovery.SearchAsync(
                SelectedProvider.Id, query, SelectedMediaKind.Value, _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                ExploreItems.Clear();
                return;
            }

            _explorePage = 1;
            _exploreTotalPages = 1;
            IsSearchMode = true;
            ExploreItems = new ObservableCollection<VideoDiscoveryCardViewModel>(
                result.Value.Items.Select(item => new VideoDiscoveryCardViewModel(item)));
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary", "Showing {0} results.", ExploreItems.Count);
            OnPropertyChanged(nameof(HasMoreExplorePages));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_loadingMore || !HasMoreExplorePages || SelectedProvider is null || SelectedExploreFeed is null || SelectedMediaKind is null)
            return;
        _loadingMore = true;
        IsLoadingMore = true;
        try
        {
            var result = await _discovery.GetPageAsync(
                SelectedProvider.Id,
                BuildExploreRequest(_explorePage + 1),
                _cts.Token);
            if (!result.IsSuccess || result.Value is null)
            {
                if (!result.IsCancelled)
                    ErrorMessage = result.Error;
                return;
            }
            _explorePage = result.Value.Page;
            _exploreTotalPages = result.Value.TotalPages;
            foreach (var item in result.Value.Items)
                ExploreItems.Add(new VideoDiscoveryCardViewModel(item));
            OnPropertyChanged(nameof(HasMoreExplorePages));
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary", "Showing {0} results.", ExploreItems.Count);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally
        {
            _loadingMore = false;
            IsLoadingMore = false;
        }
    }

    private async Task LoadExploreAsync()
    {
        if (_disposed || SelectedProvider is null || SelectedExploreFeed is null || SelectedMediaKind is null)
            return;
        IsSearchMode = false;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _discovery.GetPageAsync(
                SelectedProvider.Id,
                BuildExploreRequest(1),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                ExploreItems.Clear();
                _exploreTotalPages = null;
                OnPropertyChanged(nameof(HasMoreExplorePages));
                return;
            }
            _explorePage = result.Value.Page;
            _exploreTotalPages = result.Value.TotalPages;
            ExploreItems = new ObservableCollection<VideoDiscoveryCardViewModel>(
                result.Value.Items.Select(item => new VideoDiscoveryCardViewModel(item)));
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary", "Showing {0} results.", ExploreItems.Count);
            OnPropertyChanged(nameof(HasMoreExplorePages));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally { IsLoading = false; }
    }

    private VideoDiscoveryRequest BuildExploreRequest(int page) => new(
        SelectedExploreFeed!.Id,
        SelectedMediaKind!.Value,
        page,
        int.TryParse(YearText, out var year) ? year : null,
        string.IsNullOrWhiteSpace(GenreId) ? null : GenreId.Trim(),
        string.IsNullOrWhiteSpace(SortBy) ? null : SortBy,
        null,
        "ja-JP",
        "JP");

    private async Task LoadRecommendationsAsync(bool refresh = false)
    {
        if (_disposed)
            return;
        IsLoadingRecommendations = true;
        ErrorMessage = null;
        if (refresh)
            _recommendationsLoaded = false;
        try
        {
            var configured = _settings.Current.DiscoverySettings.EnabledRecommendationFeeds;
            var jobs = new List<(VideoDiscoveryProviderOption Provider, VideoDiscoveryFeed Feed, VideoMetadataMediaKind Kind)>();
            foreach (var provider in Providers)
            {
                foreach (var feed in _discovery.GetFeeds(provider.Id, VideoDiscoveryFeedKind.Recommendation))
                {
                    if (configured.TryGetValue(provider.Id + ":" + feed.Id, out var enabled) && !enabled)
                        continue;
                    var kind = feed.SupportedMediaKinds.FirstOrDefault();
                    jobs.Add((provider, feed, kind));
                }
            }
            var sections = (await Task.WhenAll(jobs.Select(async job =>
            {
                try
                {
                    var result = await _discovery.GetPageAsync(
                        job.Provider.Id,
                        new VideoDiscoveryRequest(job.Feed.Id, job.Kind, 1, Language: "ja-JP", Region: "JP"),
                        _cts.Token);
                    return result.IsSuccess && result.Value is { Items.Length: > 0 } page
                        ? new VideoDiscoverySectionViewModel(job.Feed, page.Items)
                        : null;
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
            }))).Where(section => section is not null).Cast<VideoDiscoverySectionViewModel>().ToList();
            RecommendationSections = new ObservableCollection<VideoDiscoverySectionViewModel>(sections);
            _recommendationsLoaded = true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoadingRecommendations = false; }
    }

    [RelayCommand]
    private void OpenDetails(VideoDiscoveryCardViewModel card)
    {
        if (card is null || _disposed)
            return;

        _detailsCts?.Cancel();
        var detailsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _detailsCts = detailsCts;
        ErrorMessage = null;
        ResourceResults.Clear();
        ResourceQuery = _resources.BuildDefaultQuery(card.Identity);
        IsSubtitleSearch = false;
        var placeholderDetails = new VideoDiscoveryDetailsViewModel(card.Item);
        SelectedDetails = placeholderDetails;
        IsExploreVisible = false;
        IsRecommendationsVisible = false;
        IsDetailsVisible = true;
        IsLoadingDetails = true;

        _ = LoadDetailsAsync(card.Identity, placeholderDetails.Artwork, detailsCts);
    }

    private async Task LoadDetailsAsync(
        VideoMetadataCandidate identity,
        VideoDiscoveryArtwork fallbackArtwork,
        CancellationTokenSource detailsCts)
    {
        try
        {
            // Let the page visibility and placeholder state render before any
            // provider performs request setup or cache work on the UI thread.
            await Task.Yield();
            var result = await Task.Run(
                () => _discovery.GetDetailsAsync(identity, detailsCts.Token),
                detailsCts.Token);
            if (result.IsCancelled
                || !ReferenceEquals(_detailsCts, detailsCts)
                || detailsCts.IsCancellationRequested)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                if (ReferenceEquals(_detailsCts, detailsCts))
                    ErrorMessage = result.Error;
                return;
            }
            // BitmapImage is a WinUI object and must be created on the UI thread.
            // Keep provider/cache work off-thread, but project the completed details
            // back on the captured page context before publishing them to XAML.
            var detailsViewModel = new VideoDiscoveryDetailsViewModel(
                result.Value,
                fallbackArtwork);
            if (!ReferenceEquals(_detailsCts, detailsCts)
                || detailsCts.IsCancellationRequested)
                return;
            SelectedDetails = detailsViewModel;
            ResourceQuery = _resources.BuildDefaultQuery(SelectedDetails.Identity);
        }
        catch (OperationCanceledException) when (detailsCts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally
        {
            if (ReferenceEquals(_detailsCts, detailsCts))
            {
                _detailsCts = null;
                IsLoadingDetails = false;
            }
            detailsCts.Dispose();
        }
    }

    [RelayCommand]
    private void CloseDetails()
    {
        _detailsCts?.Cancel();
        IsLoadingDetails = false;
        IsDetailsVisible = false;
        IsRecommendationsVisible = _isRecommendationsTab;
        IsExploreVisible = !_isRecommendationsTab;
        SelectedDetails = null;
    }

    [RelayCommand]
    private Task SearchResourcesAsync() => SearchResourceResultsAsync(false);

    [RelayCommand]
    private Task SearchSubtitlesAsync() => SearchResourceResultsAsync(true);

    private async Task SearchResourceResultsAsync(bool subtitles)
    {
        if (SelectedDetails is null)
            return;
        IsSubtitleSearch = subtitles;
        var query = ResourceQuery.Trim();
        if (subtitles)
        {
            var defaultQuery = _resources.BuildDefaultQuery(SelectedDetails.Identity);
            query = query.Length == 0 || query.Equals(defaultQuery, StringComparison.OrdinalIgnoreCase)
                ? _resources.BuildSubtitleQuery(SelectedDetails.Identity)
                : query.Contains("srt", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("字幕", StringComparison.Ordinal)
                    ? query
                    : $"{query} srt";
            ResourceQuery = query;
        }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _resources.SearchAsync(new VideoResourceSearchRequest(
                SelectedDetails.Identity,
                query,
                SelectedResourceCategory.Code), _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                return;
            }
            ResourceResults = new ObservableCollection<NyaaTorrentItemViewModel>(
                result.Value.Select(item => new NyaaTorrentItemViewModel(item)));
            StatusText = ResourceStringHelper.FormatString(
                subtitles ? "DiscoverSubtitleSummary" : "DiscoverResourceSummary",
                subtitles ? "Showing {0} subtitle results." : "Showing {0} Nyaa results.",
                ResourceResults.Count);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleSubscriptionAsync()
    {
        if (SelectedDetails is null)
            return;

        var settings = _settings.Current.DiscoverySettings.Clone();
        var key = SubscriptionKey(SelectedDetails.Identity);
        var existing = settings.SubscribedVideoKeys.FirstOrDefault(
            value => value.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            settings.SubscribedVideoKeys.Add(key);
        else
            settings.SubscribedVideoKeys.Remove(existing);

        _settings.Set(value => value.DiscoverySettings, settings);
        OnPropertyChanged(nameof(IsSelectedDetailsSubscribed));
        OnPropertyChanged(nameof(SubscriptionButtonText));
        await _settings.SaveAsync();
    }

    private bool IsSubscribed(VideoMetadataCandidate identity) =>
        (_settings.Current.DiscoverySettings.SubscribedVideoKeys ?? []).Any(
            value => value.Equals(SubscriptionKey(identity), StringComparison.OrdinalIgnoreCase));

    private static string SubscriptionKey(VideoMetadataCandidate identity) =>
        $"{identity.ProviderId}:{identity.ProviderItemId}";

    [RelayCommand]
    private async Task DownloadAndImportResource(NyaaTorrentItemViewModel row)
    {
        if (row is null || !row.CanDownload)
            return;

        try
        {
            var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
            downloadManager.Enqueue(row.Item);
            row.IsImported = true;
            row.Status = ResourceStringHelper.GetString(
                "NyaaStatusQueued", "Added to downloads");
        }
        catch (Exception ex)
        {
            row.Status = ex.Message;
            ErrorMessage = ex.Message;
        }
    }

    private Task<INyaaDownloadManager> GetNyaaDownloadManagerAsync(CancellationToken ct)
    {
        if (_nyaaDownloadManagerTask is not null)
            return _nyaaDownloadManagerTask;

        return _nyaaDownloadManagerTask = Task.Run(
            () => _nyaaDownloadManager.Value,
            ct);
    }

    [RelayCommand]
    private async Task AddResourceToQbAsync(NyaaTorrentItemViewModel row)
    {
        if (row is null || !row.CanDownload)
            return;
        if (!await HasQbCredentialsAsync())
        {
            row.Status = ResourceStringHelper.GetString(
                "DiscoverQbMissing", "Configure qBittorrent in Downloads first.");
            ErrorMessage = row.Status;
            return;
        }
        row.IsDownloading = true;
        row.Status = ResourceStringHelper.GetString("DownloadsAddingStatus", "Adding to qBittorrent…");
        try
        {
            var result = await _downloads.AddAsync(row.Item, _cts.Token);
            if (result.IsSuccess)
            {
                row.IsImported = true;
                row.Status = ResourceStringHelper.GetString("DownloadsAddedStatus", "Added to qBittorrent");
            }
            else if (!result.IsCancelled)
            {
                row.Status = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsAddFailedStatus", "Could not add torrent.");
                ErrorMessage = result.Error;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { row.Status = ex.Message; ErrorMessage = ex.Message; }
        finally { row.IsDownloading = false; }
    }

    private async Task<bool> HasQbCredentialsAsync()
    {
        if (!_credentials.HasCredentials)
            return false;
        return await _credentials.LoadAsync(_cts.Token) is not null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _detailsCts?.Cancel();
        _cts.Dispose();
        _detailsCts?.Dispose();
    }
}
