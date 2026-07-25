using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Niratan.Helpers;
using Niratan.Models.Nyaa;
using Niratan.Services.Nyaa;
using Niratan.Services.UI;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Dialogs;

public sealed record NyaaDownloadViewOption(string Code, string DisplayName);

public partial class NyaaImportDialogViewModel : ObservableObject, IDisposable
{
    private readonly INyaaClient _nyaaClient;
    private readonly INyaaDownloadManager _downloadManager;
    private readonly IFileRevealService _fileRevealService;
    private readonly INotificationService _notificationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<NyaaTorrentItem> _allSearchResults = [];

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
    public partial string ResultSummary { get; set; } = ResourceStringHelper.GetString(
        "NyaaInitialSummary",
        "Search Nyaa for a resource pack.");

    [ObservableProperty]
    public partial ObservableCollection<NyaaTorrentItemViewModel> Results { get; set; } = [];

    [ObservableProperty]
    public partial NyaaDownloadViewOption SelectedResultFilter { get; set; }

    [ObservableProperty]
    public partial NyaaDownloadViewOption SelectedResultSort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoDownloads))]
    public partial ObservableCollection<NyaaDownloadTaskSnapshot> Downloads { get; set; } = [];

    public bool NoDownloads => Downloads.Count == 0;

    [ObservableProperty]
    public partial NyaaDownloadViewOption SelectedDownloadFilter { get; set; }

    [ObservableProperty]
    public partial NyaaDownloadViewOption SelectedDownloadSort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadTabHeader))]
    public partial int TotalDownloadCount { get; set; }

    public string DownloadTabHeader => ResourceStringHelper.FormatString(
        "NyaaDownloadsTabHeader",
        "Downloads ({0})",
        TotalDownloadCount);

    public IReadOnlyList<NyaaSearchCategory> Categories { get; } =
    [
        new("0_0", ResourceStringHelper.GetString("NyaaCategoryAll", "All categories")),
        new("3_0", ResourceStringHelper.GetString("NyaaCategoryLiterature", "Literature")),
        new("2_0", ResourceStringHelper.GetString("NyaaCategoryAudio", "Audio")),
        new("1_0", ResourceStringHelper.GetString("NyaaCategoryAnime", "Anime")),
        new("4_0", ResourceStringHelper.GetString("NyaaCategoryLiveAction", "Live action")),
    ];

    public IReadOnlyList<NyaaDownloadViewOption> DownloadFilters { get; } =
    [
        new("all", ResourceStringHelper.GetString("NyaaDownloadFilterAll", "All tasks")),
        new("active", ResourceStringHelper.GetString("NyaaDownloadFilterActive", "Active")),
        new("paused", ResourceStringHelper.GetString("NyaaDownloadFilterPaused", "Paused")),
        new("completed", ResourceStringHelper.GetString("NyaaDownloadFilterCompleted", "Completed")),
        new("failed", ResourceStringHelper.GetString("NyaaDownloadFilterFailed", "Failed / cancelled")),
    ];

    public IReadOnlyList<NyaaDownloadViewOption> ResultFilters { get; } =
    [
        new("all", ResourceStringHelper.GetString("NyaaResultFilterAll", "All results")),
        new("trusted", ResourceStringHelper.GetString("NyaaResultFilterTrusted", "Trusted only")),
        new("original", ResourceStringHelper.GetString("NyaaResultFilterOriginal", "Exclude remakes")),
        new("seeded", ResourceStringHelper.GetString("NyaaResultFilterSeeded", "Has seeders")),
    ];

    public IReadOnlyList<NyaaDownloadViewOption> ResultSortOptions { get; } =
    [
        new("seeders", ResourceStringHelper.GetString("NyaaResultSortSeeders", "Most seeders")),
        new("newest", ResourceStringHelper.GetString("NyaaResultSortNewest", "Newest")),
        new("downloads", ResourceStringHelper.GetString("NyaaResultSortDownloads", "Most downloads")),
        new("smallest", ResourceStringHelper.GetString("NyaaResultSortSmallest", "Smallest")),
        new("largest", ResourceStringHelper.GetString("NyaaResultSortLargest", "Largest")),
        new("title", ResourceStringHelper.GetString("NyaaResultSortTitle", "Title")),
    ];

    public IReadOnlyList<NyaaDownloadViewOption> DownloadSortOptions { get; } =
    [
        new("newest", ResourceStringHelper.GetString("NyaaDownloadSortNewest", "Newest first")),
        new("oldest", ResourceStringHelper.GetString("NyaaDownloadSortOldest", "Oldest first")),
        new("status", ResourceStringHelper.GetString("NyaaDownloadSortStatus", "Status")),
        new("progress", ResourceStringHelper.GetString("NyaaDownloadSortProgress", "Progress")),
        new("title", ResourceStringHelper.GetString("NyaaDownloadSortTitle", "Title")),
    ];

    public bool CanSearch => !IsSearching && !string.IsNullOrWhiteSpace(SearchQuery);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public NyaaImportDialogViewModel(
        INyaaClient nyaaClient,
        INyaaDownloadManager downloadManager,
        IFileRevealService fileRevealService,
        INotificationService notificationService)
    {
        _nyaaClient = nyaaClient;
        _downloadManager = downloadManager;
        _fileRevealService = fileRevealService;
        _notificationService = notificationService;
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcherQueue = null;
        }
        SelectedCategory = Categories[0];
        SelectedResultFilter = ResultFilters[0];
        SelectedResultSort = ResultSortOptions[0];
        SelectedDownloadFilter = DownloadFilters[0];
        SelectedDownloadSort = DownloadSortOptions[0];
        _downloadManager.TasksChanged += OnDownloadTasksChanged;
        RefreshDownloads();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        IsSearching = true;
        ErrorMessage = null;
        try
        {
            var result = await _nyaaClient.SearchAsync(
                new NyaaSearchRequest(SearchQuery, SelectedCategory.Code),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error;
                return;
            }

            _allSearchResults = result.Value!;
            RefreshSearchResults();
            if (_allSearchResults.Count == 0)
            {
                ResultSummary = ResourceStringHelper.GetString(
                    "NyaaNoResults",
                    "No matching torrents.");
            }
            else if (Results.Count == _allSearchResults.Count)
            {
                ResultSummary = ResourceStringHelper.FormatString(
                    "NyaaResultSummary",
                    "{0} results. Verify the release contents before downloading.",
                    Results.Count);
            }
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
    private void DownloadAndImport(NyaaTorrentItemViewModel row)
    {
        if (!row.CanDownload)
            return;

        _downloadManager.Enqueue(row.Item);
        row.IsImported = true;
        row.Status = ResourceStringHelper.GetString("NyaaStatusQueued", "Added to downloads");
    }

    [RelayCommand]
    private Task PauseDownloadAsync(NyaaDownloadTaskSnapshot task) =>
        _downloadManager.PauseAsync(task.TaskId);

    [RelayCommand]
    private Task ResumeDownloadAsync(NyaaDownloadTaskSnapshot task) =>
        _downloadManager.ResumeAsync(task.TaskId);

    [RelayCommand]
    private void CancelDownload(NyaaDownloadTaskSnapshot task) =>
        _downloadManager.Cancel(task.TaskId);

    [RelayCommand]
    private void RetryDownload(NyaaDownloadTaskSnapshot task) =>
        _downloadManager.Retry(task.TaskId);

    [RelayCommand]
    private void RemoveDownload(NyaaDownloadTaskSnapshot task) =>
        _downloadManager.Remove(task.TaskId);

    [RelayCommand]
    private async Task OpenDownloadFolderAsync(NyaaDownloadTaskSnapshot task)
    {
        if (task.DownloadRootPath is null)
            return;
        var result = await _fileRevealService.RevealInFileExplorerAsync(task.DownloadRootPath);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error ?? "Could not open the download folder.");
    }

    private void OnDownloadTasksChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            RefreshDownloads();
        else
            _dispatcherQueue.TryEnqueue(RefreshDownloads);
    }

    private void RefreshDownloads()
    {
        var tasks = _downloadManager.GetTasks();
        TotalDownloadCount = tasks.Count;
        var filtered = tasks.Where(MatchesDownloadFilter);
        filtered = SelectedDownloadSort?.Code switch
        {
            "oldest" => filtered.OrderBy(task => task.CreatedAt),
            "status" => filtered
                .OrderBy(task => task.State)
                .ThenByDescending(task => task.CreatedAt),
            "progress" => filtered
                .OrderByDescending(task => task.ProgressPercent)
                .ThenByDescending(task => task.CreatedAt),
            "title" => filtered
                .OrderBy(task => task.Item.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderByDescending(task => task.CreatedAt),
        };
        Downloads = new ObservableCollection<NyaaDownloadTaskSnapshot>(filtered);
    }

    private void RefreshSearchResults()
    {
        var filtered = _allSearchResults.Where(item =>
            SelectedResultFilter?.Code switch
            {
                "trusted" => item.IsTrusted,
                "original" => !item.IsRemake,
                "seeded" => item.Seeders > 0,
                _ => true,
            });
        filtered = SelectedResultSort?.Code switch
        {
            "newest" => filtered.OrderByDescending(item => item.PublishedAt),
            "downloads" => filtered.OrderByDescending(item => item.Downloads),
            "smallest" => filtered.OrderBy(item => item.SizeBytes),
            "largest" => filtered.OrderByDescending(item => item.SizeBytes),
            "title" => filtered.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered
                .OrderByDescending(item => item.Seeders)
                .ThenByDescending(item => item.PublishedAt),
        };
        Results = new ObservableCollection<NyaaTorrentItemViewModel>(
            filtered.Select(item => new NyaaTorrentItemViewModel(item)));
        if (_allSearchResults.Count > 0)
        {
            ResultSummary = ResourceStringHelper.FormatString(
                "NyaaFilteredResultSummary",
                "Showing {0} of {1} results.",
                Results.Count,
                _allSearchResults.Count);
        }
    }

    private bool MatchesDownloadFilter(NyaaDownloadTaskSnapshot task) =>
        SelectedDownloadFilter?.Code switch
        {
            "active" => task.State is NyaaDownloadTaskState.Queued
                or NyaaDownloadTaskState.Downloading
                or NyaaDownloadTaskState.Importing,
            "paused" => task.State == NyaaDownloadTaskState.Paused,
            "completed" => task.State == NyaaDownloadTaskState.Completed,
            "failed" => task.State is NyaaDownloadTaskState.Failed
                or NyaaDownloadTaskState.Cancelled,
            _ => true,
        };

    partial void OnSelectedDownloadFilterChanged(NyaaDownloadViewOption value) =>
        RefreshDownloads();

    partial void OnSelectedDownloadSortChanged(NyaaDownloadViewOption value) =>
        RefreshDownloads();

    partial void OnSelectedResultFilterChanged(NyaaDownloadViewOption value) =>
        RefreshSearchResults();

    partial void OnSelectedResultSortChanged(NyaaDownloadViewOption value) =>
        RefreshSearchResults();

    public void Dispose()
    {
        _downloadManager.TasksChanged -= OnDownloadTasksChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
