using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public partial class VideoDiscoveryResourceSearchPageViewModel : ObservableObject, IDisposable
{
    private readonly IVideoResourceSearchService _resources;
    private readonly INyaaSubscriptionService _subscriptions;
    private readonly Lazy<INyaaDownloadManager> _nyaaDownloadManager;
    private readonly IQbittorrentCredentialStore _credentials;
    private readonly IQbittorrentDownloadCoordinator _downloads;
    private readonly ISettingsService _settings;
    private CancellationTokenSource _cts = new();
    private Task<INyaaDownloadManager>? _nyaaDownloadManagerTask;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubscriptionMode))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    [NotifyPropertyChangedFor(nameof(DownloadActionText))]
    public partial VideoDiscoveryResourceSearchTarget? Target { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial NyaaSearchCategory SelectedCategory { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<NyaaTorrentItemViewModel> Results { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrictReleaseGroup))]
    [NotifyPropertyChangedFor(nameof(StrictResolution))]
    [NotifyPropertyChangedFor(nameof(StrictStartAfterText))]
    [NotifyPropertyChangedFor(nameof(HasStrictSelection))]
    [NotifyCanExecuteChangedFor(nameof(SubmitSelectionCommand))]
    public partial NyaaTorrentItemViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(SubmitSelectionCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public IReadOnlyList<NyaaSearchCategory> Categories { get; } =
    [
        new("0_0", ResourceStringHelper.GetString("NyaaCategoryAll", "All categories")),
        new("1_0", ResourceStringHelper.GetString("NyaaCategoryAnime", "Anime")),
        new("4_0", ResourceStringHelper.GetString("NyaaCategoryLiveAction", "Live action")),
    ];

    public bool IsSubscriptionMode =>
        Target?.Mode == VideoDiscoveryResourceRouteMode.Subscription;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string PageTitle => IsSubscriptionMode
        ? ResourceStringHelper.GetString("DiscoverSubscriptionHeading", "Choose a Nyaa release to subscribe")
        : ResourceStringHelper.GetString("DiscoverResourceHeading.Text", "Search Nyaa resources");
    public string DownloadActionText => IsSubscriptionMode
        ? ResourceStringHelper.GetString(
            "DiscoverDownloadAndSubscribeButton",
            "Download and subscribe")
        : _settings.Current.DownloadBackend == DownloadBackendKind.Qbittorrent
            ? ResourceStringHelper.GetString("DiscoverAddToQbButton.Content", "Add to qBittorrent")
            : ResourceStringHelper.GetString("DiscoverNyaaDownloadButton.Content", "Download and import");
    public string StrictReleaseGroup => ParseReleaseGroup(SelectedResult?.Item.Title) ?? "—";
    public string StrictResolution => ParseResolution(SelectedResult?.Item.Title) ?? "—";
    public int? StrictStartAfterEpisode =>
        Target?.Work.Identity.MediaKind == VideoMetadataMediaKind.Movie
            ? null
            : ParseEpisode(SelectedResult?.Item.Title)
                ?? Target?.Work.Identity.EpisodeNumber;
    public string StrictStartAfterText =>
        Target?.Work.Identity.MediaKind == VideoMetadataMediaKind.Movie
            ? ResourceStringHelper.GetString("DiscoverSubscriptionMovieStart", "Movie")
            : StrictStartAfterEpisode?.ToString(CultureInfo.CurrentCulture) ?? "—";
    public bool HasStrictSelection => SelectedResult is not null
        && !SelectedResult.Item.IsRemake
        && !NyaaSubscriptionService.IsBatchTitle(SelectedResult.Item.Title)
        && ParseReleaseGroup(SelectedResult.Item.Title) is not null
        && ParseResolution(SelectedResult.Item.Title) is not null
        && (Target?.Work.Identity.MediaKind == VideoMetadataMediaKind.Movie
            || StrictStartAfterEpisode is not null);

    public VideoDiscoveryResourceSearchPageViewModel(
        IVideoResourceSearchService resources,
        INyaaSubscriptionService subscriptions,
        Lazy<INyaaDownloadManager> nyaaDownloadManager,
        IQbittorrentCredentialStore credentials,
        IQbittorrentDownloadCoordinator downloads,
        ISettingsService settings)
    {
        _resources = resources;
        _subscriptions = subscriptions;
        _nyaaDownloadManager = nyaaDownloadManager;
        _credentials = credentials;
        _downloads = downloads;
        _settings = settings;
        SelectedCategory = Categories[0];
    }

    public async Task InitializeAsync(VideoDiscoveryResourceSearchTarget target)
    {
        if (_disposed)
            return;
        ResetCancellation();
        Target = target;
        SearchQuery = _resources.BuildDefaultQuery(target.Work.Identity);
        SelectedCategory = target.Work.Identity.MediaKind == VideoMetadataMediaKind.Anime
            ? Categories.First(category => category.Code == "1_0")
            : Categories[0];
        Results.Clear();
        SelectedResult = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(DownloadActionText));
        await SearchAsync();
    }

    private bool CanSearch() => !IsBusy
        && Target is not null
        && !string.IsNullOrWhiteSpace(SearchQuery);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (Target is null)
            return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _resources.SearchAsync(
                new VideoResourceSearchRequest(
                    Target.Work.Identity,
                    SearchQuery.Trim(),
                    SelectedCategory.Code),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                return;
            }
            Results = new ObservableCollection<NyaaTorrentItemViewModel>(
                result.Value.Select(item => new NyaaTorrentItemViewModel(item)));
            SelectedResult = null;
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverResourceSummary",
                "Showing {0} Nyaa results.",
                Results.Count);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanSubmitSelection() => !IsBusy
        && SelectedResult is not null
        && (!IsSubscriptionMode || HasStrictSelection);

    [RelayCommand(CanExecute = nameof(CanSubmitSelection))]
    private async Task SubmitSelectionAsync()
    {
        if (Target is null || SelectedResult is null)
            return;
        IsBusy = true;
        ErrorMessage = null;
        var row = SelectedResult;
        try
        {
            if (IsSubscriptionMode)
            {
                var artwork = new NyaaSubscriptionArtwork(
                    Target.Work.Identity.PosterUrl,
                    Target.Work.Artwork.PosterPath);
                var result = await _subscriptions.SubscribeAsync(
                    Target.Work.Identity,
                    SearchQuery.Trim(),
                    SelectedCategory.Code,
                    row.Item,
                    StrictStartAfterEpisode,
                    artwork,
                    _cts.Token);
                if (!result.IsSuccess)
                {
                    if (!result.IsCancelled)
                        ErrorMessage = result.Error;
                    return;
                }
                row.Status = ResourceStringHelper.FormatString(
                    "DiscoverSubscriptionCreated",
                    "Downloaded the selected release and subscribed. {0} new task(s) were queued.",
                    result.Value);
                StatusText = row.Status;
                return;
            }

            if (_settings.Current.DownloadBackend == DownloadBackendKind.Qbittorrent)
            {
                if (!_credentials.HasCredentials
                    || await _credentials.LoadAsync(_cts.Token) is null)
                {
                    ErrorMessage = ResourceStringHelper.GetString(
                        "DiscoverQbMissing",
                        "Configure qBittorrent in Downloads first.");
                    row.Status = ErrorMessage;
                    return;
                }
                var result = await _downloads.AddAsync(row.Item, _cts.Token);
                if (!result.IsSuccess)
                {
                    if (!result.IsCancelled)
                        ErrorMessage = result.Error;
                    row.Status = result.Error ?? "";
                    return;
                }
                row.IsImported = true;
                row.Status = ResourceStringHelper.GetString(
                    "DownloadsAddedStatus",
                    "Added to qBittorrent");
            }
            else
            {
                var manager = await GetNyaaDownloadManagerAsync(_cts.Token);
                manager.Enqueue(row.Item);
                row.IsImported = true;
                row.Status = ResourceStringHelper.GetString(
                    "NyaaStatusQueued",
                    "Added to downloads");
            }
            StatusText = row.Status;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            row.Status = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private Task<INyaaDownloadManager> GetNyaaDownloadManagerAsync(CancellationToken ct) =>
        _nyaaDownloadManagerTask ??= Task.Run(() => _nyaaDownloadManager.Value, ct);

    private void ResetCancellation()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    public void OnNavigatedFrom() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    internal static string? ParseReleaseGroup(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var match = ReleaseGroupRegex().Match(title);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static string? ParseResolution(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var match = ResolutionRegex().Match(title);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static int? ParseEpisode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var match = EpisodeRegex().Match(title);
        return match.Success && int.TryParse(match.Groups[1].Value, out var episode)
            ? episode
            : null;
    }

    [GeneratedRegex(@"^\s*\[([^\]]+)\]")]
    private static partial Regex ReleaseGroupRegex();
    [GeneratedRegex(@"\b(2160p|1080p|720p|576p|480p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
    [GeneratedRegex(@"(?:S\d{1,2}E|\bE(?:P)?\s*|\s-\s)0*(\d{1,4})(?:\b|v\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();
}
