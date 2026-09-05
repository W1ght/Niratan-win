using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public sealed record DownloadBackendOption(DownloadBackendKind Kind, string DisplayName);

public partial class NyaaSubscriptionItemViewModel : ObservableObject
{
    public NyaaVideoSubscription Subscription { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    public string Key => Subscription.Key;
    public string Title => Subscription.Year is int year
        ? $"{Subscription.Title} ({year})"
        : Subscription.Title;
    public bool IsLegacy => string.IsNullOrWhiteSpace(Subscription.ReleaseGroup)
        || string.IsNullOrWhiteSpace(Subscription.Resolution);
    public string StateText => IsLegacy
        ? ResourceStringHelper.GetString(
            "DownloadsSubscriptionNeedsSetup",
            "Needs release setup")
        : Subscription.Enabled
        ? ResourceStringHelper.GetString("DownloadsSubscriptionEnabled", "Enabled")
        : ResourceStringHelper.GetString("DownloadsSubscriptionDisabled", "Paused");
    public string ToggleButtonText => IsLegacy
        ? ResourceStringHelper.GetString(
            "DownloadsSubscriptionNeedsSetupButton",
            "Needs setup")
        : Subscription.Enabled
        ? ResourceStringHelper.GetString("DownloadsSubscriptionDisableButton", "Pause")
        : ResourceStringHelper.GetString("DownloadsSubscriptionEnableButton", "Enable");
    public bool CanToggle => !IsLegacy && !IsBusy;
    public bool CanCheck => !IsLegacy && Subscription.Enabled && !IsBusy;
    public string BackendText => IsLegacy
        ? ResourceStringHelper.GetString(
            "DownloadsSubscriptionLegacyBackend",
            "Download backend not selected")
        : Subscription.DownloadBackend == DownloadBackendKind.MonoTorrent
        ? ResourceStringHelper.GetString("DownloadsBackendMonoTorrent", "Built-in MonoTorrent")
        : ResourceStringHelper.GetString("DownloadsBackendQbittorrent", "qBittorrent (external)");
    public string RulesText => IsLegacy
        ? ResourceStringHelper.GetString(
            "DownloadsSubscriptionLegacyRule",
            "Open Video discovery and subscribe again to choose a release rule.")
        : string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Subscription.Query) ? null : Subscription.Query,
        string.IsNullOrWhiteSpace(Subscription.ReleaseGroup) ? null : $"[{Subscription.ReleaseGroup}]",
        string.IsNullOrWhiteSpace(Subscription.Resolution) ? null : Subscription.Resolution,
        Subscription.Trusted is true || Subscription.RequireTrusted
            ? ResourceStringHelper.GetString("DownloadsSubscriptionTrusted", "Trusted")
            : Subscription.Trusted is false
                ? ResourceStringHelper.GetString("DownloadsSubscriptionUntrusted", "Untrusted")
                : null,
        Subscription.StartAfterEpisode is int episode
            ? ResourceStringHelper.FormatString(
                "DownloadsSubscriptionStartsFromEpisode",
                "From episode {0}",
                episode)
            : null,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string LastCheckedText => Subscription.LastCheckedAt is DateTimeOffset checkedAt
        ? ResourceStringHelper.FormatString(
            "DownloadsSubscriptionLastChecked",
            "Last checked {0}",
            checkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : ResourceStringHelper.GetString(
            "DownloadsSubscriptionNeverChecked",
            "Never checked");
    public string LastError => Subscription.LastError ?? "";
    public bool HasError => !string.IsNullOrWhiteSpace(Subscription.LastError);
    public BitmapImage? PosterImage { get; }

    public NyaaSubscriptionItemViewModel(NyaaVideoSubscription subscription)
    {
        Subscription = subscription.Clone();
        PosterImage = CreatePosterImage(Subscription);
    }

    private static BitmapImage? CreatePosterImage(NyaaVideoSubscription subscription)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(subscription.PosterPath))
            {
                var posterPath = Path.GetFullPath(subscription.PosterPath);
                var cacheRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Niratan",
                    "Cache",
                    "VideoMetadataArtwork"));
                if (posterPath.StartsWith(
                        cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    && File.Exists(posterPath))
                {
                    return new BitmapImage(new Uri(posterPath, UriKind.Absolute));
                }
            }

        }
        catch
        {
        }
        return null;
    }
}

public sealed class NyaaDownloadTaskItemViewModel : ObservableObject
{
    public NyaaDownloadTaskSnapshot Snapshot { get; private set; }

    public string TaskId => Snapshot.TaskId;
    public NyaaTorrentItem Item => Snapshot.Item;
    public string StateText => Snapshot.StateText;
    public string Status => Snapshot.Status;
    public double ProgressPercent => Snapshot.ProgressPercent;
    public string ProgressText => Snapshot.ProgressText;
    public string DownloadRateText => Snapshot.DownloadRateText;
    public int ConnectedPeers => Snapshot.ConnectedPeers;
    public string? Error => Snapshot.Error;
    public string? DownloadRootPath => Snapshot.DownloadRootPath;
    public bool CanPause => Snapshot.CanPause;
    public bool CanResume => Snapshot.CanResume;
    public bool CanCancel => Snapshot.CanCancel;
    public bool CanRetry => Snapshot.CanRetry;
    public bool CanOpenFolder => Snapshot.CanOpenFolder;
    public bool CanRemove => Snapshot.CanRemove;

    public NyaaDownloadTaskItemViewModel(NyaaDownloadTaskSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public void Update(NyaaDownloadTaskSnapshot snapshot)
    {
        if (Equals(Snapshot, snapshot))
            return;

        Snapshot = snapshot;
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DownloadRateText));
        OnPropertyChanged(nameof(ConnectedPeers));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(DownloadRootPath));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CanRemove));
    }
}

public partial class DownloadsPageViewModel : ObservableObject, IDisposable
{
    private readonly INyaaClient _nyaaClient;
    private readonly Lazy<INyaaDownloadManager> _nyaaDownloadManager;
    private readonly IQbittorrentDownloadCoordinator _downloadCoordinator;
    private readonly IQbittorrentClient _qbittorrentClient;
    private readonly IQbittorrentCredentialStore _credentialStore;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly IFileRevealService _fileRevealService;
    private readonly IVideoDownloadImportService _videoImportService;
    private readonly INyaaSubscriptionService _subscriptionService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _nyaaDownloadManagerSync = new();
    private readonly HashSet<string> _subscriptionArtworkRefreshes = new(StringComparer.OrdinalIgnoreCase);
    private Task<INyaaDownloadManager>? _nyaaDownloadManagerTask;
    private INyaaDownloadManager? _resolvedNyaaDownloadManager;
    private IReadOnlyList<NyaaTorrentItem> _allSearchResults = [];
    private bool _initialized;
    private bool _disposed;
    private bool _nyaaTasksSubscribed;

