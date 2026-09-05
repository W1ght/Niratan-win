using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Video;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.Views.Pages;

namespace Niratan.ViewModels.Pages;

/// <summary>
/// Owns only the discovery feeds, search and recommendation shelves. Detail and
/// acquisition work live in their route-specific view models.
/// </summary>
public partial class DiscoverPageViewModel : ObservableObject, IDisposable
{
    private static readonly string[] AggregatedSearchProviderOrder = ["anilist", "tmdb"];
    private readonly IVideoDiscoveryService _discovery;
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _resultCts;
    private bool _recommendationsLoaded;
    private bool _disposed;
    private bool _loadingMore;
    private int _explorePage = 1;
    private int? _exploreTotalPages;
    private int _resultGeneration;
    private DiscoverResultMode _resultMode = DiscoverResultMode.Recommendations;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingRecommendations { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProviderWarning))]
    public partial string? ProviderWarning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearchMode { get; set; }

    [ObservableProperty]
    public partial string ResultsHeading { get; set; } = "";

    [ObservableProperty]
    public partial string YearText { get; set; } = "";

    [ObservableProperty]
    public partial string GenreId { get; set; } = "";

    [ObservableProperty]
    public partial VideoDiscoverySortOption? SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<VideoDiscoveryCardViewModel> ExploreItems { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<VideoDiscoverySectionViewModel> RecommendationSections { get; set; } = [];

    public ObservableCollection<VideoDiscoveryProviderOption> Providers { get; } = [];
    public IReadOnlyList<VideoDiscoverySortOption> SortOptions { get; } =
    [
        new("popularity.desc", ResourceStringHelper.GetString("DiscoverSortPopularity", "Popularity")),
        new("vote_average.desc", ResourceStringHelper.GetString("DiscoverSortRating", "Rating")),
        new("release_date.desc", ResourceStringHelper.GetString("DiscoverSortRelease", "Release date")),
    ];

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasProviderWarning => !string.IsNullOrWhiteSpace(ProviderWarning);
    public bool HasMoreExplorePages =>
        _resultMode == DiscoverResultMode.Explore
        && ExploreItems.Count > 0
        && (_exploreTotalPages is null || _explorePage < _exploreTotalPages);
    public string SortBy => SelectedSortOption?.Value ?? "popularity.desc";

    public DiscoverPageViewModel(
        IVideoDiscoveryService discovery,
        ISettingsService settings,
        INavigationService navigation)
    {
        _discovery = discovery;
        _settings = settings;
        _navigation = navigation;
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
        if (Providers.Count > 0)
            await LoadRecommendationsAsync();
    }

    public void OnNavigatedFrom()
    {
        if (!_disposed)
        {
            CancelPendingResultRequest();
            _cts.Cancel();
        }
    }

    private void ConfigureProviders()
    {
        Providers.Clear();
        var configuredOrder = _settings.Current.DiscoverySettings.ExploreProviderOrder;
        IEnumerable<string> order = configuredOrder.Count == 0
            ? AggregatedSearchProviderOrder
            : configuredOrder.Where(id => AggregatedSearchProviderOrder.Contains(id, StringComparer.OrdinalIgnoreCase));
        foreach (var id in order.Concat(AggregatedSearchProviderOrder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsProviderEnabled(id))
                continue;
            Providers.Add(new VideoDiscoveryProviderOption(id, ProviderName(id)));
        }
    }

    private string[] GetEnabledAggregateProviderIds() => AggregatedSearchProviderOrder
        .Where(IsProviderEnabled)
        .ToArray();

    private bool IsProviderEnabled(string id) => id.ToLowerInvariant() switch
    {
        "tmdb" => _settings.Current.VideoSettings.Metadata.TmdbEnabled,
        "anilist" => _settings.Current.VideoSettings.Metadata.AniListEnabled,
        _ => false,
    };

    private static string ProviderName(string id) => id.ToLowerInvariant() switch
    {
        "tmdb" => "TMDB",
        "anilist" => "AniList",
        _ => id,
    };

    partial void OnSelectedSortOptionChanged(
        VideoDiscoverySortOption? oldValue,
        VideoDiscoverySortOption? newValue) => OnPropertyChanged(nameof(SortBy));

    [RelayCommand]
    private void OpenVideoSettings() => _navigation.Navigate(typeof(VideoSettingsPage));

    [RelayCommand]
    private void OpenDownloadTasks() =>
        _navigation.Navigate(typeof(DownloadsPage), DownloadsPageSection.Tasks);

    [RelayCommand]
    private void OpenSubscriptions() =>
        _navigation.Navigate(typeof(DownloadsPage), DownloadsPageSection.Subscriptions);

    [RelayCommand]
    private Task ApplyFiltersAsync() => LoadExploreAsync();

    [RelayCommand]
    private Task RefreshAsync()
    {
        _discovery.ClearCache();
        return _resultMode switch
        {
            DiscoverResultMode.Search => SearchVideosAsync(),
            DiscoverResultMode.Explore => LoadExploreAsync(),
            _ => LoadRecommendationsAsync(true),
        };
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchVideosAsync()
    {
        if (_disposed)
            return;
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            ExploreItems.Clear();
            ProviderWarning = null;
            await LoadRecommendationsAsync(true);
            return;
        }

        var generation = BeginResultRequest(DiscoverResultMode.Search, out var requestCts);
        IsLoading = true;
        ErrorMessage = null;
        ProviderWarning = null;
        StatusText = "";
        _explorePage = 1;
        _exploreTotalPages = 1;
        ExploreItems.Clear();
        OnPropertyChanged(nameof(HasMoreExplorePages));
        try
        {
            var result = await _discovery.SearchAggregatedAsync(
                GetEnabledAggregateProviderIds(),
                query,
                VideoDiscoverySearchCategory.All,
                requestCts.Token);
            if (!IsCurrentResultRequest(generation, requestCts))
                return;
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                ExploreItems.Clear();
                return;
            }

            ExploreItems = new ObservableCollection<VideoDiscoveryCardViewModel>(
                result.Value.Items.Select(item => new VideoDiscoveryCardViewModel(item)));
            ProviderWarning = result.Value.Error;
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary",
                "Showing {0} results.",
                ExploreItems.Count);
            OnPropertyChanged(nameof(HasMoreExplorePages));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentResultRequest(generation, requestCts))
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (IsCurrentResultRequest(generation, requestCts))
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_loadingMore
            || !HasMoreExplorePages
            || _resultMode != DiscoverResultMode.Explore)
            return;

        var requestCts = _resultCts;
        if (requestCts is null || requestCts.IsCancellationRequested)
            return;
        var generation = Volatile.Read(ref _resultGeneration);
        _loadingMore = true;
        IsLoadingMore = true;
        try
        {
            var result = await _discovery.GetAggregatedPageAsync(
                GetEnabledAggregateProviderIds(),
                BuildExploreRequest(_explorePage + 1),
                requestCts.Token);
            if (!IsCurrentResultRequest(generation, requestCts)
                || _resultMode != DiscoverResultMode.Explore)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                if (!result.IsCancelled)
                    ErrorMessage = result.Error;
                return;
            }
            _explorePage = result.Value.Page;
            _exploreTotalPages = result.Value.TotalPages;
            ProviderWarning = result.Value.Error;
            foreach (var item in result.Value.Items)
                ExploreItems.Add(new VideoDiscoveryCardViewModel(item));
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary",
                "Showing {0} results.",
                ExploreItems.Count);
            OnPropertyChanged(nameof(HasMoreExplorePages));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentResultRequest(generation, requestCts))
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (IsCurrentResultRequest(generation, requestCts))
            {
                _loadingMore = false;
                IsLoadingMore = false;
            }
        }
    }

    private async Task LoadExploreAsync()
    {
        if (_disposed)
            return;
        var generation = BeginResultRequest(DiscoverResultMode.Explore, out var requestCts);
        IsLoading = true;
        ErrorMessage = null;
        ProviderWarning = null;
        StatusText = "";
        ExploreItems.Clear();
        OnPropertyChanged(nameof(HasMoreExplorePages));
        try
        {
            var result = await _discovery.GetAggregatedPageAsync(
                GetEnabledAggregateProviderIds(),
                BuildExploreRequest(1),
                requestCts.Token);
            if (!IsCurrentResultRequest(generation, requestCts))
                return;
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
            ProviderWarning = result.Value.Error;
            ExploreItems = new ObservableCollection<VideoDiscoveryCardViewModel>(
                result.Value.Items.Select(item => new VideoDiscoveryCardViewModel(item)));
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResultSummary",
                "Showing {0} results.",
                ExploreItems.Count);
            OnPropertyChanged(nameof(HasMoreExplorePages));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentResultRequest(generation, requestCts))
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (IsCurrentResultRequest(generation, requestCts))
                IsLoading = false;
        }
    }

    private VideoDiscoveryAggregateRequest BuildExploreRequest(int page) => new(
        Page: page,
        PageSize: 20,
        Year: int.TryParse(YearText, out var year) ? year : null,
        GenreId: string.IsNullOrWhiteSpace(GenreId) ? null : GenreId.Trim(),
        SortBy: string.IsNullOrWhiteSpace(SortBy) ? null : SortBy,
        Language: "ja-JP",
        Region: "JP");

    private async Task LoadRecommendationsAsync(bool refresh = false)
    {
        if (_disposed || (_recommendationsLoaded && !refresh))
            return;
        var generation = BeginResultRequest(
            DiscoverResultMode.Recommendations,
            out var requestCts);
        IsLoadingRecommendations = true;
        ErrorMessage = null;
        ProviderWarning = null;
        StatusText = "";
        ExploreItems.Clear();
        if (refresh)
            _recommendationsLoaded = false;
        try
        {
            var result = await _discovery.GetAggregatedRecommendationsAsync(
                GetEnabledAggregateProviderIds(),
                requestCts.Token);
            if (!IsCurrentResultRequest(generation, requestCts))
                return;
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                RecommendationSections.Clear();
                return;
            }

            ProviderWarning = string.Join(
                Environment.NewLine,
                result.Value
                    .Select(page => page.Error)
                    .Where(error => !string.IsNullOrWhiteSpace(error))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase));
            if (string.IsNullOrWhiteSpace(ProviderWarning))
                ProviderWarning = null;
            RecommendationSections = new ObservableCollection<VideoDiscoverySectionViewModel>(
                result.Value
                    .Where(page => page.Items.Length > 0)
                    .Select(page => new VideoDiscoverySectionViewModel(
                        CreateAggregateRecommendationFeed(page.FeedId),
                        page.Items)));
            _recommendationsLoaded = true;
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentResultRequest(generation, requestCts))
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (IsCurrentResultRequest(generation, requestCts))
                IsLoadingRecommendations = false;
        }
    }

    private static VideoDiscoveryFeed CreateAggregateRecommendationFeed(string feedId)
    {
        var normalized = feedId.ToLowerInvariant();
        return new VideoDiscoveryFeed(
            "aggregate",
            normalized,
            normalized,
            VideoDiscoveryFeedKind.Recommendation,
            normalized == "seasonal"
                ? [VideoMetadataMediaKind.Anime]
                : [
                    VideoMetadataMediaKind.Movie,
                    VideoMetadataMediaKind.Series,
                    VideoMetadataMediaKind.Anime,
                ],
            SupportsPaging: false,
            SupportsFilters: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelPendingResultRequest();
        _cts.Cancel();
        _cts.Dispose();
    }

    private int BeginResultRequest(
        DiscoverResultMode mode,
        out CancellationTokenSource requestCts)
    {
        var generation = Interlocked.Increment(ref _resultGeneration);
        requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var previousRequest = Interlocked.Exchange(ref _resultCts, requestCts);
        previousRequest?.Cancel();
        previousRequest?.Dispose();
        _resultMode = mode;
        IsSearchMode = mode != DiscoverResultMode.Recommendations;
        ResultsHeading = mode == DiscoverResultMode.Search
            ? ResourceStringHelper.GetString("DiscoverSearchResultsHeadingText", "Search results")
            : ResourceStringHelper.GetString("DiscoverExploreResultsHeadingText", "Discover");
        IsLoading = false;
        IsLoadingRecommendations = false;
        IsLoadingMore = false;
        _loadingMore = false;
        OnPropertyChanged(nameof(HasMoreExplorePages));
        return generation;
    }

    private bool IsCurrentResultRequest(
        int generation,
        CancellationTokenSource requestCts) =>
        generation == Volatile.Read(ref _resultGeneration)
        && ReferenceEquals(Volatile.Read(ref _resultCts), requestCts)
        && !requestCts.IsCancellationRequested;

    private void CancelPendingResultRequest()
    {
        Interlocked.Increment(ref _resultGeneration);
        var requestCts = Interlocked.Exchange(ref _resultCts, null);
        requestCts?.Cancel();
        requestCts?.Dispose();
        IsLoading = false;
        IsLoadingRecommendations = false;
        IsLoadingMore = false;
        _loadingMore = false;
        OnPropertyChanged(nameof(HasMoreExplorePages));
    }

    private enum DiscoverResultMode
    {
        Recommendations,
        Search,
        Explore,
    }
}
