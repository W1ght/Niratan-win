using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Helpers;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.Services.Settings;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public partial class VideoLibraryPageViewModel : ObservableObject,
    IRecipient<VideoLibraryChangedMessage>
{
    private readonly IVideoLibraryService _videoLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IVideoPlayerWindowService _playerWindowService;
    private readonly IVideoThumbnailService _thumbnailService;
    private readonly IFileRevealService _fileRevealService;
    private readonly IVideoLibraryScanCoordinator? _scanCoordinator;
    private readonly IVideoMetadataCoordinator? _metadataCoordinator;
    private readonly ISettingsService? _settingsService;
    private readonly IVideoDiscoveryService? _discoveryService;
    private readonly IVideoResourceSearchService? _resourceSearchService;
    private readonly Lazy<INyaaDownloadManager>? _nyaaDownloadManager;
    private readonly IMessenger _messenger;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource _scanCts = new();
    private List<VideoItem> _allVideos = [];
    private List<VideoCollection> _collections = [];
    private List<VideoLibrarySource> _sources = [];
    private readonly HashSet<string> _selectedVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, VideoLibraryScanProgress> _latestScanProgress = [];
    private readonly Dictionary<Guid, VideoMetadataBatchProgress> _latestMetadataProgress = [];
    private SynchronizationContext? _uiContext;
    private string? _activeFolderPath;
    private string? _activeCollectionId;
    private string? _activeSeriesName;
    private string? _activeTag;
    private bool _isSubscribedToPlayerLibraryChanges;
    private Guid? _lastReloadedMetadataJobId;
    private CancellationTokenSource? _seriesEpisodesCts;
    private Task<INyaaDownloadManager>? _nyaaDownloadManagerTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
    [NotifyPropertyChangedFor(nameof(ShowNoVideos))]
    public partial ObservableCollection<VideoItemViewModel> Videos { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<VideoLibraryFilterRow> FolderFilters { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<VideoLibraryFilterRow> CollectionFilters { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<VideoLibraryFilterRow> TagFilters { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<VideoLibrarySourceSummary> SourceSummaries { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadataTasks))]
    [NotifyPropertyChangedFor(nameof(MetadataTaskCountText))]
    public partial ObservableCollection<VideoMetadataTaskViewModel> MetadataTasks { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHomeContinueWatching))]
    public partial ObservableCollection<VideoItemViewModel> HomeContinueWatching { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHomeRecentlyAdded))]
    public partial ObservableCollection<VideoItemViewModel> HomeRecentlyAdded { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHomeNextEpisodes))]
    public partial ObservableCollection<VideoItemViewModel> HomeNextEpisodes { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeriesCards))]
    public partial ObservableCollection<VideoSeriesViewModel> SeriesCards { get; set; } = new();

    [ObservableProperty]
    public partial bool IsLoadingSeriesEpisodes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeriesEpisodesError))]
    public partial string? SeriesEpisodesError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeriesBrowseView))]
    [NotifyPropertyChangedFor(nameof(IsSeriesDetailView))]
    [NotifyPropertyChangedFor(nameof(IsLibraryBrowseView))]
    public partial VideoSeriesViewModel? SelectedSeries { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<VideoCollectionMembershipOption> ManualCollectionOptions { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedVideo))]
    public partial VideoItemViewModel? SelectedVideo { get; set; }

    [ObservableProperty]
    public partial string SelectedVideoTitleDraft { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedVideoTagsDraft { get; set; } = "";

    [ObservableProperty]
    public partial string SelectedVideoSubtitlePath { get; set; } = "";

    [ObservableProperty]
    public partial string ManualCollectionNameDraft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial int SelectedVideoCount { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<VideoSmartRuleDraft> SmartRuleDrafts { get; set; } = new();

    [ObservableProperty]
    public partial string SmartCollectionDialogTitle { get; set; } = "Create smart collection";

    private string? _editingSmartCollectionId;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial VideoLibrarySortOption SelectedSortOption { get; set; } = VideoLibrarySortOption.Recent;

    [ObservableProperty]
    public partial VideoLibraryView SelectedLibraryView { get; set; } = VideoLibraryView.Home;

    [ObservableProperty]
    public partial VideoLibraryLayoutMode SelectedLayoutMode { get; set; } = VideoLibraryLayoutMode.Posters;

    [ObservableProperty]
    public partial string CurrentViewTitle { get; set; } = GetViewTitle(VideoLibraryView.Home);

    [ObservableProperty]
    public partial string CurrentViewSubtitle { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
    [NotifyPropertyChangedFor(nameof(ShowNoVideos))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogLoading))]
    public partial bool IsContentLoading { get; set; }

    [ObservableProperty]
    public partial bool HasActiveScan { get; set; }

    [ObservableProperty]
    public partial bool IsActiveScanIndeterminate { get; set; }

    [ObservableProperty]
    public partial double ActiveScanProgress { get; set; }

    [ObservableProperty]
    public partial string ActiveScanText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasActiveMetadataRefresh { get; set; }

    [ObservableProperty]
    public partial bool IsMetadataRefreshIndeterminate { get; set; }

    [ObservableProperty]
    public partial double MetadataRefreshProgress { get; set; }

    [ObservableProperty]
    public partial string MetadataRefreshText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasBackgroundMetadataTask { get; set; }

    [ObservableProperty]
    public partial double BackgroundMetadataProgress { get; set; }

    [ObservableProperty]
    public partial string BackgroundMetadataText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMetadataTaskPanelVisible))]
    public partial bool IsMetadataTaskPanelOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartCollectionPreviewRows))]
    public partial string SmartCollectionNameDraft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartCollectionPreviewRows))]
    public partial VideoSmartRuleField SelectedSmartRuleField { get; set; } = VideoSmartRuleField.FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartCollectionPreviewRows))]
    public partial string SmartRuleValueDraft { get; set; } = "";

    public IReadOnlyList<VideoLibrarySortOptionItem> SortOptions { get; } =
    [
        new(VideoLibrarySortOption.Recent, ResourceStringHelper.GetString("VideoLibrarySortRecent", "Recent")),
        new(VideoLibrarySortOption.Title, ResourceStringHelper.GetString("VideoLibrarySortTitle", "Title")),
        new(VideoLibrarySortOption.Progress, ResourceStringHelper.GetString("VideoLibrarySortProgress", "Progress")),
        new(VideoLibrarySortOption.Folder, ResourceStringHelper.GetString("VideoLibrarySortFolder", "Folder")),
    ];

    public IReadOnlyList<VideoLibraryMediaTypeOption> SourceMediaTypeOptions { get; } =
    [
        new(VideoLibraryMediaType.Auto, ResourceStringHelper.GetString("VideoLibrarySourceTypeAuto", "Auto")),
        new(VideoLibraryMediaType.Anime, ResourceStringHelper.GetString("VideoLibrarySourceTypeAnime", "Anime")),
        new(VideoLibraryMediaType.JapaneseDramaTv, ResourceStringHelper.GetString("VideoLibrarySourceTypeDrama", "Japanese Drama / TV")),
        new(VideoLibraryMediaType.Movie, ResourceStringHelper.GetString("VideoLibrarySourceTypeMovie", "Movie")),
    ];

    public IReadOnlyList<VideoSmartRuleFieldOption> AvailableSmartRuleFields { get; } =
    [
        new(VideoSmartRuleField.FileName, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldFileName", "File name")),
        new(VideoSmartRuleField.ParentFolder, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldParentFolder", "Parent folder")),
        new(VideoSmartRuleField.Path, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldPath", "Full path")),
        new(VideoSmartRuleField.Tag, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldTag", "Tag")),
        new(VideoSmartRuleField.HasBoundSubtitle, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldHasBoundSubtitle", "Has subtitle")),
        new(VideoSmartRuleField.PlaybackState, ResourceStringHelper.GetString("VideoLibrarySmartRuleFieldPlaybackState", "Playback state")),
    ];

    public bool NoVideos => !IsContentLoading && Videos.Count == 0;
    public bool ShowNoVideos => NoVideos && IsLibraryBrowseView;
    public bool ShowCatalogLoading => IsContentLoading && IsVideoCatalogView;
    public bool HasSelectedVideo => SelectedVideo != null;
    public bool HasSelection => SelectedVideoCount > 0;
    public bool HasSources => SourceSummaries.Count > 0;
    public bool HasMetadataTasks => MetadataTasks.Count > 0;
    public string MetadataTaskCountText => ResourceStringHelper.FormatString(
        "VideoMetadataTaskCountFormat",
        "{0} background tasks",
        MetadataTasks.Count);
    public bool IsListLayout => SelectedLayoutMode == VideoLibraryLayoutMode.List;
    public bool IsPosterLayout => SelectedLayoutMode == VideoLibraryLayoutMode.Posters;
    public bool IsSmartRuleValueVisible => SelectedSmartRuleField != VideoSmartRuleField.HasBoundSubtitle;
    public bool IsFoldersView => SelectedLibraryView == VideoLibraryView.Folders;
    public bool IsCollectionsView => SelectedLibraryView == VideoLibraryView.Collections;
    public bool IsTagsView => SelectedLibraryView == VideoLibraryView.Tags;
    public bool IsHomeView => SelectedLibraryView == VideoLibraryView.Home;
    public bool IsDiscoverView => SelectedLibraryView == VideoLibraryView.Discover;
    public bool IsSourcesView => SelectedLibraryView == VideoLibraryView.Sources;
    public bool IsVideoCatalogView => !IsSourcesView && !IsDiscoverView;
    public bool IsSeriesBrowseView => SelectedLibraryView == VideoLibraryView.Series && SelectedSeries == null;
    public bool IsSeriesDetailView => SelectedLibraryView == VideoLibraryView.Series && SelectedSeries != null;
    public bool IsLibraryBrowseView => IsVideoCatalogView && !IsHomeView && SelectedLibraryView != VideoLibraryView.Series;
    public bool IsCatalogSearchVisible => IsLibraryBrowseView || IsSeriesBrowseView;
    public bool IsLibraryHeaderVisible => IsCatalogSearchVisible || IsSourcesView;
    public bool IsMetadataTaskPanelVisible => IsSourcesView && IsMetadataTaskPanelOpen;
    public bool HasSeriesCards => SeriesCards.Count > 0;
    public bool HasSeriesEpisodesError => !string.IsNullOrWhiteSpace(SeriesEpisodesError);
    public bool HasHomeContinueWatching => HomeContinueWatching.Count > 0;
    public bool HasHomeNextEpisodes => HomeNextEpisodes.Count > 0;
    public bool HasHomeRecentlyAdded => HomeRecentlyAdded.Count > 0;
    public string HomeMoviesCountText => FormatVideoCount(_allVideos.Count(video =>
        video.LibraryMediaType == VideoLibraryMediaType.Movie || video.CatalogNodeKind == VideoCatalogNodeKind.Movie));
    public string HomeSeriesCountText => FormatVideoCount(_allVideos
        .Where(video => video.CatalogSeriesNodeId.HasValue && video.LibraryMediaType != VideoLibraryMediaType.Anime)
        .Select(video => video.CatalogSeriesNodeId).Distinct().Count());
    public string HomeAnimeCountText => FormatVideoCount(_allVideos.Count(video =>
        video.LibraryMediaType == VideoLibraryMediaType.Anime));
    public string HomeCollectionsCountText => FormatVideoCount(_collections.Count);
    public bool IsOnlineMetadataEnabled =>
        _settingsService?.Current.VideoSettings.Metadata.OnlineConsentAccepted == true;
    public IReadOnlyList<VideoItemViewModel> SmartCollectionPreviewRows
    {
        get
        {
            var rules = BuildSmartRules();
            return rules.Count == 0
                ? []
                : _allVideos
                    .Where(video => Niratan.Services.Video.VideoSmartCollectionMatcher.Matches(video, rules))
                    .Take(5)
                    .Select(video => new VideoItemViewModel(video))
                    .ToList();
        }
    }

    public VideoLibraryPageViewModel(
        IVideoLibraryService videoLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IVideoPlayerWindowService playerWindowService,
        IVideoThumbnailService videoThumbnailService,
        IFileRevealService fileRevealService,
        IMessenger? messenger = null)
        : this(
            videoLibraryService,
            dialogService,
            notificationService,
            playerWindowService,
            videoThumbnailService,
            fileRevealService,
            null,
            null,
            null,
            null,
            null,
            null,
            messenger)
    {
    }

    public VideoLibraryPageViewModel(
        IVideoLibraryService videoLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IVideoPlayerWindowService playerWindowService,
        IVideoThumbnailService videoThumbnailService,
        IFileRevealService fileRevealService,
        IVideoLibraryScanCoordinator? scanCoordinator,
        IMessenger? messenger = null)
        : this(
            videoLibraryService,
            dialogService,
            notificationService,
            playerWindowService,
            videoThumbnailService,
            fileRevealService,
            scanCoordinator,
            null,
            null,
            null,
            null,
            null,
            messenger)
    {
    }

    public VideoLibraryPageViewModel(
        IVideoLibraryService videoLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IVideoPlayerWindowService playerWindowService,
        IVideoThumbnailService videoThumbnailService,
        IFileRevealService fileRevealService,
        IVideoLibraryScanCoordinator? scanCoordinator,
        IVideoMetadataCoordinator? metadataCoordinator,
        ISettingsService? settingsService,
        IVideoDiscoveryService? discoveryService = null,
        IVideoResourceSearchService? resourceSearchService = null,
        Lazy<INyaaDownloadManager>? nyaaDownloadManager = null,
        IMessenger? messenger = null)
    {
        _videoLibraryService = videoLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _playerWindowService = playerWindowService;
        _thumbnailService = videoThumbnailService;
        _fileRevealService = fileRevealService;
        _scanCoordinator = scanCoordinator;
        _metadataCoordinator = metadataCoordinator;
        _settingsService = settingsService;
        _discoveryService = discoveryService;
        _resourceSearchService = resourceSearchService;
        _nyaaDownloadManager = nyaaDownloadManager;
        _messenger = messenger ?? new WeakReferenceMessenger();
        _messenger.RegisterAll(this);
    }

    public async Task InitializeAsync()
    {
        _uiContext ??= SynchronizationContext.Current;
        SubscribeToPlayerLibraryChanges();
        SubscribeToScanProgress();
        SubscribeToMetadataProgress();
        await LoadVideosAsync();
        if (_metadataCoordinator != null)
        {
            foreach (var progress in _metadataCoordinator.ActiveBatchProgress)
                ApplyMetadataBatchProgress(progress);
            await LoadMetadataTasksAsync(CancellationToken.None);
        }
        if (_scanCoordinator != null)
            _ = RefreshSourcesInBackgroundAsync();
    }

    public void OnNavigatedFrom()
    {
        _cts.Cancel();
        _scanCts.Cancel();
        _seriesEpisodesCts?.Cancel();
        UnsubscribeFromPlayerLibraryChanges();
        UnsubscribeFromScanProgress();
        UnsubscribeFromMetadataProgress();
    }

    private async Task RefreshSourcesInBackgroundAsync()
    {
        try
        {
            _scanCts.Cancel();
            _scanCts.Dispose();
            _scanCts = new CancellationTokenSource();
            await _scanCoordinator!.ScanAllAsync(fullScan: false, _scanCts.Token);
            await LoadVideosAsync();
            if (_metadataCoordinator != null && IsOnlineMetadataEnabled)
                await _metadataCoordinator.QueueAllSourcesAsync(forceRefresh: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, "Video library scan failed");
        }
    }

    public void Receive(VideoLibraryChangedMessage message) => _ = LoadVideosAsync();

    public IReadOnlyList<VideoSmartRuleMatchOption> AvailableSmartRuleMatches { get; } =
    [
        new(VideoSmartRuleMatch.Contains, "Contains"),
        new(VideoSmartRuleMatch.Equals, "Equals"),
        new(VideoSmartRuleMatch.IsTrue, "Is true"),
    ];

    public Task OpenResolvedYouTubeAsync(VideoPlaybackLaunchRequest request) =>
        _playerWindowService.OpenAsync(request, _cts.Token);

    public async Task<Result<VideoPlaybackLaunchRequest>> AddResolvedYouTubeSourceAsync(
        ResolvedRemoteVideoSource source)
    {
        var added = await _videoLibraryService.AddRemoteVideoAsync(source, _cts.Token);
        if (!added.IsSuccess)
            return Result<VideoPlaybackLaunchRequest>.Failure(added.Error!, added.ErrorTitle!);

        await LoadVideosAsync();
        return Result<VideoPlaybackLaunchRequest>.Success(
            new VideoPlaybackLaunchRequest(added.Value!, _allVideos.ToList(), source));
    }

    [RelayCommand]
    private async Task ImportVideoAsync()
    {
        var filePath = await _dialogService.OpenFilePickerAsync(".mkv", ".mp4", ".webm", ".avi", ".mov");
        if (filePath == null)
            return;

        var result = await _videoLibraryService.ImportVideoAsync(filePath, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess(
            ResourceStringHelper.GetString("VideoLibraryImportedMessage", "Video imported."),
            ResourceStringHelper.GetString("VideoLibraryImportedTitle", "Video imported"));
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var folderPath = await _dialogService.OpenFolderPickerAsync();
        if (folderPath == null)
            return;

        var result = await _videoLibraryService.ScanFolderAsync(folderPath, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess(
            ResourceStringHelper.FormatString(
                "VideoLibraryFolderScannedMessage",
                "Scanned {0} videos.",
                result.Value!.ImportedCount),
            ResourceStringHelper.GetString("VideoLibraryFolderScannedTitle", "Folder scanned"));
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task RefreshAllSourcesAsync()
    {
        var result = await _videoLibraryService.RefreshAllSourcesAsync(_cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        await LoadVideosAsync();
        if (result.IsSuccess && _metadataCoordinator != null && IsOnlineMetadataEnabled)
            await _metadataCoordinator.QueueAllSourcesAsync(forceRefresh: false, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ScrapeAllMetadataAsync()
    {
        if (_metadataCoordinator == null || !await EnsureOnlineMetadataConsentAsync())
            return;

        await _metadataCoordinator.QueueAllSourcesAsync(forceRefresh: true, CancellationToken.None);
        await LoadMetadataTasksAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleMetadataTasks() => IsMetadataTaskPanelOpen = !IsMetadataTaskPanelOpen;

    [RelayCommand]
    private async Task RefreshMetadataTasksAsync() =>
        await LoadMetadataTasksAsync(CancellationToken.None);

    [RelayCommand]
    private async Task CancelMetadataTaskAsync(VideoMetadataTaskViewModel task)
    {
        if (_metadataCoordinator == null)
            return;

        await _metadataCoordinator.CancelTaskAsync(task.JobId, CancellationToken.None);
        await LoadMetadataTasksAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RetryMetadataTaskAsync(VideoMetadataTaskViewModel task)
    {
        if (_metadataCoordinator == null || !await EnsureOnlineMetadataConsentAsync())
            return;

        await _metadataCoordinator.RetryTaskAsync(task.JobId, CancellationToken.None);
        await LoadMetadataTasksAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RetryFailedMetadataTasksAsync()
    {
        if (_metadataCoordinator == null || !await EnsureOnlineMetadataConsentAsync())
            return;

        await _metadataCoordinator.RetryFailedTasksAsync(CancellationToken.None);
        await LoadMetadataTasksAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void OpenMetadataReview()
    {
        IsMetadataTaskPanelOpen = false;
        SelectLibraryView(nameof(VideoLibraryView.NeedsReview));
    }

    [RelayCommand]
    private async Task RefreshSourceAsync(VideoLibrarySourceSummary summary)
    {
        var result = await _videoLibraryService.RefreshSourceAsync(summary.Source.Id, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        await LoadVideosAsync();
        if (result.IsSuccess && _metadataCoordinator != null && IsOnlineMetadataEnabled
            && Guid.TryParse(summary.Source.Id, out var sourceId))
            await _metadataCoordinator.QueueSourceRefreshAsync(sourceId, forceRefresh: false, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshSourceMetadataAsync(VideoLibrarySourceSummary summary)
    {
        if (_metadataCoordinator == null || !Guid.TryParse(summary.Source.Id, out var sourceId))
            return;
        if (!await EnsureOnlineMetadataConsentAsync())
            return;
        await _metadataCoordinator.QueueSourceRefreshAsync(sourceId, forceRefresh: true, CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleSourceSettings(VideoLibrarySourceSummary summary) =>
        summary.IsSourceSettingsExpanded = !summary.IsSourceSettingsExpanded;

    [RelayCommand]
    private async Task CancelSourceMetadataAsync(VideoLibrarySourceSummary summary)
    {
        if (_metadataCoordinator != null && Guid.TryParse(summary.Source.Id, out var sourceId))
            await _metadataCoordinator.CancelSourceRefreshAsync(sourceId, CancellationToken.None);
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(VideoLibrarySourceSummary summary)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Remove video source",
            $"Remove '{summary.Source.Name}' and its videos from Niratan? Files on disk are kept.");
        if (!confirmed)
            return;

        var result = await _videoLibraryService.RemoveSourceAsync(summary.Source.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task RevealSourceAsync(VideoLibrarySourceSummary summary)
    {
        var result = await _fileRevealService.RevealInFileExplorerAsync(summary.Source.FolderPath, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
    }

    [RelayCommand]
    private async Task RemoveMissingVideosAsync()
    {
        var result = await _videoLibraryService.RemoveMissingVideosAsync(_cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess($"Removed {result.Value} missing videos.");
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task OpenVideoAsync(VideoItemViewModel item)
    {
        await OpenVideoCoreAsync(item, startFromBeginning: false);
    }

    [RelayCommand]
    private async Task OpenVideoFromBeginningAsync(VideoItemViewModel item)
    {
        await OpenVideoCoreAsync(item, startFromBeginning: true);
    }

    private async Task OpenVideoCoreAsync(VideoItemViewModel item, bool startFromBeginning)
    {
        var result = await _videoLibraryService.MarkOpenedAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        var video = startFromBeginning
            ? CloneForPlayback(item.Video, lastPositionSeconds: 0)
            : item.Video;

        var playlist = ResolvePlaybackPlaylist(item.Video);
        var currentIndex = playlist.FindIndex(candidate =>
            string.Equals(candidate.Id, item.Video.Id, StringComparison.OrdinalIgnoreCase));
        var context = new VideoLibraryPlaybackContext(
            playlist.Select(candidate => new VideoLibraryPlaybackQueueEntry(
                    candidate.Id,
                    candidate.CatalogAssetId,
                    candidate.CatalogNodeId))
                .ToImmutableArray(),
            Math.Max(0, currentIndex));
        await _playerWindowService.OpenAsync(
            new VideoPlaybackLaunchRequest(video, playlist, LibraryContext: context),
            _cts.Token);
    }

    private List<VideoItem> ResolvePlaybackPlaylist(VideoItem selected)
    {
        if (IsSeriesDetailView && SelectedSeries != null)
        {
            if (SelectedSeries.ContainsSpecialFeature(selected))
                return [selected];
            if (SelectedSeries.ContainsRegularEpisode(selected))
                return SelectedSeries.RegularEpisodes.Select(item => item.Video).ToList();
        }

        return Videos.Select(row => row.Video).ToList();
    }

    [RelayCommand]
    private async Task DeleteVideoAsync(VideoItemViewModel item)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString("VideoLibraryDeleteTitle", "Delete video"),
            ResourceStringHelper.FormatString(
                "VideoLibraryDeleteMessageFormat",
                "Delete '{0}'? This only removes it from Niratan.",
                item.Video.Title));
        if (!confirmed)
            return;

        var result = await _videoLibraryService.DeleteVideoAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess(
            ResourceStringHelper.GetString("VideoLibraryDeletedMessage", "Video deleted."));
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task MarkWatchedAsync(VideoItemViewModel item)
    {
        var result = await _videoLibraryService.MarkWatchedAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(VideoItemViewModel item)
    {
        var isFavorite = !item.Video.IsFavorite;
        var result = await _videoLibraryService.SetFavoriteAsync(item.Video.Id, isFavorite, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess(ResourceStringHelper.GetString(
            isFavorite ? "VideoLibraryFavoriteAddedMessage" : "VideoLibraryFavoriteRemovedMessage",
            isFavorite ? "Added to favorites." : "Removed from favorites."));
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task AddToNewCollectionAsync(VideoItemViewModel item)
    {
        var name = await _dialogService.PromptTextAsync(
            ResourceStringHelper.GetString("VideoLibraryManualCollectionPromptTitle", "New collection"),
            ResourceStringHelper.GetString("VideoLibraryManualCollectionPromptPlaceholder", "Collection name"),
            ResourceStringHelper.GetString("VideoLibraryManualCollectionPromptPrimary", "Create"),
            ResourceStringHelper.GetString("VideoLibraryCreateSmartCollectionSecondaryButton", "Cancel"));
        if (string.IsNullOrWhiteSpace(name))
            return;

        var result = await _videoLibraryService.CreateManualCollectionAsync(
            name.Trim(),
            [item.Video.Id],
            _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        var createdCollection = result.Value!;
        _notificationService.ShowSuccess(ResourceStringHelper.GetString(
            "VideoLibraryManualCollectionCreatedMessage",
            "Collection created."));
        await LoadVideosAsync();

        SelectedLibraryView = VideoLibraryView.Collections;
        _activeCollectionId = createdCollection.Id;
        _activeFolderPath = null;
        _activeSeriesName = null;
        _activeTag = null;
        ApplyVisibleVideos();
    }

    [RelayCommand]
    private async Task RevealFileAsync(VideoItemViewModel item)
    {
        if (item.Video.IsRemote)
            return;

        var result = await _fileRevealService.RevealInFileExplorerAsync(item.Video.FilePath, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            _notificationService.ShowError(
                result.Error ?? ResourceStringHelper.GetString(
                    "VideoLibraryRevealFileMissingMessage",
                    "The video file no longer exists."),
                result.ErrorTitle ?? "Error");
        }
    }

    [RelayCommand]
    private async Task ClearProgressAsync(VideoItemViewModel item)
    {
        var result = await _videoLibraryService.ClearProgressAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadVideosAsync();
    }

    [RelayCommand]
    private void ToggleVideoSelection(VideoItemViewModel item)
    {
        if (_selectedVideoIds.Contains(item.Video.Id))
            _selectedVideoIds.Remove(item.Video.Id);
        else
            _selectedVideoIds.Add(item.Video.Id);

        item.IsSelected = _selectedVideoIds.Contains(item.Video.Id);
        SelectedVideoCount = _selectedVideoIds.Count;
        if (item.IsSelected)
            SelectVideoDetails(item);
        else if (SelectedVideo?.Video.Id == item.Video.Id)
            SelectFirstRemainingVideo();
    }

    private async Task LoadSeriesEpisodesAsync(
        VideoSeriesViewModel series,
        CancellationTokenSource cts)
    {
        IsLoadingSeriesEpisodes = true;
        SeriesEpisodesError = null;
        try
        {
            var result = series.MetadataIdentity is VideoMetadataCandidate identity
                ? await _discoveryService!.GetDetailsAsync(identity, cts.Token)
                : await _discoveryService!.GetDetailsByTitleAsync(
                    series.MetadataSearchTitles,
                    series.MetadataMediaKind,
                    series.MetadataYear,
                    cts.Token);

            // A scraped season can have its own provider identity (for example,
            // Bangumi's season 3 entry).  That detail response is valid but only
            // contains one season.  Resolve the series title as well and keep
            // the richer response so every remote season/episode is represented.
            if (series.MetadataMediaKind != VideoMetadataMediaKind.Movie)
            {
                var titleResult = await _discoveryService!.GetDetailsByTitleAsync(
                    series.MetadataSearchTitles,
                    series.MetadataMediaKind,
                    series.MetadataYear,
                    cts.Token);
                if (titleResult.IsSuccess
                    && titleResult.Value is not null
                    && (result.Value is null
                        || result.Value.Seasons.IsDefaultOrEmpty
                        || titleResult.Value.Seasons.Length > result.Value.Seasons.Length))
                {
                    result = titleResult;
                }
            }
            if (!ReferenceEquals(_seriesEpisodesCts, cts)
                || cts.IsCancellationRequested)
            {
                return;
            }

            if (!result.IsSuccess || result.Value is null)
            {
                SeriesEpisodesError = result.Error;
                return;
            }

            series.ApplyRemoteMetadata(result.Value.Metadata);
            series.ApplyRemoteSeasons(result.Value.Seasons.IsDefault
                ? []
                : result.Value.Seasons);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_seriesEpisodesCts, cts))
                SeriesEpisodesError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_seriesEpisodesCts, cts))
            {
                _seriesEpisodesCts = null;
                IsLoadingSeriesEpisodes = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task DownloadEpisodeAsync(VideoEpisodeSlotViewModel episode)
    {
        if (episode is null
            || !episode.CanDownload
            || SelectedSeries?.MetadataIdentity is not VideoMetadataCandidate seriesIdentity
            || _resourceSearchService is null
            || _nyaaDownloadManager is null)
        {
            return;
        }

        episode.IsDownloading = true;
        episode.DownloadStatus = ResourceStringHelper.GetString(
            "VideoEpisodeSearchingResources",
            "Searching resources…");
        try
        {
            var episodeIdentity = seriesIdentity with
            {
                MediaKind = VideoMetadataMediaKind.Episode,
                Title = $"{seriesIdentity.Title} {episode.SeasonNumber:00}x{episode.EpisodeNumber:00}",
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                Aliases = seriesIdentity.Aliases.Add(episode.Title).Distinct().ToImmutableArray(),
            };
            var query = $"{seriesIdentity.OriginalTitle ?? seriesIdentity.Title} " +
                        $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
            var result = await _resourceSearchService.SearchAsync(
                new VideoResourceSearchRequest(episodeIdentity, query),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is not { Count: > 0 } resources)
            {
                episode.DownloadStatus = result.Error
                    ?? ResourceStringHelper.GetString(
                        "VideoEpisodeResourceNotFound",
                        "No matching resource was found.");
                return;
            }

            var selected = SelectEpisodeResource(resources, episode);
            var downloadManager = await GetNyaaDownloadManagerAsync(_cts.Token);
            downloadManager.Enqueue(selected);
            episode.IsQueued = true;
            episode.DownloadStatus = ResourceStringHelper.GetString(
                "VideoEpisodeDownloadQueued",
                "Added to downloads");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            episode.DownloadStatus = ex.Message;
        }
        finally
        {
            episode.IsDownloading = false;
        }
    }

    private Task<INyaaDownloadManager> GetNyaaDownloadManagerAsync(CancellationToken ct)
    {
        if (_nyaaDownloadManagerTask is not null)
            return _nyaaDownloadManagerTask;

        var downloadManager = _nyaaDownloadManager
            ?? throw new InvalidOperationException("The video download service is unavailable.");
        return _nyaaDownloadManagerTask = Task.Run(
            () => downloadManager.Value,
            ct);
    }

    private static NyaaTorrentItem SelectEpisodeResource(
        IReadOnlyList<NyaaTorrentItem> resources,
        VideoEpisodeSlotViewModel episode)
    {
        var seasonEpisode = $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
        var compactSeasonEpisode = $"S{episode.SeasonNumber}E{episode.EpisodeNumber:00}";
        var xNotation = $"{episode.SeasonNumber}x{episode.EpisodeNumber:00}";
        return resources
            .OrderByDescending(item => item.Title.Contains(seasonEpisode, StringComparison.OrdinalIgnoreCase)
                                       || item.Title.Contains(compactSeasonEpisode, StringComparison.OrdinalIgnoreCase)
                                       || item.Title.Contains(xNotation, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.IsTrusted)
            .ThenByDescending(item => item.Seeders)
            .ThenByDescending(item => item.PublishedAt)
            .First();
    }

    [RelayCommand]
    private void SelectVideoDetails(VideoItemViewModel item)
    {
        _selectedVideoIds.Add(item.Video.Id);
        item.IsSelected = true;
        SelectedVideoCount = _selectedVideoIds.Count;
        SelectedVideo = item;
        SelectedVideoTitleDraft = item.Video.Title;
        SelectedVideoTagsDraft = item.Video.Tags ?? "";
        SelectedVideoSubtitlePath = item.Video.SubtitlePath ?? item.Video.SubtitleSelectionPath ?? "";
        RebuildManualCollectionOptions();
    }

    [RelayCommand]
    private void CloseVideoDetails() => SelectedVideo = null;

    [RelayCommand]
    private async Task RefreshSelectedMetadataAsync()
    {
        if (SelectedVideo?.Video.CatalogAssetId is not Guid assetId || _metadataCoordinator == null)
            return;
        var allowNetwork = await EnsureOnlineMetadataConsentAsync();
        try
        {
            var result = await _metadataCoordinator.RefreshAssetAsync(assetId, allowNetwork, _cts.Token);
            if (!string.IsNullOrWhiteSpace(result.Error))
                _notificationService.ShowError(result.Error, "Metadata refresh");
            await LoadVideosAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, "Metadata refresh");
        }
        finally
        {
            HasActiveMetadataRefresh = false;
        }
    }

    private async Task<bool> EnsureOnlineMetadataConsentAsync()
    {
        if (IsOnlineMetadataEnabled)
            return true;
        var accepted = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString("VideoMetadataConsentDialogTitle", "Enable online metadata?"),
            ResourceStringHelper.GetString(
                "VideoMetadataConsentDialogMessage",
                "Niratan sends only the parsed title, year, episode numbers and external IDs. It never uploads video, subtitle, NFO content or local paths."),
            ResourceStringHelper.GetString("VideoMetadataConsentDialogAccept", "Enable"),
            ResourceStringHelper.GetString("VideoMetadataConsentDialogCancel", "Not now"));
        if (accepted && _settingsService != null)
        {
            _settingsService.Current.VideoSettings.Metadata.OnlineConsentAccepted = true;
            await _settingsService.SaveAsync();
            OnPropertyChanged(nameof(IsOnlineMetadataEnabled));
        }
        return accepted;
    }

    [RelayCommand]
    private async Task SaveSourceSettingsAsync(VideoLibrarySourceSummary summary)
    {
        if (!summary.TryApplyProviderOrder(out var invalidProvider))
        {
            _notificationService.ShowError(
                ResourceStringHelper.FormatString(
                    "VideoLibraryInvalidProviderOrderMessage",
                    "Unknown metadata provider: {0}",
                    invalidProvider ?? ""),
                ResourceStringHelper.GetString(
                    "VideoLibraryInvalidProviderOrderTitle",
                    "Invalid provider order"));
            return;
        }
        var result = await _videoLibraryService.UpdateSourceSettingsAsync(summary.Source, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task FullScanSourceAsync(VideoLibrarySourceSummary summary)
    {
        if (_scanCoordinator == null || !Guid.TryParse(summary.Source.Id, out var sourceId))
            return;
        await _scanCoordinator.ScanSourceAsync(sourceId, fullScan: true, _cts.Token);
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task CancelSourceScanAsync(VideoLibrarySourceSummary summary)
    {
        if (_scanCoordinator != null && Guid.TryParse(summary.Source.Id, out var sourceId))
            await _scanCoordinator.CancelAsync(sourceId, _cts.Token);
    }

    [RelayCommand]
    private async Task PauseSourceScanAsync(VideoLibrarySourceSummary summary)
    {
        if (_scanCoordinator != null && Guid.TryParse(summary.Source.Id, out var sourceId))
            await _scanCoordinator.PauseAsync(sourceId, _cts.Token);
    }

    [RelayCommand]
    private async Task ResumeSourceScanAsync(VideoLibrarySourceSummary summary)
    {
        if (_scanCoordinator != null && Guid.TryParse(summary.Source.Id, out var sourceId))
            await _scanCoordinator.ResumeAsync(sourceId, _cts.Token);
    }

    [RelayCommand]
    private async Task BindMatchCandidateAsync(VideoMatchCandidateSnapshot candidate)
    {
        if (SelectedVideo?.Video is not { CatalogAssetId: Guid assetId } video || _metadataCoordinator == null)
            return;
        var kind = video.LibraryMediaType == VideoLibraryMediaType.Anime
            ? VideoMetadataMediaKind.Anime
            : video.CatalogNodeKind == VideoCatalogNodeKind.Movie
                ? VideoMetadataMediaKind.Movie
                : VideoMetadataMediaKind.Series;
        var proposed = new VideoMetadataCandidate(
            candidate.ProviderId,
            candidate.ProviderItemId,
            kind,
            candidate.Title,
            null,
            candidate.Year,
            video.SeasonNumber,
            video.EpisodeNumber,
            video.AbsoluteEpisodeNumber,
            [],
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add(candidate.ProviderId, candidate.ProviderItemId),
            null);
        var preview = await _metadataCoordinator.PreviewRematchAsync(assetId, proposed, _cts.Token);
        var diff = string.Join(Environment.NewLine, preview.FieldChanges.Select(change =>
            $"{change.Field}: {change.CurrentValue ?? "—"} → {change.ProposedValue ?? "—"}"));
        var confirmed = await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString("VideoMetadataRematchPreviewTitle", "Confirm rematch"),
            $"{preview.ProposedHierarchy}{Environment.NewLine}{diff}",
            ResourceStringHelper.GetString("VideoMetadataRematchConfirm", "Bind and lock"),
            ResourceStringHelper.GetString("VideoMetadataRematchCancel", "Cancel"));
        if (!confirmed)
            return;
        await _metadataCoordinator.ConfirmRematchAsync(preview, _cts.Token);
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task SaveVideoDetailsAsync()
    {
        if (SelectedVideo == null)
            return;

        var tags = SplitTags(SelectedVideoTagsDraft);
        var result = await _videoLibraryService.UpdateVideoDetailsAsync(
            SelectedVideo.Video.Id,
            SelectedVideoTitleDraft,
            tags,
            string.IsNullOrWhiteSpace(SelectedVideoSubtitlePath) ? null : SelectedVideoSubtitlePath,
            _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadVideosAsync();
        RestoreSelectedVideoDetails();
    }

    [RelayCommand]
    private async Task BindSubtitleAsync()
    {
        if (SelectedVideo == null)
            return;
        var path = await _dialogService.OpenFilePickerAsync(".srt", ".vtt", ".ass", ".ssa");
        if (path == null)
            return;
        SelectedVideoSubtitlePath = path;
        await SaveVideoDetailsAsync();
    }

    [RelayCommand]
    private async Task ClearBoundSubtitleAsync()
    {
        SelectedVideoSubtitlePath = "";
        await SaveVideoDetailsAsync();
    }

    [RelayCommand]
    private async Task SetSelectedCollectionMembershipAsync(VideoCollectionMembershipOption option)
    {
        if (SelectedVideo == null)
            return;

        var ids = option.Collection.ItemIds.ToList();
        if (option.IsIncluded)
        {
            if (!ids.Contains(SelectedVideo.Video.Id, StringComparer.OrdinalIgnoreCase))
                ids.Add(SelectedVideo.Video.Id);
        }
        else
        {
            ids.RemoveAll(id => string.Equals(id, SelectedVideo.Video.Id, StringComparison.OrdinalIgnoreCase));
        }

        var result = await _videoLibraryService.UpdateManualCollectionAsync(
            option.Collection, ids, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await LoadVideosAsync();
        RestoreSelectedVideoDetails();
    }

    [RelayCommand]
    private async Task AddSelectedToNewCollectionAsync()
    {
        if (SelectedVideo == null || string.IsNullOrWhiteSpace(ManualCollectionNameDraft))
            return;

        var result = await _videoLibraryService.CreateManualCollectionAsync(
            ManualCollectionNameDraft,
            [SelectedVideo.Video.Id],
            _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        ManualCollectionNameDraft = "";
        await LoadVideosAsync();
        RestoreSelectedVideoDetails();
    }

    [RelayCommand]
    private async Task MarkSelectedWatchedAsync()
    {
        foreach (var id in _selectedVideoIds.ToList())
        {
            var result = await _videoLibraryService.MarkWatchedAsync(id, _cts.Token);
            if (!result.IsSuccess && !result.IsCancelled)
            {
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
                return;
            }
        }
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task ClearSelectedProgressAsync()
    {
        foreach (var id in _selectedVideoIds.ToList())
        {
            var result = await _videoLibraryService.ClearProgressAsync(id, _cts.Token);
            if (!result.IsSuccess && !result.IsCancelled)
            {
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
                return;
            }
        }
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedVideosAsync()
    {
        if (_selectedVideoIds.Count == 0)
            return;
        var confirmed = await _dialogService.ConfirmAsync(
            "Remove selected videos",
            $"Remove {_selectedVideoIds.Count} selected videos from Niratan? Files on disk are kept.");
        if (!confirmed)
            return;

        var result = await _videoLibraryService.DeleteVideosAsync(_selectedVideoIds.ToList(), _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _selectedVideoIds.Clear();
        SelectedVideoCount = 0;
        SelectedVideo = null;
        await LoadVideosAsync();
    }

    [RelayCommand]
    private void SelectLibraryView(string? viewName)
    {
        if (!Enum.TryParse<VideoLibraryView>(viewName, out var view))
            view = string.Equals(viewName, nameof(VideoLibraryView.Watched), StringComparison.OrdinalIgnoreCase)
                ? VideoLibraryView.Finished
                : VideoLibraryView.All;

        SelectedLibraryView = view;
        SelectedSeries = null;
        _activeFolderPath = null;
        _activeCollectionId = null;
        _activeSeriesName = null;
        _activeTag = null;
        ApplyVisibleVideos();
    }

    [RelayCommand]
    private void SelectSeries(VideoSeriesViewModel series)
    {
        _seriesEpisodesCts?.Cancel();
        SelectedLibraryView = VideoLibraryView.Series;
        SelectedSeries = series;
        SelectedVideo = series.PrimaryPlayItem;
        CurrentViewTitle = series.Title;
        CurrentViewSubtitle = series.FactsText;
        SeriesEpisodesError = null;
        if (_discoveryService is not null)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _seriesEpisodesCts = cts;
            _ = LoadSeriesEpisodesAsync(series, cts);
        }
    }

    [RelayCommand]
    private void SelectSeriesSeason(VideoSeasonViewModel season) =>
        SelectedSeries?.SelectSeason(season.SeasonNumber);

    [RelayCommand]
    private void BackToSeries()
    {
        _seriesEpisodesCts?.Cancel();
        SelectedSeries = null;
        SelectedVideo = null;
        IsLoadingSeriesEpisodes = false;
        SeriesEpisodesError = null;
        CurrentViewTitle = GetViewTitle(VideoLibraryView.Series);
        CurrentViewSubtitle = FormatVideoCount(SeriesCards.Count);
    }

    [RelayCommand]
    private void SelectLayout(string? layoutName)
    {
        if (Enum.TryParse<VideoLibraryLayoutMode>(layoutName, out var layoutMode))
            SelectedLayoutMode = layoutMode;
    }

    [RelayCommand]
    private void SelectFolderFilter(VideoLibraryFilterRow row)
    {
        SelectedLibraryView = VideoLibraryView.Folders;
        _activeFolderPath = row.Key;
        _activeCollectionId = null;
        _activeSeriesName = null;
        _activeTag = null;
        ApplyVisibleVideos();
    }

    [RelayCommand]
    private void SelectCollectionFilter(VideoLibraryFilterRow row)
    {
        SelectedLibraryView = VideoLibraryView.Collections;
        _activeFolderPath = null;
        _activeCollectionId = row.Key;
        _activeSeriesName = null;
        _activeTag = null;
        ApplyVisibleVideos();
    }

    [RelayCommand]
    private void SelectTagFilter(VideoLibraryFilterRow row)
    {
        SelectedLibraryView = VideoLibraryView.Tags;
        _activeFolderPath = null;
        _activeCollectionId = null;
        _activeSeriesName = null;
        _activeTag = row.Key;
        ApplyVisibleVideos();
    }

    [RelayCommand]
    private async Task DeleteCollectionAsync(VideoLibraryFilterRow row)
    {
        var collection = _collections.FirstOrDefault(item => item.Id == row.Key);
        if (collection == null)
            return;
        var confirmed = await _dialogService.ConfirmAsync(
            "Delete collection",
            $"Delete '{collection.Name}'? Videos stay in your library.");
        if (!confirmed)
            return;

        var result = await _videoLibraryService.DeleteCollectionAsync(collection.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }
        if (_activeCollectionId == collection.Id)
            _activeCollectionId = null;
        await LoadVideosAsync();
        RestoreSelectedVideoDetails();
    }

    [RelayCommand]
    private void BeginCreateSmartCollection()
    {
        _editingSmartCollectionId = null;
        SmartCollectionDialogTitle = "Create smart collection";
        SmartCollectionNameDraft = "";
        SmartRuleDrafts = new ObservableCollection<VideoSmartRuleDraft>
        {
            new(VideoSmartRuleField.FileName, VideoSmartRuleMatch.Contains, ""),
        };
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
    }

    public bool BeginEditSmartCollection(VideoLibraryFilterRow row)
    {
        var collection = _collections.FirstOrDefault(item => item.Id == row.Key);
        if (collection?.Kind != VideoCollectionKind.Smart)
            return false;

        _editingSmartCollectionId = collection.Id;
        SmartCollectionDialogTitle = "Edit smart collection";
        SmartCollectionNameDraft = collection.Name;
        SmartRuleDrafts = new ObservableCollection<VideoSmartRuleDraft>(
            collection.SmartRules.Select(rule => new VideoSmartRuleDraft(
                rule.Field, rule.Match, rule.Value)));
        if (SmartRuleDrafts.Count == 0)
            SmartRuleDrafts.Add(new VideoSmartRuleDraft(VideoSmartRuleField.FileName, VideoSmartRuleMatch.Contains, ""));
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
        return true;
    }

    [RelayCommand]
    private void AddSmartRule()
    {
        SmartRuleDrafts.Add(new VideoSmartRuleDraft(
            VideoSmartRuleField.FileName, VideoSmartRuleMatch.Contains, ""));
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
    }

    [RelayCommand]
    private void RemoveSmartRule(VideoSmartRuleDraft rule)
    {
        SmartRuleDrafts.Remove(rule);
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
    }

    public void RefreshSmartCollectionPreview() => OnPropertyChanged(nameof(SmartCollectionPreviewRows));

    [RelayCommand]
    private async Task CreateSmartCollectionAsync()
    {
        var name = SmartCollectionNameDraft.Trim();
        var rules = BuildSmartRules();
        if (string.IsNullOrWhiteSpace(name) || rules.Count == 0)
            return;

        var existing = string.IsNullOrWhiteSpace(_editingSmartCollectionId)
            ? null
            : _collections.FirstOrDefault(collection => collection.Id == _editingSmartCollectionId);
        var result = existing == null
            ? await _videoLibraryService.CreateSmartCollectionAsync(name, rules, _cts.Token)
            : await _videoLibraryService.UpdateSmartCollectionAsync(existing, name, rules, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        var createdCollection = result.Value!;

        SmartCollectionNameDraft = "";
        SelectedSmartRuleField = VideoSmartRuleField.FileName;
        SmartRuleValueDraft = "";
        SmartRuleDrafts.Clear();
        _editingSmartCollectionId = null;

        await LoadVideosAsync();

        if (_collections.All(collection => !string.Equals(collection.Id, createdCollection.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _collections.Add(createdCollection);
            RebuildFilters();
        }

        SelectedLibraryView = VideoLibraryView.Collections;
        _activeCollectionId = createdCollection.Id;
        ApplyVisibleVideos();
    }

    private async Task LoadVideosAsync()
    {
        try
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Dispose();
        }

        _cts = new CancellationTokenSource();
        IsContentLoading = true;

        var videosTask = _videoLibraryService.GetVideosAsync(ct: _cts.Token);
        var collectionsTask = _videoLibraryService.GetCollectionsAsync(_cts.Token);
        var sourcesTask = _videoLibraryService.GetSourcesAsync(_cts.Token);
        await Task.WhenAll(videosTask, collectionsTask, sourcesTask);

        var videoResult = await videosTask;
        var collectionResult = await collectionsTask;
        var sourceResult = await sourcesTask;

        if (videoResult.IsSuccess)
        {
            _allVideos = videoResult.Value!.ToList();
            _collections = collectionResult.IsSuccess
                ? collectionResult.Value!.ToList()
                : [];
            _sources = sourceResult.IsSuccess
                ? sourceResult.Value!.ToList()
                : [];
            var currentIds = _allVideos.Select(video => video.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _selectedVideoIds.RemoveWhere(id => !currentIds.Contains(id));
            SelectedVideoCount = _selectedVideoIds.Count;
            RebuildFilters();
            ApplyVisibleVideos();
        }
        else if (!videoResult.IsCancelled)
        {
            _notificationService.ShowError(videoResult.Error!, videoResult.ErrorTitle!);
        }

        if (!collectionResult.IsSuccess && !collectionResult.IsCancelled)
            _notificationService.ShowError(collectionResult.Error!, collectionResult.ErrorTitle!);
        if (!sourceResult.IsSuccess && !sourceResult.IsCancelled)
            _notificationService.ShowError(sourceResult.Error!, sourceResult.ErrorTitle!);

        IsContentLoading = false;
    }

    private void SubscribeToPlayerLibraryChanges()
    {
        if (_isSubscribedToPlayerLibraryChanges)
            return;

        _playerWindowService.LibraryChanged += OnPlayerLibraryChanged;
        _isSubscribedToPlayerLibraryChanges = true;
    }

    private void UnsubscribeFromPlayerLibraryChanges()
    {
        if (!_isSubscribedToPlayerLibraryChanges)
            return;

        _playerWindowService.LibraryChanged -= OnPlayerLibraryChanged;
        _isSubscribedToPlayerLibraryChanges = false;
    }

    private void SubscribeToScanProgress()
    {
        if (_scanCoordinator != null)
            _scanCoordinator.ProgressChanged -= OnScanProgressChanged;
        if (_scanCoordinator != null)
            _scanCoordinator.ProgressChanged += OnScanProgressChanged;
    }

    private void UnsubscribeFromScanProgress()
    {
        if (_scanCoordinator != null)
            _scanCoordinator.ProgressChanged -= OnScanProgressChanged;
    }

    private void SubscribeToMetadataProgress()
    {
        if (_metadataCoordinator != null)
            _metadataCoordinator.ProgressChanged -= OnMetadataProgressChanged;
        if (_metadataCoordinator != null)
            _metadataCoordinator.ProgressChanged += OnMetadataProgressChanged;
        if (_metadataCoordinator != null)
        {
            _metadataCoordinator.BatchProgressChanged -= OnMetadataBatchProgressChanged;
            _metadataCoordinator.BatchProgressChanged += OnMetadataBatchProgressChanged;
        }
    }

    private void UnsubscribeFromMetadataProgress()
    {
        if (_metadataCoordinator != null)
            _metadataCoordinator.ProgressChanged -= OnMetadataProgressChanged;
        if (_metadataCoordinator != null)
            _metadataCoordinator.BatchProgressChanged -= OnMetadataBatchProgressChanged;
    }

    private void OnMetadataBatchProgressChanged(object? sender, VideoMetadataBatchProgress progress)
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ApplyMetadataBatchProgress(progress), null);
            return;
        }
        ApplyMetadataBatchProgress(progress);
    }

    private void ApplyMetadataBatchProgress(VideoMetadataBatchProgress progress)
    {
        _latestMetadataProgress[progress.SourceId] = progress;
        ApplyMetadataTaskProgress(progress);
        SourceSummaries.FirstOrDefault(summary =>
                Guid.TryParse(summary.Source.Id, out var id) && id == progress.SourceId)
            ?.UpdateMetadataProgress(progress);
        var active = _metadataCoordinator?.ActiveBatchProgress
            .Where(item => item.State is VideoCatalogJobState.Running or VideoCatalogJobState.Queued)
            .OrderByDescending(item => item.TotalCount - item.ProcessedCount)
            .FirstOrDefault();
        HasBackgroundMetadataTask = active != null;
        if (active == null)
        {
            if (progress.State == VideoCatalogJobState.Completed
                && _lastReloadedMetadataJobId != progress.JobId)
            {
                _lastReloadedMetadataJobId = progress.JobId;
                _ = LoadVideosAsync();
            }
            _ = LoadMetadataTasksAsync(CancellationToken.None);
            return;
        }
        BackgroundMetadataProgress = active.TotalCount > 0
            ? Math.Clamp(active.ProcessedCount * 100d / active.TotalCount, 0, 100)
            : 100;
        var sourceName = SourceSummaries.FirstOrDefault(summary =>
            Guid.TryParse(summary.Source.Id, out var id) && id == active.SourceId)?.Source.Name;
        BackgroundMetadataText = ResourceStringHelper.FormatString(
            "VideoMetadataBackgroundProgressFormat",
            "Scraping metadata{0} · {1} / {2} · {3} success · {4} pending · {5} failed",
            string.IsNullOrWhiteSpace(sourceName) ? "" : $" · {sourceName}",
            active.ProcessedCount,
            active.TotalCount,
            Math.Max(0, active.ProcessedCount - active.NeedsReviewCount - active.FailedCount),
            active.NeedsReviewCount,
            active.FailedCount);
    }

    private void ApplyMetadataTaskProgress(VideoMetadataBatchProgress progress)
    {
        var task = MetadataTasks.FirstOrDefault(item => item.JobId == progress.JobId);
        if (task != null)
        {
            task.Update(progress);
            return;
        }

        var sourceName = SourceSummaries.FirstOrDefault(summary =>
            Guid.TryParse(summary.Source.Id, out var id) && id == progress.SourceId)?.Source.Name;
        MetadataTasks.Add(new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                progress.JobId,
                progress.SourceId,
                progress.State,
                progress.ProcessedCount,
                progress.TotalCount,
                progress.MatchedCount,
                progress.NeedsReviewCount,
                progress.Error,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                progress.FailedCount),
            sourceName));
        OnPropertyChanged(nameof(HasMetadataTasks));
        OnPropertyChanged(nameof(MetadataTaskCountText));
    }

    private async Task LoadMetadataTasksAsync(CancellationToken ct)
    {
        if (_metadataCoordinator == null)
        {
            MetadataTasks = new ObservableCollection<VideoMetadataTaskViewModel>();
            return;
        }

        try
        {
            var snapshots = await _metadataCoordinator.GetTaskHistoryAsync(50, ct);
            var sourceNames = _sources
                .Where(source => Guid.TryParse(source.Id, out _))
                .ToDictionary(source => Guid.Parse(source.Id), source => source.Name);
            MetadataTasks = new ObservableCollection<VideoMetadataTaskViewModel>(
                snapshots.Select(snapshot => new VideoMetadataTaskViewModel(
                    snapshot,
                    snapshot.SourceId is { } sourceId
                        && sourceNames.TryGetValue(sourceId, out var sourceName)
                        ? sourceName
                        : null)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                ex.Message,
                ResourceStringHelper.GetString(
                    "VideoMetadataTaskLoadFailedTitle",
                    "Background tasks unavailable"));
        }
    }

    private void OnMetadataProgressChanged(object? sender, VideoMetadataRefreshProgress progress)
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ApplyMetadataProgress(progress), null);
            return;
        }
        ApplyMetadataProgress(progress);
    }

    private void ApplyMetadataProgress(VideoMetadataRefreshProgress progress)
    {
        if (HasBackgroundMetadataTask)
            return;
        HasActiveMetadataRefresh = progress.Stage != VideoMetadataRefreshStage.Completed;
        IsMetadataRefreshIndeterminate = progress.TotalProviders <= 0
                                         || progress.Stage is not VideoMetadataRefreshStage.Searching;
        MetadataRefreshProgress = progress.TotalProviders > 0
            ? Math.Clamp(progress.CompletedProviders * 100d / progress.TotalProviders, 0, 100)
            : 0;
        var stage = progress.Stage switch
        {
            VideoMetadataRefreshStage.Searching => ResourceStringHelper.GetString(
                "VideoMetadataStageSearching", "Searching providers"),
            VideoMetadataRefreshStage.Matching => ResourceStringHelper.GetString(
                "VideoMetadataStageMatching", "Matching candidates"),
            VideoMetadataRefreshStage.Details => ResourceStringHelper.GetString(
                "VideoMetadataStageDetails", "Loading details"),
            VideoMetadataRefreshStage.Artwork => ResourceStringHelper.GetString(
                "VideoMetadataStageArtwork", "Caching artwork"),
            _ => ResourceStringHelper.GetString("VideoMetadataStageCompleted", "Metadata refresh complete"),
        };
        var itemTitle = _allVideos.FirstOrDefault(video => video.CatalogAssetId == progress.AssetId)?.Title;
        MetadataRefreshText = string.IsNullOrWhiteSpace(itemTitle)
            ? stage
            : $"{stage} · {itemTitle}";
    }

    private void OnScanProgressChanged(object? sender, VideoLibraryScanProgress progress)
    {
        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(_ => ApplyScanProgress(progress), null);
            return;
        }
        ApplyScanProgress(progress);
    }

    private void ApplyScanProgress(VideoLibraryScanProgress progress)
    {
        _latestScanProgress[progress.SourceId] = progress;
        SourceSummaries.FirstOrDefault(summary =>
                Guid.TryParse(summary.Source.Id, out var id) && id == progress.SourceId)
            ?.UpdateProgress(progress);

        var active = _latestScanProgress.Values
            .Where(item => item.State is VideoCatalogJobState.Running or VideoCatalogJobState.Paused)
            .OrderByDescending(item => item.Generation)
            .FirstOrDefault();
        HasActiveScan = active != null;
        if (active == null)
            return;
        IsActiveScanIndeterminate = active.TotalCount is not > 0;
        ActiveScanProgress = active.TotalCount is > 0
            ? Math.Clamp(active.ProcessedCount * 100d / active.TotalCount.Value, 0, 100)
            : 0;
        ActiveScanText = VideoLibrarySourceSummary.FormatProgress(active);
    }

    private async void OnPlayerLibraryChanged(object? sender, EventArgs e) =>
        await LoadVideosAsync();

    partial void OnSearchTextChanged(string value) => ApplyVisibleVideos();

    partial void OnSelectedSortOptionChanged(VideoLibrarySortOption value) => ApplyVisibleVideos();

    partial void OnSelectedLayoutModeChanged(VideoLibraryLayoutMode value)
    {
        OnPropertyChanged(nameof(IsListLayout));
        OnPropertyChanged(nameof(IsPosterLayout));
    }

    partial void OnSelectedSmartRuleFieldChanged(VideoSmartRuleField value)
    {
        OnPropertyChanged(nameof(IsSmartRuleValueVisible));
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
    }

    partial void OnSelectedLibraryViewChanged(VideoLibraryView value)
    {
        if (value != VideoLibraryView.Sources)
            IsMetadataTaskPanelOpen = false;

        CurrentViewTitle = GetViewTitle(value);
        OnPropertyChanged(nameof(IsHomeView));
        OnPropertyChanged(nameof(IsDiscoverView));
        OnPropertyChanged(nameof(IsFoldersView));
        OnPropertyChanged(nameof(IsCollectionsView));
        OnPropertyChanged(nameof(IsTagsView));
        OnPropertyChanged(nameof(IsSourcesView));
        OnPropertyChanged(nameof(IsVideoCatalogView));
        OnPropertyChanged(nameof(IsSeriesBrowseView));
        OnPropertyChanged(nameof(IsSeriesDetailView));
        OnPropertyChanged(nameof(IsLibraryBrowseView));
        OnPropertyChanged(nameof(IsCatalogSearchVisible));
        OnPropertyChanged(nameof(IsLibraryHeaderVisible));
        OnPropertyChanged(nameof(IsMetadataTaskPanelVisible));
        OnPropertyChanged(nameof(ShowNoVideos));
        OnPropertyChanged(nameof(ShowCatalogLoading));
    }

    partial void OnSourceSummariesChanged(ObservableCollection<VideoLibrarySourceSummary> value) =>
        OnPropertyChanged(nameof(HasSources));

    private void ApplyVisibleVideos()
    {
        RebuildHomeSections();
        RebuildSeriesCards();
        var filtered = FilterVideos(_allVideos).ToList();
        Videos = new ObservableCollection<VideoItemViewModel>(
            SortVideos(filtered).Select(video => new VideoItemViewModel(
                video,
                _selectedVideoIds.Contains(video.Id))));
        CurrentViewSubtitle = FormatVideoCount(Videos.Count);
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
        _ = GenerateMissingThumbnailsForVisibleVideosAsync(_cts.Token);
    }

    private void RebuildSeriesCards()
    {
        var selectedId = SelectedSeries?.Id;
        var selectedSeason = SelectedSeries?.SelectedSeason;
        var nodeGroups = _allVideos
            .Where(video => video.CatalogSeriesNodeId.HasValue
                            && video.LibraryMediaType != VideoLibraryMediaType.Anime)
            .GroupBy(video => video.CatalogSeriesNodeId!.Value)
            .Select(group => new SeriesNodeGroup(group.Key, group))
            .ToList();
        var seriesGroups = MergeRelatedSeriesGroups(nodeGroups, selectedId);
        SeriesCards = new ObservableCollection<VideoSeriesViewModel>(seriesGroups
            .Where(group => string.IsNullOrWhiteSpace(SearchText)
                            || group.Videos.Any(video => MatchesSearch(video, SearchText.Trim())))
            .Select(group => new VideoSeriesViewModel(group.NodeId, group.Videos))
            .OrderBy(series => series.Title, StringComparer.CurrentCultureIgnoreCase));
        SelectedSeries = selectedId.HasValue
            ? SeriesCards.FirstOrDefault(series => series.Id == selectedId.Value)
            : null;
        if (SelectedSeries != null && selectedSeason != null)
            SelectedSeries.SelectSeason(selectedSeason.SeasonNumber);
        if (SelectedLibraryView == VideoLibraryView.Series && SelectedSeries == null)
            CurrentViewSubtitle = FormatVideoCount(SeriesCards.Count);
    }

    private sealed class SeriesNodeGroup
    {
        public SeriesNodeGroup(Guid nodeId, IEnumerable<VideoItem> videos)
        {
            NodeId = nodeId;
            Videos = videos.ToList();
            NodeIds = [nodeId];
            IdentityKeys = BuildSeriesIdentityKeys(Videos);
        }

        public Guid NodeId { get; set; }
        public List<VideoItem> Videos { get; }
        public HashSet<Guid> NodeIds { get; }
        public HashSet<string> IdentityKeys { get; }
    }

    private static IReadOnlyList<SeriesNodeGroup> MergeRelatedSeriesGroups(
        IReadOnlyList<SeriesNodeGroup> groups,
        Guid? selectedNodeId)
    {
        if (groups.Count < 2)
            return groups;

        var parents = Enumerable.Range(0, groups.Count).ToArray();

        int Find(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
                parents[rightRoot] = leftRoot;
        }

        var identityOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < groups.Count; index++)
        {
            foreach (var key in groups[index].IdentityKeys)
            {
                if (identityOwners.TryGetValue(key, out var owner))
                    Union(index, owner);
                else
                    identityOwners[key] = index;
            }
        }

        return Enumerable.Range(0, groups.Count)
            .GroupBy(Find)
            .Select(memberIndexes =>
            {
                var members = memberIndexes.Select(index => groups[index]).ToList();
                var selectedMember = selectedNodeId.HasValue
                    ? members.FirstOrDefault(member => member.NodeIds.Contains(selectedNodeId.Value))
                    : null;
                var primary = selectedMember
                              ?? members
                                  .OrderByDescending(member => member.Videos.Count)
                                  .ThenByDescending(member => member.Videos.Count(video => video.IsAvailable))
                                  .ThenBy(member => member.NodeId)
                                  .First();
                var merged = new SeriesNodeGroup(
                    primary.NodeId,
                    members.SelectMany(member => member.Videos));
                merged.NodeIds.Clear();
                foreach (var member in members)
                {
                    merged.NodeIds.UnionWith(member.NodeIds);
                    merged.IdentityKeys.UnionWith(member.IdentityKeys);
                }
                return merged;
            })
            .OrderBy(group => group.NodeId)
            .ToList();
    }

    private static HashSet<string> BuildSeriesIdentityKeys(IEnumerable<VideoItem> videos)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            foreach (var pair in video.ExternalIds)
            {
                if (IsSeriesIdentityProvider(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    keys.Add($"id:{pair.Key.Trim()}:{pair.Value.Trim()}");
            }

            foreach (var candidate in video.MatchCandidates)
            {
                if (candidate.HasHardConflict
                    || candidate.Score < 0.15
                    || !IsSeriesIdentityProvider(candidate.ProviderId)
                    || string.IsNullOrWhiteSpace(candidate.ProviderItemId))
                {
                    continue;
                }

                keys.Add($"id:{candidate.ProviderId.Trim()}:{candidate.ProviderItemId.Trim()}");
            }

            foreach (var title in new[] { video.CatalogSeriesTitle, video.CatalogSeriesOriginalTitle }
                         .Where(title => !string.IsNullOrWhiteSpace(title)))
            {
                var normalized = NormalizeSeriesTitle(title!);
                if (normalized.Length > 0)
                    keys.Add($"title:{normalized}");
            }
        }

        return keys;
    }

    private static bool IsSeriesIdentityProvider(string providerId) =>
        providerId.Equals("tmdb", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("tvmaze", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("bangumi", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("anidb", StringComparison.OrdinalIgnoreCase)
        || providerId.Equals("mal", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSeriesTitle(string title)
    {
        var value = title.Trim();
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?:\s*[-:·]?\s*\d+(?:st|nd|rd|th)?\s*(?:season|期|季)\s*)$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?:第\s*\d+\s*[期季]\s*)$",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private void RebuildHomeSections()
    {
        HomeContinueWatching = new ObservableCollection<VideoItemViewModel>(
            BuildContinueWatchingItems(_allVideos, 6)
                .Select(video => new VideoItemViewModel(video)));
        HomeRecentlyAdded = new ObservableCollection<VideoItemViewModel>(
            _allVideos
                .Where(video => video.IsAvailable)
                .OrderByDescending(video => video.ImportedAt)
                .Take(6)
                .Select(video => new VideoItemViewModel(video)));
        HomeNextEpisodes = new ObservableCollection<VideoItemViewModel>(
            BuildNextEpisodes().Take(6).Select(video => new VideoItemViewModel(video)));
    }

    internal static IReadOnlyList<VideoItem> BuildContinueWatchingItems(
        IEnumerable<VideoItem> videos,
        int limit)
    {
        return videos
            .Where(video => video.IsAvailable && HasProgress(video) && !video.IsWatched)
            .GroupBy(video => video.CatalogSeriesNodeId.HasValue
                ? $"series:{video.CatalogSeriesNodeId.Value:D}"
                : $"asset:{video.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
                .First())
            .OrderByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
            .Take(limit)
            .ToList();
    }

    private IEnumerable<VideoItem> BuildNextEpisodes() => BuildNextEpisodeItems(_allVideos);

    internal static IReadOnlyList<VideoItem> BuildNextEpisodeItems(IEnumerable<VideoItem> videos)
    {
        var logicalEpisodes = VideoSeriesViewModel.CollapseLogicalEntries(videos
            .Where(video => video.CatalogSeriesNodeId.HasValue
                            && video.CatalogNodeKind == VideoCatalogNodeKind.Episode
                            && video.IsAvailable
                            && !VideoSeriesViewModel.IsSpecialEntry(video)));

        return logicalEpisodes
            .GroupBy(video => video.CatalogSeriesNodeId!.Value)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(video => video.SeasonNumber ?? int.MaxValue)
                    .ThenBy(video => video.EpisodeNumber ?? video.AbsoluteEpisodeNumber ?? int.MaxValue)
                    .ThenBy(video => video.AbsoluteEpisodeNumber ?? int.MaxValue)
                    .ThenBy(video => video.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var anchor = ordered.FindLastIndex(video => video.IsWatched || HasProgress(video));
                return anchor >= 0
                    ? ordered.Skip(anchor + 1).FirstOrDefault(video => !video.IsWatched)
                    : null;
            })
            .Where(video => video != null)
            .Select(video => video!)
            .OrderByDescending(video => video.ImportedAt)
            .ToList();
    }

    private IEnumerable<VideoItem> FilterVideos(IEnumerable<VideoItem> videos)
    {
        var query = SearchText.Trim();
        return videos
            .Where(MatchesSelectedView)
            .Where(video => string.IsNullOrWhiteSpace(query) || MatchesSearch(video, query));
    }

    private bool MatchesSelectedView(VideoItem video) =>
        SelectedLibraryView switch
        {
            VideoLibraryView.Discover => false,
            VideoLibraryView.ContinueWatching => HasProgress(video) && !video.IsWatched,
            VideoLibraryView.Movies => video.CatalogNodeKind == VideoCatalogNodeKind.Movie,
            VideoLibraryView.Anime => video.LibraryMediaType == VideoLibraryMediaType.Anime,
            VideoLibraryView.Unwatched => !HasProgress(video) && !video.IsWatched,
            VideoLibraryView.Finished => video.IsWatched,
            VideoLibraryView.Watched => video.IsWatched,
            VideoLibraryView.Recent => video.LastOpenedAt.HasValue,
            VideoLibraryView.Favorites => video.IsFavorite,
            VideoLibraryView.NeedsReview => video.NeedsReview,
            VideoLibraryView.Unorganized => video.IsUnorganized,
            VideoLibraryView.Series when string.IsNullOrWhiteSpace(_activeSeriesName) =>
                video.CatalogSeriesNodeId.HasValue
                && video.LibraryMediaType != VideoLibraryMediaType.Anime,
            VideoLibraryView.Folders when !string.IsNullOrWhiteSpace(_activeFolderPath) =>
                string.Equals(video.SourceFolderPath, _activeFolderPath, StringComparison.OrdinalIgnoreCase),
            VideoLibraryView.Series when !string.IsNullOrWhiteSpace(_activeSeriesName) =>
                string.Equals(SeriesName(video), _activeSeriesName, StringComparison.OrdinalIgnoreCase),
            VideoLibraryView.Collections when !string.IsNullOrWhiteSpace(_activeCollectionId) =>
                MatchesCollection(video, _activeCollectionId),
            VideoLibraryView.Tags when !string.IsNullOrWhiteSpace(_activeTag) =>
                SplitTags(video.Tags).Contains(_activeTag, StringComparer.OrdinalIgnoreCase),
            _ => true,
        };

    private IEnumerable<VideoItem> SortVideos(IEnumerable<VideoItem> videos) =>
        SelectedSortOption switch
        {
            VideoLibrarySortOption.Title => videos
                .OrderBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(video => video.LastOpenedAt ?? video.ImportedAt),
            VideoLibrarySortOption.Progress => videos
                .OrderByDescending(ProgressRatio)
                .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase),
            VideoLibrarySortOption.Folder => videos
                .OrderBy(video => video.SourceFolderPath ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => videos
                .OrderByDescending(video => video.LastOpenedAt ?? video.ImportedAt)
                .ThenBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase),
        };

    private void RebuildFilters()
    {
        FolderFilters = new ObservableCollection<VideoLibraryFilterRow>(
            _allVideos
                .Where(video => !string.IsNullOrWhiteSpace(video.SourceFolderPath))
                .GroupBy(video => video.SourceFolderPath!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => Path.GetFileName(group.Key), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new VideoLibraryFilterRow(
                    group.Key,
                    Path.GetFileName(group.Key),
                    FormatVideoCount(group.Count()))));

        CollectionFilters = new ObservableCollection<VideoLibraryFilterRow>(
            _collections.Count > 0
                ? _collections
                    .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(collection => new VideoLibraryFilterRow(
                        collection.Id,
                        collection.Name,
                        FormatVideoCount(_allVideos.Count(video => MatchesCollection(video, collection.Id))),
                        collection.Kind))
                : _allVideos
                    .Where(video => !string.IsNullOrWhiteSpace(video.CollectionName))
                    .GroupBy(video => video.CollectionName!, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => new VideoLibraryFilterRow(
                        group.Key,
                        group.Key,
                        FormatVideoCount(group.Count()))));

        TagFilters = new ObservableCollection<VideoLibraryFilterRow>(
            _allVideos
                .SelectMany(video => SplitTags(video.Tags))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new VideoLibraryFilterRow(
                    group.Key,
                    group.Key,
                    FormatVideoCount(group.Count()))));

        SourceSummaries = new ObservableCollection<VideoLibrarySourceSummary>(
            _sources.Select(source =>
            {
                var sourceVideos = _allVideos
                    .Where(video => string.Equals(video.SourceId, source.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var summary = new VideoLibrarySourceSummary(
                    source,
                    sourceVideos.Count,
                    sourceVideos.Count(video => HasProgress(video) && !video.IsWatched),
                    sourceVideos.Count(video => !video.IsRemote && !File.Exists(video.FilePath)));
                if (Guid.TryParse(source.Id, out var sourceId)
                    && _latestScanProgress.TryGetValue(sourceId, out var progress))
                    summary.UpdateProgress(progress);
                if (Guid.TryParse(source.Id, out sourceId))
                {
                    var metadataProgress = _metadataCoordinator?.ActiveBatchProgress
                        .FirstOrDefault(item => item.SourceId == sourceId)
                        ?? (_latestMetadataProgress.TryGetValue(sourceId, out var latestProgress)
                            ? latestProgress
                            : null);
                    if (metadataProgress != null)
                        summary.UpdateMetadataProgress(metadataProgress);
                }
                return summary;
            }));
        OnPropertyChanged(nameof(HomeMoviesCountText));
        OnPropertyChanged(nameof(HomeSeriesCountText));
        OnPropertyChanged(nameof(HomeAnimeCountText));
        OnPropertyChanged(nameof(HomeCollectionsCountText));
    }

    private static bool MatchesSearch(VideoItem video, string query) =>
        Contains(video.Title, query)
        || Contains(video.FilePath, query)
        || Contains(video.OriginalUrl, query)
        || Contains(video.SourceFolderPath, query)
        || Contains(video.CollectionName, query)
        || Contains(video.OriginalTitle, query)
        || Contains(video.LocalizedSubtitle, query)
        || video.ExternalIds.Any(pair => Contains(pair.Key, query) || Contains(pair.Value, query))
        || SplitTags(video.Tags).Any(tag => Contains(tag, query));

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static bool HasProgress(VideoItem video) =>
        video.DurationSeconds > 0
        && video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds;

    private static double ProgressRatio(VideoItem video) =>
        video.DurationSeconds <= 0
            ? 0
            : Math.Clamp(video.LastPositionSeconds / video.DurationSeconds, 0, 1);

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags
                .Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string GetViewTitle(VideoLibraryView value) =>
        value switch
        {
            VideoLibraryView.Home => ResourceStringHelper.GetString("VideoLibraryViewHome", "Home"),
            VideoLibraryView.Discover => ResourceStringHelper.GetString("VideoLibraryViewDiscover", "Discover"),
            VideoLibraryView.Movies => ResourceStringHelper.GetString("VideoLibraryViewMovies", "Movies"),
            VideoLibraryView.Anime => ResourceStringHelper.GetString("VideoLibraryViewAnime", "Anime"),
            VideoLibraryView.ContinueWatching => ResourceStringHelper.GetString(
                "VideoLibraryViewContinueWatching",
                "Continue Watching"),
            VideoLibraryView.Unwatched => ResourceStringHelper.GetString("VideoLibraryViewUnwatched", "Unwatched"),
            VideoLibraryView.Finished => ResourceStringHelper.GetString("VideoLibraryViewFinished", "Finished"),
            VideoLibraryView.Recent => ResourceStringHelper.GetString("VideoLibraryViewRecent", "Recent"),
            VideoLibraryView.Favorites => ResourceStringHelper.GetString("VideoLibraryViewFavorites", "Favorites"),
            VideoLibraryView.NeedsReview => ResourceStringHelper.GetString("VideoLibraryViewNeedsReview", "Needs Review"),
            VideoLibraryView.Unorganized => ResourceStringHelper.GetString("VideoLibraryViewUnorganized", "Unorganized"),
            VideoLibraryView.Sources => ResourceStringHelper.GetString("VideoLibraryViewSources", "Sources"),
            VideoLibraryView.Watched => ResourceStringHelper.GetString("VideoLibraryViewWatched", "Watched"),
            VideoLibraryView.Folders => ResourceStringHelper.GetString("VideoLibraryViewFolders", "Folders"),
            VideoLibraryView.Series => ResourceStringHelper.GetString("VideoLibraryViewSeries", "Series"),
            VideoLibraryView.Collections => ResourceStringHelper.GetString("VideoLibraryViewCollections", "Collections"),
            VideoLibraryView.Tags => ResourceStringHelper.GetString("VideoLibraryViewTags", "Tags"),
            _ => ResourceStringHelper.GetString("VideoLibraryViewAll", "All Videos"),
        };

    private static string FormatVideoCount(int count) =>
        ResourceStringHelper.FormatString("VideoLibraryCountFormat", "{0} videos", count);

    private IReadOnlyList<VideoSmartRule> BuildSmartRules()
    {
        if (SmartRuleDrafts.Count > 0)
        {
            return SmartRuleDrafts
                .Select(draft => new VideoSmartRule
                {
                    Field = draft.Field,
                    Match = draft.Match,
                    Value = draft.Value.Trim(),
                })
                .Where(rule => rule.Match == VideoSmartRuleMatch.IsTrue || rule.Value.Length > 0)
                .ToList();
        }

        if (SelectedSmartRuleField == VideoSmartRuleField.HasBoundSubtitle)
        {
            return
            [
                new VideoSmartRule
                {
                    Field = VideoSmartRuleField.HasBoundSubtitle,
                    Match = VideoSmartRuleMatch.IsTrue,
                },
            ];
        }

        var value = SmartRuleValueDraft.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? []
            : [new VideoSmartRule(SelectedSmartRuleField, value)];
    }

    private static VideoItem CloneForPlayback(VideoItem video, double lastPositionSeconds) =>
        new()
        {
            Id = video.Id,
            Title = video.Title,
            FilePath = video.FilePath,
            SubtitlePath = video.SubtitlePath,
            ImportedAt = video.ImportedAt,
            LastOpenedAt = video.LastOpenedAt,
            LastPositionSeconds = lastPositionSeconds,
            DurationSeconds = video.DurationSeconds,
            ManualSortOrder = video.ManualSortOrder,
            FileSizeBytes = video.FileSizeBytes,
            ModifiedAt = video.ModifiedAt,
            SourceFolderPath = video.SourceFolderPath,
            SourceId = video.SourceId,
            LastSeenAt = video.LastSeenAt,
            PosterPath = video.PosterPath,
            ThumbnailPath = video.ThumbnailPath,
            Tags = video.Tags,
            CollectionName = video.CollectionName,
            IsFavorite = video.IsFavorite,
            IsWatched = video.IsWatched,
            SubtitleSelectionKind = video.SubtitleSelectionKind,
            SubtitleSelectionPath = video.SubtitleSelectionPath,
            SubtitleSelectionTrackId = video.SubtitleSelectionTrackId,
            SubtitleSelectionTrackName = video.SubtitleSelectionTrackName,
            ProfileId = video.ProfileId,
            ProviderId = video.ProviderId,
            RemoteId = video.RemoteId,
            OriginalUrl = video.OriginalUrl,
            CanonicalUrl = video.CanonicalUrl,
            RemoteThumbnailUrl = video.RemoteThumbnailUrl,
            RemoteSubtitleLanguage = video.RemoteSubtitleLanguage,
        };

    private void RebuildManualCollectionOptions()
    {
        var selectedId = SelectedVideo?.Video.Id;
        ManualCollectionOptions = new ObservableCollection<VideoCollectionMembershipOption>(
            _collections
                .Where(collection => collection.Kind == VideoCollectionKind.Manual)
                .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(collection => new VideoCollectionMembershipOption(
                    collection,
                    selectedId != null && collection.ItemIds.Contains(selectedId, StringComparer.OrdinalIgnoreCase))));
    }

    private void RestoreSelectedVideoDetails()
    {
        var selectedId = SelectedVideo?.Video.Id;
        if (selectedId == null)
            return;
        var item = Videos.FirstOrDefault(video =>
            string.Equals(video.Video.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (item != null)
            SelectVideoDetails(item);
    }

    private void SelectFirstRemainingVideo()
    {
        var item = Videos.FirstOrDefault(video => _selectedVideoIds.Contains(video.Video.Id));
        if (item == null)
            SelectedVideo = null;
        else
            SelectVideoDetails(item);
    }

    private bool IsCoveredByAnyCollection(VideoItem video) =>
        _collections.Count == 0
            ? !string.IsNullOrWhiteSpace(video.CollectionName)
            : _collections.Any(collection => MatchesCollection(video, collection.Id));

    private bool MatchesCollection(VideoItem video, string collectionId)
    {
        var collection = _collections.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, collectionId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Name, collectionId, StringComparison.OrdinalIgnoreCase));

        if (collection == null)
            return string.Equals(video.CollectionName, collectionId, StringComparison.OrdinalIgnoreCase);

        if (collection.Kind == VideoCollectionKind.Manual && collection.ItemIds.Count > 0)
            return collection.ItemIds.Contains(video.Id, StringComparer.OrdinalIgnoreCase);

        if (collection.Kind == VideoCollectionKind.Smart && collection.SmartRules.Count > 0)
            return Niratan.Services.Video.VideoSmartCollectionMatcher.Matches(video, collection.SmartRules);

        return string.Equals(video.CollectionName, collection.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SeriesName(VideoItem video) =>
        !string.IsNullOrWhiteSpace(video.CatalogSeriesTitle)
            ? video.CatalogSeriesTitle
            : !string.IsNullOrWhiteSpace(video.CollectionName)
            ? video.CollectionName
            : string.IsNullOrWhiteSpace(video.SourceFolderPath)
                ? null
                : Path.GetFileName(video.SourceFolderPath);

    private async Task GenerateMissingThumbnailsForVisibleVideosAsync(CancellationToken token)
    {
        try
        {
            var visibleRows = Videos.Take(24).ToList();

            foreach (var row in visibleRows)
            {
                token.ThrowIfCancellationRequested();
                var result = await _thumbnailService.EnsureThumbnailAsync(
                    row.Video,
                    generateIfMissing: true,
                    token);
                if (string.IsNullOrWhiteSpace(result))
                    continue;

                row.ApplyGeneratedThumbnail(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed record VideoLibrarySortOptionItem(
    VideoLibrarySortOption Value,
    string DisplayName);

public sealed record VideoLibraryMediaTypeOption(
    VideoLibraryMediaType Value,
    string DisplayName);

public sealed record VideoSmartRuleFieldOption(
    VideoSmartRuleField Value,
    string DisplayName);

public sealed record VideoSmartRuleMatchOption(
    VideoSmartRuleMatch Value,
    string DisplayName);

public sealed record VideoLibraryFilterRow(
    string Key,
    string DisplayName,
    string MetadataText,
    VideoCollectionKind? CollectionKind = null);

public sealed partial class VideoLibrarySourceSummary : ObservableObject
{
    private static readonly Dictionary<string, string> ProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = "local",
        ["tmdb"] = "tmdb",
        ["tvmaze"] = "tvmaze",
        ["anilist"] = "anilist",
        ["anidb"] = "anidb",
        ["bangumi"] = "bangumi",
        ["tvdb"] = "tvdb",
    };

    public VideoLibrarySourceSummary(VideoLibrarySource source, int itemCount, int inProgressCount, int missingCount)
    {
        Source = source;
        ItemCount = itemCount;
        InProgressCount = inProgressCount;
        MissingCount = missingCount;
        MediaTypeDraft = source.MediaType;
        ProviderOrderDraft = string.Join(", ", source.ProviderOrder);
    }

    public VideoLibrarySource Source { get; }
    public int ItemCount { get; }
    public int InProgressCount { get; }
    public int MissingCount { get; }
    public VideoLibraryMediaType? MediaTypeDraft { get; set; }
    public int MediaTypeSelectedIndex
    {
        get => (int)(MediaTypeDraft ?? VideoLibraryMediaType.Auto);
        set
        {
            if (Enum.IsDefined(typeof(VideoLibraryMediaType), value))
                MediaTypeDraft = (VideoLibraryMediaType)value;
        }
    }
    public string ProviderOrderDraft { get; set; }
    public string StatusText => ResourceStringHelper.FormatString(
        "VideoLibrarySourceStatusFormat",
        "{0} videos · {1} in progress · {2} missing",
        ItemCount, InProgressCount, MissingCount);
    public string LastScannedText => Source.LastScannedAt.HasValue
        ? ResourceStringHelper.FormatString(
            "VideoLibrarySourceLastScannedFormat",
            "Last scanned {0:g}",
            Source.LastScannedAt.Value.ToLocalTime())
        : ResourceStringHelper.GetString("VideoLibrarySourceNeverScanned", "Never scanned");
    public string ScrapeSummaryText => _lastMetadataProgress is { } progress
        ? ResourceStringHelper.FormatString(
            "VideoLibrarySourceLastScrapeFormat",
            "Last scrape ({0}): {1} success · {2} pending · {3} failed",
            FormatMetadataState(progress.State),
            Math.Max(0, progress.ProcessedCount - progress.NeedsReviewCount - progress.FailedCount),
            progress.NeedsReviewCount,
            progress.FailedCount)
        : ResourceStringHelper.GetString("VideoLibrarySourceNeverScraped", "Never scraped");
    public bool HasError => !string.IsNullOrWhiteSpace(Source.LastError);
    public string ProviderOrderText => Source.ProviderOrder.Count == 0
        ? ResourceStringHelper.GetString("VideoLibrarySourceDefaultProviderRoute", "Default provider route")
        : string.Join(" → ", Source.ProviderOrder);

    [ObservableProperty]
    public partial bool IsScanProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsScanIndeterminate { get; set; }

    [ObservableProperty]
    public partial double ScanProgressValue { get; set; }

    [ObservableProperty]
    public partial string ScanProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string CurrentItemText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMetadataProgressVisible { get; set; }

    [ObservableProperty]
    public partial bool IsMetadataIndeterminate { get; set; }

    [ObservableProperty]
    public partial double MetadataProgressValue { get; set; }

    [ObservableProperty]
    public partial string MetadataProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string MetadataErrorText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSourceSettingsExpanded { get; set; }

    private VideoMetadataBatchProgress? _lastMetadataProgress;

    public bool HasMetadataError => !string.IsNullOrWhiteSpace(MetadataErrorText);

    public void UpdateProgress(VideoLibraryScanProgress progress)
    {
        IsScanProgressVisible = progress.State is VideoCatalogJobState.Running or VideoCatalogJobState.Paused;
        IsScanIndeterminate = progress.TotalCount is not > 0;
        ScanProgressValue = progress.TotalCount is > 0
            ? Math.Clamp(progress.ProcessedCount * 100d / progress.TotalCount.Value, 0, 100)
            : 0;
        ScanProgressText = FormatProgress(progress);
        CurrentItemText = string.IsNullOrWhiteSpace(progress.CurrentPath)
            ? ""
            : Path.GetFileName(progress.CurrentPath);
    }

    public static string FormatProgress(VideoLibraryScanProgress progress)
    {
        var stage = progress.Stage switch
        {
            VideoLibraryScanStage.Enumerating => ResourceStringHelper.GetString(
                "VideoLibraryScanStageEnumerating", "Discovering files"),
            VideoLibraryScanStage.Analyzing => ResourceStringHelper.GetString(
                "VideoLibraryScanStageAnalyzing", "Reading metadata"),
            VideoLibraryScanStage.Committing => ResourceStringHelper.GetString(
                "VideoLibraryScanStageCommitting", "Saving catalog"),
            _ => ResourceStringHelper.GetString("VideoLibraryScanStageCompleted", "Scan complete"),
        };
        var count = progress.TotalCount is > 0
            ? $"{progress.ProcessedCount:N0} / {progress.TotalCount.Value:N0}"
            : $"{progress.ProcessedCount:N0}";
        var speed = progress.ItemsPerSecond > 0.05
            ? ResourceStringHelper.FormatString(
                "VideoLibraryScanRateFormat", "{0:0.0} items/s", progress.ItemsPerSecond)
            : "";
        return string.Join(" · ", new[] { stage, count, speed }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public void UpdateMetadataProgress(VideoMetadataBatchProgress progress)
    {
        _lastMetadataProgress = progress;
        IsMetadataProgressVisible = progress.State is VideoCatalogJobState.Running
            or VideoCatalogJobState.Queued
            or VideoCatalogJobState.Failed;
        IsMetadataIndeterminate = progress.TotalCount <= 0
                                  && progress.State is VideoCatalogJobState.Running or VideoCatalogJobState.Queued;
        MetadataProgressValue = progress.TotalCount > 0
            ? Math.Clamp(progress.ProcessedCount * 100d / progress.TotalCount, 0, 100)
            : 0;
        var state = progress.State switch
        {
            VideoCatalogJobState.Queued => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageQueued", "Metadata refresh queued"),
            VideoCatalogJobState.Completed => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageCompleted", "Metadata refresh complete"),
            VideoCatalogJobState.Cancelled => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageCancelled", "Metadata refresh cancelled"),
            VideoCatalogJobState.Failed => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageFailed", "Metadata refresh failed"),
            VideoCatalogJobState.Interrupted => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageInterrupted", "Metadata refresh interrupted"),
            _ => ResourceStringHelper.GetString(
                "VideoMetadataBatchStageRunning", "Scraping metadata in background"),
        };
        MetadataProgressText = ResourceStringHelper.FormatString(
            "VideoMetadataSourceProgressFormat",
            "{0} · {1} / {2} · {3} success · {4} pending · {5} failed",
            state,
            progress.ProcessedCount,
            progress.TotalCount,
            Math.Max(0, progress.ProcessedCount - progress.NeedsReviewCount - progress.FailedCount),
            progress.NeedsReviewCount,
            progress.FailedCount);
        MetadataErrorText = progress.Error ?? "";
        OnPropertyChanged(nameof(HasMetadataError));
        OnPropertyChanged(nameof(ScrapeSummaryText));
    }

    private static string FormatMetadataState(VideoCatalogJobState state) => state switch
    {
        VideoCatalogJobState.Queued => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageQueued", "Metadata refresh queued"),
        VideoCatalogJobState.Completed => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageCompleted", "Metadata refresh complete"),
        VideoCatalogJobState.Cancelled => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageCancelled", "Metadata refresh cancelled"),
        VideoCatalogJobState.Failed => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageFailed", "Metadata refresh failed"),
        VideoCatalogJobState.Interrupted => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageInterrupted", "Metadata refresh interrupted"),
        _ => ResourceStringHelper.GetString(
            "VideoMetadataBatchStageRunning", "Scraping metadata in background"),
    };

    public bool TryApplyProviderOrder(out string? invalidProvider)
    {
        var parsed = ProviderOrderDraft
            .Split([',', ';', '\n', '\r', '→'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        invalidProvider = parsed.FirstOrDefault(provider => !ProviderIds.ContainsKey(provider));
        if (invalidProvider != null)
            return false;
        Source.ProviderOrder = parsed
            .Select(provider => ProviderIds[provider])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Source.MediaType = MediaTypeDraft ?? Source.MediaType;
        ProviderOrderDraft = string.Join(", ", Source.ProviderOrder);
        return true;
    }
}

public sealed partial class VideoCollectionMembershipOption : ObservableObject
{
    public VideoCollectionMembershipOption(VideoCollection collection, bool isIncluded)
    {
        Collection = collection;
        IsIncluded = isIncluded;
    }

    public VideoCollection Collection { get; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }
}

public sealed partial class VideoSmartRuleDraft : ObservableObject
{
    public VideoSmartRuleDraft(
        VideoSmartRuleField field,
        VideoSmartRuleMatch match,
        string value)
    {
        Field = field;
        Match = match;
        Value = value;
    }

    [ObservableProperty]
    public partial VideoSmartRuleField Field { get; set; }

    [ObservableProperty]
    public partial VideoSmartRuleMatch Match { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }
}