    [ObservableProperty]
    public partial bool IsDiscoveryVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTasksVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSubscriptionsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubscriptionEmpty))]
    [NotifyPropertyChangedFor(nameof(CanCheckAllSubscriptions))]
    public partial ObservableCollection<NyaaSubscriptionItemViewModel> Subscriptions { get; set; } = [];

    [ObservableProperty]
    public partial string SubscriptionStatusText { get; set; } = ResourceStringHelper.GetString(
        "DownloadsSubscriptionsInitialStatus",
        "Subscriptions are checked every 30 minutes while enabled.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckAllSubscriptions))]
    public partial bool IsCheckingSubscriptions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial NyaaSearchCategory SelectedCategory { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string SearchSummary { get; set; } = ResourceStringHelper.GetString(
        "DownloadsInitialSummary",
        "Search Nyaa for a resource.");

    [ObservableProperty]
    public partial ObservableCollection<NyaaTorrentItemViewModel> SearchResults { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<QbittorrentTorrentViewModel> Tasks { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NyaaDownloadTaskItemViewModel> BuiltInTasks { get; set; } = [];

    [ObservableProperty]
    public partial string TaskStatusText { get; set; } = ResourceStringHelper.GetString(
        "DownloadsTasksInitialStatus",
        "Configure qBittorrent to load download tasks.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaskDetailsError))]
    public partial string? TaskDetailsErrorMessage { get; set; }

    [ObservableProperty]
    public partial string TaskDetailsStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedTask))]
    public partial ObservableCollection<VideoLibrarySource> ImportSources { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedTask))]
    public partial VideoLibrarySource? SelectedImportSource { get; set; }

    [ObservableProperty]
    public partial string ImportStatusText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedTask))]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImportSelectedTask))]
    public partial QbittorrentTorrentViewModel? SelectedTask { get; set; }

    [ObservableProperty]
    public partial QbittorrentTorrentDetailsViewModel? SelectedTaskDetails { get; set; }

    [ObservableProperty]
    public partial bool IsTaskDetailsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsTaskOverviewVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsTaskFilesVisible { get; set; }

    [ObservableProperty]
    public partial bool IsTaskTrackersVisible { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    public partial string PasswordDraft { get; set; } = "";

    [ObservableProperty]
    public partial string ApiKeyDraft { get; set; } = "";

    [ObservableProperty]
    public partial string DefaultSavePath { get; set; } = "";

    [ObservableProperty]
    public partial string DefaultCategory { get; set; } = "niratan";

    [ObservableProperty]
    public partial bool AddPaused { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonoTorrentDownloadRootIsDefault))]
    public partial string MonoTorrentDownloadRootPath { get; set; } =
        MonoTorrentDownloadRootPolicy.DefaultPath;

    [ObservableProperty]
    public partial string MonoTorrentAdditionalTrackersText { get; set; } = "";

    [ObservableProperty]
    public partial int MonoTorrentListenPort { get; set; }

    [ObservableProperty]
    public partial bool MonoTorrentPortForwardingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MonoTorrentDhtEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MonoTorrentPeerExchangeEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MonoTorrentLocalPeerDiscoveryEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int MonoTorrentMaximumConnections { get; set; } = 120;

    [ObservableProperty]
    public partial int MonoTorrentMaximumConnectionsPerTorrent { get; set; } = 80;

    [ObservableProperty]
    public partial int MonoTorrentMaximumHalfOpenConnections { get; set; } = 20;

    [ObservableProperty]
    public partial int MonoTorrentMaximumOpenFiles { get; set; } = 96;

    [ObservableProperty]
    public partial int MonoTorrentDownloadRateLimitKiB { get; set; }

    [ObservableProperty]
    public partial int MonoTorrentUploadRateLimitKiB { get; set; } = 2048;

    [ObservableProperty]
    public partial int MonoTorrentUploadSlotsPerTorrent { get; set; } = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuiltInBackend))]
    [NotifyPropertyChangedFor(nameof(IsQbittorrentBackend))]
    [NotifyPropertyChangedFor(nameof(BackendDescription))]
    [NotifyPropertyChangedFor(nameof(DownloadActionText))]
    [NotifyPropertyChangedFor(nameof(DownloadNotice))]
    public partial DownloadBackendOption? SelectedBackendOption { get; set; }

    [ObservableProperty]
    public partial bool IsSavingSettings { get; set; }

    [ObservableProperty]
    public partial bool IsTestingConnection { get; set; }

    [ObservableProperty]
    public partial string CredentialStatusText { get; set; } = ResourceStringHelper.GetString(
        "DownloadsCredentialsMissing",
        "Not configured");

    public IReadOnlyList<NyaaSearchCategory> Categories { get; } =
    [
        new("0_0", ResourceStringHelper.GetString("NyaaCategoryAll", "All categories")),
        new("3_0", ResourceStringHelper.GetString("NyaaCategoryLiterature", "Literature")),
        new("2_0", ResourceStringHelper.GetString("NyaaCategoryAudio", "Audio")),
        new("1_0", ResourceStringHelper.GetString("NyaaCategoryAnime", "Anime")),
        new("4_0", ResourceStringHelper.GetString("NyaaCategoryLiveAction", "Live action")),
    ];

    public IReadOnlyList<DownloadBackendOption> BackendOptions { get; } =
    [
        new(
            DownloadBackendKind.MonoTorrent,
            ResourceStringHelper.GetString("DownloadsBackendMonoTorrent", "Built-in MonoTorrent")),
        new(
            DownloadBackendKind.Qbittorrent,
            ResourceStringHelper.GetString("DownloadsBackendQbittorrent", "qBittorrent (external)")),
    ];

    public bool CanSearch => !IsSearching && !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsSubscriptionEmpty => Subscriptions.Count == 0;
    public bool CanCheckAllSubscriptions => !IsCheckingSubscriptions && Subscriptions.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBuiltInBackend => SelectedBackendOption?.Kind == DownloadBackendKind.MonoTorrent;
    public bool IsQbittorrentBackend => SelectedBackendOption?.Kind == DownloadBackendKind.Qbittorrent;
    public bool MonoTorrentDownloadRootIsDefault =>
        string.IsNullOrWhiteSpace(MonoTorrentDownloadRootPath)
        || MonoTorrentDownloadRootPolicy.PathsEqual(
            MonoTorrentDownloadRootPath,
            MonoTorrentDownloadRootPolicy.DefaultPath);
    public string BackendDescription => IsBuiltInBackend
        ? ResourceStringHelper.GetString(
            "DownloadsBackendMonoTorrentDescription",
            "Downloads run inside Niratan through MonoTorrent and supported resources are imported automatically.")
        : ResourceStringHelper.GetString(
            "DownloadsBackendQbittorrentDescription",
            "Downloads are managed by the configured qBittorrent WebUI and remain available after Niratan restarts.");
    public string DownloadActionText => IsBuiltInBackend
        ? ResourceStringHelper.GetString("DownloadsUseMonoTorrentButton", "Download with MonoTorrent")
        : ResourceStringHelper.GetString("DownloadsAddButton.Content", "Add to qBittorrent");
    public string DownloadNotice => IsBuiltInBackend
        ? ResourceStringHelper.GetString(
            "DownloadsMonoTorrentNotice",
            "Search results come from Nyaa's RSS feed. Niratan will download selected resources with its built-in MonoTorrent engine.")
        : ResourceStringHelper.GetString(
            "DownloadsQbittorrentNotice",
            "Search results come from Nyaa's RSS feed. Review the release before sending it to qBittorrent.");
    public bool HasTaskDetailsError => !string.IsNullOrWhiteSpace(TaskDetailsErrorMessage);
    public string SelectedTaskTitle => SelectedTask?.Name ?? "";
    public bool HasSelectedTask => SelectedTask is not null;
    public bool CanCancelSelectedTask => SelectedTask?.CanPause == true;
    public bool CanResumeSelectedTask => SelectedTask?.CanResume == true;
    public bool CanOpenSelectedTaskLocation => !string.IsNullOrWhiteSpace(SelectedTask?.LocationPath);
    public bool IsTaskDetailsLoaded => SelectedTaskDetails is not null;
    public bool CanImportSelectedTask =>
        SelectedTask?.Torrent.IsCompleted == true
        && SelectedImportSource is not null
        && !IsImporting;
    public IReadOnlyList<QbittorrentTorrentFileViewModel> TaskDetailFiles =>
        SelectedTaskDetails?.Files ?? [];
    public IReadOnlyList<QbittorrentTorrentTrackerViewModel> TaskDetailTrackers =>
        SelectedTaskDetails?.Trackers ?? [];

    public event EventHandler? TaskDetailsRequested;

    public DownloadsPageViewModel(
        INyaaClient nyaaClient,
        Lazy<INyaaDownloadManager> nyaaDownloadManager,
        IQbittorrentDownloadCoordinator downloadCoordinator,
        IQbittorrentClient qbittorrentClient,
        IQbittorrentCredentialStore credentialStore,
        ISettingsService settingsService,
        IDialogService dialogService,
        IFileRevealService fileRevealService,
        IVideoDownloadImportService videoImportService,
        INyaaSubscriptionService subscriptionService)
    {
        _nyaaClient = nyaaClient;
        _nyaaDownloadManager = nyaaDownloadManager;
        _downloadCoordinator = downloadCoordinator;
        _qbittorrentClient = qbittorrentClient;
        _credentialStore = credentialStore;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _fileRevealService = fileRevealService;
        _videoImportService = videoImportService;
        _subscriptionService = subscriptionService;
        SelectedCategory = Categories[0];
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcherQueue = null;
        }

        SelectedBackendOption = BackendOptions[0];
        _downloadCoordinator.TasksChanged += OnTasksChanged;
        _subscriptionService.SubscriptionsChanged += OnSubscriptionsChanged;
    }

    partial void OnSelectedBackendOptionChanged(
        DownloadBackendOption? oldValue,
        DownloadBackendOption? newValue)
    {
        SelectedTask = null;
        SelectedTaskDetails = null;
        TaskDetailsErrorMessage = null;
        if (_initialized && oldValue?.Kind != newValue?.Kind)
            _ = RefreshTasksAsync();
    }

    partial void OnSelectedTaskChanged(
        QbittorrentTorrentViewModel? oldValue,
        QbittorrentTorrentViewModel? newValue)
    {
        OnPropertyChanged(nameof(SelectedTaskTitle));
        OnPropertyChanged(nameof(HasSelectedTask));
        OnPropertyChanged(nameof(CanCancelSelectedTask));
        OnPropertyChanged(nameof(CanResumeSelectedTask));
        OnPropertyChanged(nameof(CanOpenSelectedTaskLocation));
    }

    partial void OnSelectedTaskDetailsChanged(
        QbittorrentTorrentDetailsViewModel? oldValue,
        QbittorrentTorrentDetailsViewModel? newValue)
    {
        OnPropertyChanged(nameof(IsTaskDetailsLoaded));
        OnPropertyChanged(nameof(TaskDetailFiles));
        OnPropertyChanged(nameof(TaskDetailTrackers));
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
            return;

        LoadSettingsDraft();
        UpdateSubscriptions(_subscriptionService.GetSubscriptions());
        _initialized = true;
        await RefreshTasksAsync();
    }

    [RelayCommand]
    private void SelectDiscovery()
    {
        IsDiscoveryVisible = true;
        IsTasksVisible = false;
        IsSubscriptionsVisible = false;
        IsSettingsVisible = false;
    }

    [RelayCommand]
    private async Task SelectTasksAsync()
    {
        IsDiscoveryVisible = false;
        IsTasksVisible = true;
        IsSubscriptionsVisible = false;
        IsSettingsVisible = false;
        await RefreshTasksAsync();
    }

    [RelayCommand]
    private void SelectSubscriptions()
    {
        IsDiscoveryVisible = false;
        IsTasksVisible = false;
        IsSubscriptionsVisible = true;
        IsSettingsVisible = false;
        UpdateSubscriptions(_subscriptionService.GetSubscriptions());
    }

    [RelayCommand]
    private void SelectSettings()
    {
        IsDiscoveryVisible = false;
        IsTasksVisible = false;
        IsSubscriptionsVisible = false;
        IsSettingsVisible = true;
        LoadSettingsDraft();
    }

    [RelayCommand]
    private async Task CheckAllSubscriptionsAsync()
    {
        if (IsCheckingSubscriptions)
            return;

        IsCheckingSubscriptions = true;
        ErrorMessage = null;
        try
        {
            await _subscriptionService.CheckAllAsync(_cts.Token);
            UpdateSubscriptions(_subscriptionService.GetSubscriptions());
            SubscriptionStatusText = ResourceStringHelper.GetString(
                "DownloadsSubscriptionsChecked",
                "Finished checking enabled subscriptions.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SubscriptionStatusText = ex.Message;
        }
        finally
        {
            IsCheckingSubscriptions = false;
        }
    }

    [RelayCommand]
    private async Task CheckSubscriptionAsync(NyaaSubscriptionItemViewModel item)
    {
        if (item is null || !item.CanCheck)
            return;

        item.IsBusy = true;
        try
        {
            var result = await _subscriptionService.CheckOneAsync(item.Key, _cts.Token);
            if (result.IsSuccess)
            {
                SubscriptionStatusText = ResourceStringHelper.FormatString(
                    "DownloadsSubscriptionCheckResult",
                    "Queued {0} new releases.",
                    result.Value);
            }
            else if (!result.IsCancelled)
            {
                ErrorMessage = result.Error;
                SubscriptionStatusText = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsSubscriptionCheckFailed",
                    "Subscription check failed.");
            }
        }
        finally
        {
            item.IsBusy = false;
            UpdateSubscriptions(_subscriptionService.GetSubscriptions());
        }
    }

    [RelayCommand]
    private async Task ToggleSubscriptionAsync(NyaaSubscriptionItemViewModel item)
    {
        if (item is null || item.IsBusy)
            return;

        item.IsBusy = true;
        try
        {
            await _subscriptionService.SetEnabledAsync(
                item.Key,
                !item.Subscription.Enabled,
                _cts.Token);
            SubscriptionStatusText = item.Subscription.Enabled
                ? ResourceStringHelper.GetString(
                    "DownloadsSubscriptionPausedStatus",
                    "Subscription paused. Existing downloads were not changed.")
                : ResourceStringHelper.GetString(
                    "DownloadsSubscriptionEnabledStatus",
                    "Subscription enabled.");
        }
        finally
        {
            item.IsBusy = false;
            UpdateSubscriptions(_subscriptionService.GetSubscriptions());
        }
    }

    [RelayCommand]
    private async Task RemoveSubscriptionAsync(NyaaSubscriptionItemViewModel item)
    {
        if (item is null || item.IsBusy)
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString(
                "DownloadsRemoveSubscriptionTitle",
                "Remove subscription?"),
            ResourceStringHelper.FormatString(
                "DownloadsRemoveSubscriptionMessage",
                "Stop following '{0}'? Existing download tasks and downloaded files will be kept.",
                item.Subscription.Title),
            ResourceStringHelper.GetString(
                "DownloadsRemoveSubscriptionConfirmButton",
                "Remove subscription"),
            ResourceStringHelper.GetString("DownloadsCancelButton", "Cancel"));
        if (!confirmed)
            return;

        item.IsBusy = true;
        try
        {
            await _subscriptionService.RemoveAsync(item.Key, _cts.Token);
            SubscriptionStatusText = ResourceStringHelper.GetString(
                "DownloadsSubscriptionRemovedStatus",
                "Subscription removed. Existing downloads were kept.");
        }
        finally
        {
            item.IsBusy = false;
            UpdateSubscriptions(_subscriptionService.GetSubscriptions());
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsSearching = true;
        ErrorMessage = null;
        try
        {
            var result = await _nyaaClient.SearchAsync(
                new NyaaSearchRequest(SearchQuery.Trim(), SelectedCategory.Code),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error;
                SearchSummary = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsSearchFailed",
                    "Search failed.");
                return;
            }

            _allSearchResults = result.Value ?? [];
            SearchResults = new ObservableCollection<NyaaTorrentItemViewModel>(
                _allSearchResults.Select(item => new NyaaTorrentItemViewModel(item)));
            SearchSummary = _allSearchResults.Count == 0
                ? ResourceStringHelper.GetString("NyaaNoResults", "No matching torrents.")
                : ResourceStringHelper.FormatString(
                    "DownloadsSearchResultSummary",
                    "Showing {0} results. Verify the release contents before downloading.",
                    _allSearchResults.Count);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task AddToBackendAsync(NyaaTorrentItemViewModel row)
    {
        if (row is null || !row.CanDownload)
            return;

        row.IsDownloading = true;
        row.Status = ResourceStringHelper.GetString(
            IsBuiltInBackend ? "DownloadsAddingMonoTorrentStatus" : "DownloadsAddingStatus",
            IsBuiltInBackend ? "Adding to MonoTorrent…" : "Adding to qBittorrent…");
        try
        {
            if (IsBuiltInBackend)
            {
                var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
                downloadManager.Enqueue(row.Item);
                row.IsImported = true;
                row.Status = ResourceStringHelper.GetString(
                    "DownloadsAddedMonoTorrentStatus",
                    "Added to MonoTorrent downloads");
                TaskStatusText = ResourceStringHelper.GetString(
                    "DownloadsAddedMonoTorrentTaskStatus",
                    "The torrent was added to the built-in MonoTorrent queue.");
            }
            else
            {
                var result = await _downloadCoordinator.AddAsync(row.Item, _cts.Token);
                if (result.IsSuccess)
                {
                    row.IsImported = true;
                    row.Status = ResourceStringHelper.GetString(
                        "DownloadsAddedStatus",
                        "Added to qBittorrent");
                    TaskStatusText = ResourceStringHelper.GetString(
                        "DownloadsAddedTaskStatus",
                        "The torrent was added to qBittorrent.");
                }
                else if (!result.IsCancelled)
                {
                    row.Status = result.Error ?? ResourceStringHelper.GetString(
                        "DownloadsAddFailedStatus",
                        "Could not add torrent.");
                    ErrorMessage = result.Error;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            row.Status = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            row.IsDownloading = false;
        }
    }

    [RelayCommand]
    private Task RefreshTasksAsync()
    {
        if (_disposed)
            return Task.CompletedTask;

        return RefreshTasksCoreAsync();
    }

    [RelayCommand]
    private async Task PauseBuiltInTaskAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        await downloadManager.PauseAsync(task.TaskId);
    }

    [RelayCommand]
    private async Task ResumeBuiltInTaskAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        await downloadManager.ResumeAsync(task.TaskId);
    }

    [RelayCommand]
    private async Task CancelBuiltInTaskAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        downloadManager.Cancel(task.TaskId);
    }

    [RelayCommand]
    private async Task RetryBuiltInTaskAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        downloadManager.Retry(task.TaskId);
    }

    [RelayCommand]
    private async Task RemoveBuiltInTaskAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null || !task.CanRemove)
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString(
                "DownloadsDeleteTaskTitle",
                "Delete download task?"),
            ResourceStringHelper.FormatString(
                "DownloadsDeleteBuiltInTaskMessage",
                "Remove '{0}' from the MonoTorrent task list? Downloaded files will be kept.",
                task.Item.Title),
            ResourceStringHelper.GetString("DownloadsDeleteTaskButtonText", "Delete task"),
            ResourceStringHelper.GetString("DownloadsCancelButton", "Cancel"));
        if (confirmed)
        {
            var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
            downloadManager.Remove(task.TaskId);
        }
    }

    [RelayCommand]
    private async Task OpenBuiltInTaskFolderAsync(NyaaDownloadTaskItemViewModel task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.DownloadRootPath))
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "DownloadsBuiltInLocationUnavailable",
                "The MonoTorrent download folder is unavailable.");
            return;
        }

        var result = await _fileRevealService.RevealInFileExplorerAsync(
            task.DownloadRootPath,
            _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            ErrorMessage = result.Error ?? ResourceStringHelper.GetString(
                "DownloadsLocationOpenFailed",
                "Could not open the task location.");
        }
    }

    [RelayCommand]
    private async Task ShowTaskDetailsAsync(QbittorrentTorrentViewModel task)
    {
        if (task is null || !task.CanDelete || _disposed)
            return;

        SelectedTask = task;
        await LoadImportSourcesAsync(task);
        SelectedTaskDetails = null;
        TaskDetailsErrorMessage = null;
        TaskDetailsStatusText = ResourceStringHelper.GetString(
            "DownloadsTaskDetailsLoading",
            "Loading task details…");
        IsTaskDetailsLoading = true;
        IsTaskOverviewVisible = true;
        IsTaskFilesVisible = false;
        IsTaskTrackersVisible = false;
        TaskDetailsRequested?.Invoke(this, EventArgs.Empty);

        try
        {
            var result = await _downloadCoordinator.GetDetailsAsync(task.Hash, _cts.Token);
            if (result.IsSuccess && result.Value is not null)
            {
                SelectedTaskDetails = new QbittorrentTorrentDetailsViewModel(task.Torrent, result.Value);
                TaskDetailsStatusText = ResourceStringHelper.GetString(
                    "DownloadsTaskDetailsLoaded",
                    "Details loaded from qBittorrent.");
            }
            else if (!result.IsCancelled)
            {
                TaskDetailsErrorMessage = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsTaskDetailsFailed",
                    "Could not load task details.");
                TaskDetailsStatusText = TaskDetailsErrorMessage;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TaskDetailsErrorMessage = ex.Message;
            TaskDetailsStatusText = ex.Message;
        }
        finally
        {
            IsTaskDetailsLoading = false;
        }
    }

    private async Task LoadImportSourcesAsync(QbittorrentTorrentViewModel task)
    {
        ImportSources.Clear();
        SelectedImportSource = null;
        ImportStatusText = "";
        if (!task.Torrent.IsCompleted)
        {
            ImportStatusText = ResourceStringHelper.GetString(
                "DownloadsImportIncomplete", "Import is available after the download completes.");
            return;
        }

        var result = await _videoImportService.GetCompatibleSourcesAsync(task.Torrent, _cts.Token);
        if (result.IsSuccess && result.Value is not null)
        {
            foreach (var source in result.Value)
                ImportSources.Add(source);
            SelectedImportSource = ImportSources.FirstOrDefault();
            ImportStatusText = ImportSources.Count == 0
                ? ResourceStringHelper.GetString(
                    "DownloadsImportNoSource", "No configured video source contains this download.")
                : ResourceStringHelper.GetString(
                    "DownloadsImportConfirm", "Choose a source and confirm a read-only library scan.");
        }
        else if (!result.IsCancelled)
        {
            ImportStatusText = result.Error ?? ResourceStringHelper.GetString(
                "DownloadsImportUnavailable", "This download cannot be imported.");
        }
    }

    [RelayCommand]
    private async Task ImportCompletedTaskAsync()
    {
        if (!CanImportSelectedTask || SelectedTask is null || SelectedImportSource is null)
            return;
        IsImporting = true;
        ErrorMessage = null;
        try
        {
            var result = await _videoImportService.ImportCompletedTaskAsync(
                SelectedTask.Torrent,
                SelectedImportSource.Id,
                _cts.Token);
            if (result.IsSuccess)
            {
                ImportStatusText = ResourceStringHelper.FormatString(
                    "DownloadsImportSuccess",
                    "Scanned {0} videos into {1}.",
                    result.Value?.VideoCount ?? 0,
                    SelectedImportSource.Name);
            }
            else if (!result.IsCancelled)
            {
                ImportStatusText = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsImportFailed", "The video source scan failed.");
                ErrorMessage = result.Error;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ImportStatusText = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void SelectTaskOverview()
    {
        IsTaskOverviewVisible = true;
        IsTaskFilesVisible = false;
        IsTaskTrackersVisible = false;
    }

    [RelayCommand]
    private void SelectTaskFiles()
    {
        IsTaskOverviewVisible = false;
        IsTaskFilesVisible = true;
        IsTaskTrackersVisible = false;
    }

    [RelayCommand]
    private void SelectTaskTrackers()
    {
        IsTaskOverviewVisible = false;
        IsTaskFilesVisible = false;
        IsTaskTrackersVisible = true;
    }

    [RelayCommand]
    private async Task CancelTaskAsync(QbittorrentTorrentViewModel task)
    {
        if (task is null || !task.CanPause)
            return;
        var result = await _downloadCoordinator.PauseAsync(task.Hash, _cts.Token);
        ShowTaskActionResult(result);
    }

    [RelayCommand]
    private async Task ResumeTaskAsync(QbittorrentTorrentViewModel task)
    {
        if (task is null || !task.CanResume)
            return;
        var result = await _downloadCoordinator.ResumeAsync(task.Hash, _cts.Token);
        ShowTaskActionResult(result);
    }

    [RelayCommand]
    private async Task RemoveTaskAsync(QbittorrentTorrentViewModel task)
    {
        if (task is null || !task.CanDelete)
            return;
        await DeleteTaskAsync(task);
    }

    [RelayCommand]
    private Task CancelSelectedTaskAsync() =>
        SelectedTask is null ? Task.CompletedTask : CancelTaskAsync(SelectedTask);

    [RelayCommand]
    private Task ResumeSelectedTaskAsync() =>
        SelectedTask is null ? Task.CompletedTask : ResumeTaskAsync(SelectedTask);

    [RelayCommand]
    private Task OpenSelectedTaskLocationAsync() =>
        SelectedTask is null ? Task.CompletedTask : OpenTaskLocationAsync(SelectedTask);

    [RelayCommand]
    private Task DeleteSelectedTaskAsync() =>
        SelectedTask is null ? Task.CompletedTask : DeleteTaskAsync(SelectedTask);

    private async Task DeleteTaskAsync(QbittorrentTorrentViewModel task)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString(
                "DownloadsDeleteTaskTitle",
                "Delete download task?"),
            ResourceStringHelper.FormatString(
                "DownloadsDeleteTaskMessage",
                "Remove '{0}' from qBittorrent? Downloaded files will be kept.",
                task.Name),
            ResourceStringHelper.GetString("DownloadsDeleteTaskButtonText", "Delete task"),
            ResourceStringHelper.GetString("DownloadsCancelButton", "Cancel"));
        if (!confirmed)
            return;

        var result = await _downloadCoordinator.DeleteAsync(task.Hash, false, _cts.Token);
        ShowTaskActionResult(result);
    }

    [RelayCommand]
    private async Task OpenTaskLocationAsync(QbittorrentTorrentViewModel task)
    {
        if (task is null || string.IsNullOrWhiteSpace(task.LocationPath))
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "DownloadsLocationUnavailable",
                "The qBittorrent save path is unavailable.");
            return;
        }

        var result = await _fileRevealService.RevealInFileExplorerAsync(
            task.LocationPath,
            _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            ErrorMessage = result.Error ?? ResourceStringHelper.GetString(
                "DownloadsLocationOpenFailed",
                "Could not open the task location.");
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsSavingSettings)
            return;

        IsSavingSettings = true;
        ErrorMessage = null;
        try
        {
            if (!TryBuildMonoTorrentSettings(out var monoTorrentSettings))
                return;

            if (!string.IsNullOrWhiteSpace(monoTorrentSettings.DownloadRootPath))
            {
                var rootIssue = await MonoTorrentDownloadRootPolicy.CheckWritableAsync(
                    monoTorrentSettings.DownloadRootPath,
                    _cts.Token);
                if (rootIssue is not null)
                {
                    ErrorMessage = GetMonoTorrentDownloadRootIssueMessage(rootIssue.Value);
                    return;
                }
            }

            var existing = await _credentialStore.LoadAsync(_cts.Token);
            var credentials = BuildCredentials(existing);
            var settings = new QbittorrentSettings
            {
                BaseUrl = ServerUrl.Trim(),
                DefaultSavePath = DefaultSavePath.Trim(),
                DefaultCategory = DefaultCategory.Trim(),
                AddPaused = AddPaused,
            };
            _settingsService.Set(
                value => value.DownloadBackend,
                SelectedBackendOption?.Kind ?? DownloadBackendKind.MonoTorrent);
            _settingsService.Set(s => s.MonoTorrentSettings, monoTorrentSettings);
            _settingsService.Set(s => s.QbittorrentSettings, settings);
            await _settingsService.SaveAsync();
            if (string.IsNullOrWhiteSpace(credentials.Username)
                && string.IsNullOrWhiteSpace(credentials.Password)
                && string.IsNullOrWhiteSpace(credentials.ApiKey))
            {
                await _credentialStore.DeleteAsync(_cts.Token);
                CredentialStatusText = ResourceStringHelper.GetString(
                    "DownloadsCredentialsMissing",
                    "Not configured");
            }
            else
            {
                await _credentialStore.SaveAsync(credentials, _cts.Token);
                CredentialStatusText = ResourceStringHelper.GetString(
                    "DownloadsCredentialsConfigured",
                    "Configured");
            }
            PasswordDraft = "";
            ApiKeyDraft = "";
            TaskStatusText = ResourceStringHelper.GetString(
                "DownloadsSettingsSaved",
                "Download settings saved.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSavingSettings = false;
        }
    }

    [RelayCommand]
    private async Task BrowseMonoTorrentDownloadRootAsync()
    {
        try
        {
            var selected = await _dialogService.OpenFolderPickerAsync();
            if (!string.IsNullOrWhiteSpace(selected))
                MonoTorrentDownloadRootPath = selected;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ResetMonoTorrentDownloadRoot() =>
        MonoTorrentDownloadRootPath = MonoTorrentDownloadRootPolicy.DefaultPath;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTestingConnection)
            return;

        IsTestingConnection = true;
        ErrorMessage = null;
        try
        {
            var existing = await _credentialStore.LoadAsync(_cts.Token);
            var result = await _qbittorrentClient.TestConnectionAsync(
                new QbittorrentSettings
                {
                    BaseUrl = ServerUrl.Trim(),
                    DefaultSavePath = DefaultSavePath.Trim(),
                    DefaultCategory = DefaultCategory.Trim(),
                    AddPaused = AddPaused,
                },
                BuildCredentials(existing),
                _cts.Token);
            if (result.IsSuccess)
            {
                TaskStatusText = ResourceStringHelper.FormatString(
                    "DownloadsConnectionSuccess",
                    "Connected to qBittorrent {0} (WebAPI {1}).",
                    result.Value!.ApplicationVersion,
                    result.Value.WebApiVersion);
            }
            else if (!result.IsCancelled)
            {
                ErrorMessage = result.Error;
                TaskStatusText = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsConnectionFailed",
                    "qBittorrent connection failed.");
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private async Task ClearCredentialsAsync()
    {
        try
        {
            await _credentialStore.DeleteAsync(_cts.Token);
            Username = "";
            PasswordDraft = "";
            ApiKeyDraft = "";
            CredentialStatusText = ResourceStringHelper.GetString(
                "DownloadsCredentialsMissing",
                "Not configured");
            TaskStatusText = ResourceStringHelper.GetString(
                "DownloadsCredentialsCleared",
                "qBittorrent credentials cleared.");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task RefreshTasksCoreAsync()
    {
        if (!await _refreshGate.WaitAsync(0, _cts.Token))
            return;
        try
        {
            if (IsBuiltInBackend)
            {
                var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
                UpdateBuiltInTasks(downloadManager.GetTasks());
                TaskStatusText = ResourceStringHelper.FormatString(
                    "DownloadsMonoTorrentTaskCount",
                    "MonoTorrent has {0} tasks.",
                    BuiltInTasks.Count);
                return;
            }

            var result = await _downloadCoordinator.RefreshAsync(_cts.Token);
            if (result.IsSuccess)
            {
                UpdateTasks(result.Value ?? []);
                TaskStatusText = ResourceStringHelper.FormatString(
                    "DownloadsTaskCount",
                    "{0} qBittorrent tasks.",
                    Tasks.Count);
            }
            else if (!result.IsCancelled)
            {
                TaskStatusText = result.Error ?? ResourceStringHelper.GetString(
                    "DownloadsRefreshFailed",
                    "Could not refresh qBittorrent tasks.");
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void OnTasksChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            UpdateTasks(_downloadCoordinator.GetTasks());
        else
            _dispatcherQueue.TryEnqueue(() => UpdateTasks(_downloadCoordinator.GetTasks()));
    }

    private void OnSubscriptionsChanged(object? sender, EventArgs e)
    {
        void Update() => UpdateSubscriptions(_subscriptionService.GetSubscriptions());
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            Update();
        else
            _dispatcherQueue.TryEnqueue(Update);
    }

    private void OnBuiltInTasksChanged(object? sender, EventArgs e)
    {
        var downloadManager = _resolvedNyaaDownloadManager;
        if (downloadManager is null)
            return;

        void Update() => UpdateBuiltInTasks(downloadManager.GetTasks());
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            Update();
        else
            _dispatcherQueue.TryEnqueue(Update);
    }

    private void UpdateTasks(IReadOnlyList<QbittorrentTorrent> tasks)
    {
        var selectedHash = SelectedTask?.Hash;
        Tasks = new ObservableCollection<QbittorrentTorrentViewModel>(
            tasks.OrderByDescending(task => task.AddedAt).Select(task => new QbittorrentTorrentViewModel(task)));
        SelectedTask = string.IsNullOrWhiteSpace(selectedHash)
            ? SelectedTask
            : Tasks.FirstOrDefault(task => task.Hash.Equals(selectedHash, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateBuiltInTasks(IReadOnlyList<NyaaDownloadTaskSnapshot> tasks)
    {
        var next = tasks
            .OrderByDescending(task => task.CreatedAt)
            .ToList();
        var nextTaskIds = next
            .Select(task => task.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = BuiltInTasks.Count - 1; index >= 0; index--)
        {
            if (!nextTaskIds.Contains(BuiltInTasks[index].TaskId))
                BuiltInTasks.RemoveAt(index);
        }

        for (var index = 0; index < next.Count; index++)
        {
            var task = next[index];
            var currentIndex = FindBuiltInTaskIndex(task.TaskId);
            if (currentIndex < 0)
            {
                BuiltInTasks.Insert(index, new NyaaDownloadTaskItemViewModel(task));
                continue;
            }

            if (currentIndex != index)
                BuiltInTasks.Move(currentIndex, index);

            BuiltInTasks[index].Update(task);
        }
    }

    private int FindBuiltInTaskIndex(string taskId)
    {
        for (var index = 0; index < BuiltInTasks.Count; index++)
        {
            if (BuiltInTasks[index].TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private void UpdateSubscriptions(IReadOnlyList<NyaaVideoSubscription> subscriptions)
    {
        Subscriptions = new ObservableCollection<NyaaSubscriptionItemViewModel>(
            subscriptions.Select(subscription => new NyaaSubscriptionItemViewModel(subscription)));
        foreach (var item in Subscriptions.Where(item =>
                     !item.IsLegacy
                     && item.PosterImage is null
                     && !string.IsNullOrWhiteSpace(item.Subscription.PosterUrl)))
        {
            _ = RefreshSubscriptionArtworkAsync(item.Key);
        }
        SubscriptionStatusText = Subscriptions.Count == 0
            ? ResourceStringHelper.GetString(
                "DownloadsSubscriptionsEmptyStatus",
                "Subscriptions created from Video discovery will appear here.")
            : ResourceStringHelper.FormatString(
                "DownloadsSubscriptionsCount",
                "{0} subscriptions.",
                Subscriptions.Count);
    }

    private async Task RefreshSubscriptionArtworkAsync(string key)
    {
        if (!_subscriptionArtworkRefreshes.Add(key))
            return;
        try
        {
            await _subscriptionService.RefreshArtworkAsync(key, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch
        {
            // A missing/expired cover is non-fatal; the fixed-size placeholder remains visible.
        }
        finally
        {
            _subscriptionArtworkRefreshes.Remove(key);
        }
    }

    private void LoadSettingsDraft()
    {
        SelectedBackendOption = BackendOptions.FirstOrDefault(option =>
            option.Kind == _settingsService.Current.DownloadBackend)
            ?? BackendOptions[0];
        var settings = _settingsService.Current.QbittorrentSettings;
        ServerUrl = settings.BaseUrl;
        DefaultSavePath = settings.DefaultSavePath;
        DefaultCategory = settings.DefaultCategory;
        AddPaused = settings.AddPaused;
        var monoTorrent = (_settingsService.Current.MonoTorrentSettings ?? new MonoTorrentSettings())
            .Normalize();
        MonoTorrentDownloadRootPath = string.IsNullOrWhiteSpace(monoTorrent.DownloadRootPath)
            ? MonoTorrentDownloadRootPolicy.DefaultPath
            : monoTorrent.DownloadRootPath;
        MonoTorrentAdditionalTrackersText = string.Join(Environment.NewLine, monoTorrent.AdditionalTrackers);
        MonoTorrentListenPort = monoTorrent.ListenPort;
        MonoTorrentPortForwardingEnabled = monoTorrent.EnablePortForwarding;
        MonoTorrentDhtEnabled = monoTorrent.EnableDht;
        MonoTorrentPeerExchangeEnabled = monoTorrent.EnablePeerExchange;
        MonoTorrentLocalPeerDiscoveryEnabled = monoTorrent.EnableLocalPeerDiscovery;
        MonoTorrentMaximumConnections = monoTorrent.MaximumConnections;
        MonoTorrentMaximumConnectionsPerTorrent = monoTorrent.MaximumConnectionsPerTorrent;
        MonoTorrentMaximumHalfOpenConnections = monoTorrent.MaximumHalfOpenConnections;
        MonoTorrentMaximumOpenFiles = monoTorrent.MaximumOpenFiles;
        MonoTorrentDownloadRateLimitKiB = monoTorrent.DownloadRateLimitKiB;
        MonoTorrentUploadRateLimitKiB = monoTorrent.UploadRateLimitKiB;
        MonoTorrentUploadSlotsPerTorrent = monoTorrent.UploadSlotsPerTorrent;
        PasswordDraft = "";
        ApiKeyDraft = "";
        var credentials = _credentialStore.LoadAsync(_cts.Token).GetAwaiter().GetResult();
        Username = credentials?.Username ?? "";
        CredentialStatusText = credentials is null
            ? ResourceStringHelper.GetString("DownloadsCredentialsMissing", "Not configured")
            : ResourceStringHelper.GetString("DownloadsCredentialsConfigured", "Configured");
    }

    private bool TryBuildMonoTorrentSettings(out MonoTorrentSettings settings)
    {
        if (!MonoTorrentSettings.TryNormalizeDownloadRootPath(
                MonoTorrentDownloadRootPath,
                out var normalizedDownloadRoot))
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "DownloadsMonoTorrentDownloadRootNotAbsolute",
                "Choose an absolute download folder path.");
            settings = new MonoTorrentSettings();
            return false;
        }

        if (MonoTorrentDownloadRootPolicy.PathsEqual(
                normalizedDownloadRoot,
                MonoTorrentDownloadRootPolicy.DefaultPath))
        {
            // Empty is the durable marker for the default path. This keeps old
            // settings compatible if the Windows profile location changes.
            normalizedDownloadRoot = "";
        }

        var trackers = MonoTorrentAdditionalTrackersText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (trackers.Length > MonoTorrentSettings.MaximumAdditionalTrackerCount)
        {
            ErrorMessage = ResourceStringHelper.FormatString(
                "DownloadsMonoTorrentTooManyTrackers",
                "Configure no more than {0} additional trackers.",
                MonoTorrentSettings.MaximumAdditionalTrackerCount);
            settings = new MonoTorrentSettings();
            return false;
        }

        var normalizedTrackers = new List<string>();
        var seenTrackers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tracker in trackers)
        {
            if (!MonoTorrentSettings.TryNormalizeTrackerUrl(tracker, out var normalized))
            {
                var displayValue = tracker.Length <= 120 ? tracker : $"{tracker[..120]}…";
                ErrorMessage = ResourceStringHelper.FormatString(
                    "DownloadsMonoTorrentInvalidTracker",
                    "Invalid tracker URL: {0}",
                    displayValue);
                settings = new MonoTorrentSettings();
                return false;
            }

            if (seenTrackers.Add(normalized))
                normalizedTrackers.Add(normalized);
        }

        settings = new MonoTorrentSettings
        {
            DownloadRootPath = normalizedDownloadRoot,
            AdditionalTrackers = normalizedTrackers,
            ListenPort = MonoTorrentListenPort,
            EnablePortForwarding = MonoTorrentPortForwardingEnabled,
            EnableDht = MonoTorrentDhtEnabled,
            EnablePeerExchange = MonoTorrentPeerExchangeEnabled,
            EnableLocalPeerDiscovery = MonoTorrentLocalPeerDiscoveryEnabled,
            MaximumConnections = MonoTorrentMaximumConnections,
            MaximumConnectionsPerTorrent = MonoTorrentMaximumConnectionsPerTorrent,
            MaximumHalfOpenConnections = MonoTorrentMaximumHalfOpenConnections,
            MaximumOpenFiles = MonoTorrentMaximumOpenFiles,
            DownloadRateLimitKiB = MonoTorrentDownloadRateLimitKiB,
            UploadRateLimitKiB = MonoTorrentUploadRateLimitKiB,
            UploadSlotsPerTorrent = MonoTorrentUploadSlotsPerTorrent,
        }.Normalize();
        return true;
    }

    private static string GetMonoTorrentDownloadRootIssueMessage(
        MonoTorrentDownloadRootIssue issue) => issue switch
    {
        MonoTorrentDownloadRootIssue.NotAbsolute => ResourceStringHelper.GetString(
            "DownloadsMonoTorrentDownloadRootNotAbsolute",
            "Choose an absolute download folder path."),
        MonoTorrentDownloadRootIssue.CreateFailed => ResourceStringHelper.GetString(
            "DownloadsMonoTorrentDownloadRootCreateFailed",
            "The download folder could not be created. Check the drive and permissions."),
        _ => ResourceStringHelper.GetString(
            "DownloadsMonoTorrentDownloadRootNotWritable",
            "The download folder is not writable."),
    };

    private async Task<INyaaDownloadManager> GetNyaaDownloadManagerAsync(CancellationToken cancellationToken)
    {
        Task<INyaaDownloadManager> task;
        lock (_nyaaDownloadManagerSync)
        {
            _nyaaDownloadManagerTask ??= Task.Run(() => _nyaaDownloadManager.Value);
            task = _nyaaDownloadManagerTask;
        }

        var downloadManager = await task.WaitAsync(cancellationToken);
        _resolvedNyaaDownloadManager = downloadManager;
        if (!_disposed && !_nyaaTasksSubscribed)
        {
            downloadManager.TasksChanged += OnBuiltInTasksChanged;
            _nyaaTasksSubscribed = true;
        }

        return downloadManager;
    }

    private QbittorrentCredentials BuildCredentials(QbittorrentCredentials? existing)
    {
        var passwordEntered = !string.IsNullOrWhiteSpace(PasswordDraft);
        var apiKeyEntered = !string.IsNullOrWhiteSpace(ApiKeyDraft);
        return new QbittorrentCredentials(
            Username.Trim(),
            passwordEntered ? PasswordDraft : existing?.Password ?? "",
            apiKeyEntered ? ApiKeyDraft.Trim() : passwordEntered ? "" : existing?.ApiKey ?? "");
    }

    private void ShowTaskActionResult(Niratan.Models.Common.Result result)
    {
        if (!result.IsSuccess && !result.IsCancelled)
            ErrorMessage = result.Error;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _downloadCoordinator.TasksChanged -= OnTasksChanged;
        _subscriptionService.SubscriptionsChanged -= OnSubscriptionsChanged;
        if (_nyaaTasksSubscribed && _resolvedNyaaDownloadManager is not null)
            _resolvedNyaaDownloadManager.TasksChanged -= OnBuiltInTasksChanged;
        TaskDetailsRequested = null;
        _cts.Cancel();
        _cts.Dispose();
        _refreshGate.Dispose();
    }
}
