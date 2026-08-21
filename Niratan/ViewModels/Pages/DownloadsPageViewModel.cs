using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public sealed record DownloadBackendOption(DownloadBackendKind Kind, string DisplayName);

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
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _nyaaDownloadManagerSync = new();
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
    public partial bool IsSettingsVisible { get; set; }

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
    public partial ObservableCollection<NyaaDownloadTaskSnapshot> BuiltInTasks { get; set; } = [];

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
    [NotifyPropertyChangedFor(nameof(IsBuiltInBackend))]
    [NotifyPropertyChangedFor(nameof(IsQbittorrentBackend))]
    [NotifyPropertyChangedFor(nameof(BackendDescription))]
    [NotifyPropertyChangedFor(nameof(DownloadActionText))]
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
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBuiltInBackend => SelectedBackendOption?.Kind == DownloadBackendKind.MonoTorrent;
    public bool IsQbittorrentBackend => SelectedBackendOption?.Kind == DownloadBackendKind.Qbittorrent;
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
        IVideoDownloadImportService videoImportService)
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
        _initialized = true;
        await RefreshTasksAsync();
    }

    [RelayCommand]
    private void SelectDiscovery()
    {
        IsDiscoveryVisible = true;
        IsTasksVisible = false;
        IsSettingsVisible = false;
    }

    [RelayCommand]
    private async Task SelectTasksAsync()
    {
        IsDiscoveryVisible = false;
        IsTasksVisible = true;
        IsSettingsVisible = false;
        await RefreshTasksAsync();
    }

    [RelayCommand]
    private void SelectSettings()
    {
        IsDiscoveryVisible = false;
        IsTasksVisible = false;
        IsSettingsVisible = true;
        LoadSettingsDraft();
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
    private async Task PauseBuiltInTaskAsync(NyaaDownloadTaskSnapshot task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        await downloadManager.PauseAsync(task.TaskId);
    }

    [RelayCommand]
    private async Task ResumeBuiltInTaskAsync(NyaaDownloadTaskSnapshot task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        await downloadManager.ResumeAsync(task.TaskId);
    }

    [RelayCommand]
    private async Task CancelBuiltInTaskAsync(NyaaDownloadTaskSnapshot task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        downloadManager.Cancel(task.TaskId);
    }

    [RelayCommand]
    private async Task RetryBuiltInTaskAsync(NyaaDownloadTaskSnapshot task)
    {
        if (task is null)
            return;

        var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
        downloadManager.Retry(task.TaskId);
    }

    [RelayCommand]
    private async Task RemoveBuiltInTaskAsync(NyaaDownloadTaskSnapshot task)
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
    private async Task OpenBuiltInTaskFolderAsync(NyaaDownloadTaskSnapshot task)
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

    private void UpdateBuiltInTasks(IReadOnlyList<NyaaDownloadTaskSnapshot> tasks) =>
        BuiltInTasks = new ObservableCollection<NyaaDownloadTaskSnapshot>(
            tasks.OrderByDescending(task => task.CreatedAt));

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
        PasswordDraft = "";
        ApiKeyDraft = "";
        var credentials = _credentialStore.LoadAsync(_cts.Token).GetAwaiter().GetResult();
        Username = credentials?.Username ?? "";
        CredentialStatusText = credentials is null
            ? ResourceStringHelper.GetString("DownloadsCredentialsMissing", "Not configured")
            : ResourceStringHelper.GetString("DownloadsCredentialsConfigured", "Configured");
    }

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
        if (_nyaaTasksSubscribed && _resolvedNyaaDownloadManager is not null)
            _resolvedNyaaDownloadManager.TasksChanged -= OnBuiltInTasksChanged;
        TaskDetailsRequested = null;
        _cts.Cancel();
        _cts.Dispose();
        _refreshGate.Dispose();
    }
}
