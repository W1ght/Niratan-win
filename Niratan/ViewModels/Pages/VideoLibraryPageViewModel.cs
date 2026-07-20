using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Video;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public partial class VideoLibraryPageViewModel : ObservableObject
{
    private readonly IVideoLibraryService _videoLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IVideoPlayerWindowService _playerWindowService;
    private readonly IVideoThumbnailService _thumbnailService;
    private readonly IFileRevealService _fileRevealService;
    private CancellationTokenSource _cts = new();
    private List<VideoItem> _allVideos = [];
    private List<VideoCollection> _collections = [];
    private List<VideoLibrarySource> _sources = [];
    private readonly HashSet<string> _selectedVideoIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeFolderPath;
    private string? _activeCollectionId;
    private string? _activeSeriesName;
    private string? _activeTag;
    private bool _isSubscribedToPlayerLibraryChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
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
    public partial VideoLibraryView SelectedLibraryView { get; set; } = VideoLibraryView.All;

    [ObservableProperty]
    public partial VideoLibraryLayoutMode SelectedLayoutMode { get; set; } = VideoLibraryLayoutMode.List;

    [ObservableProperty]
    public partial string CurrentViewTitle { get; set; } = GetViewTitle(VideoLibraryView.All);

    [ObservableProperty]
    public partial string CurrentViewSubtitle { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
    public partial bool IsContentLoading { get; set; }

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
    public bool HasSelectedVideo => SelectedVideo != null;
    public bool HasSelection => SelectedVideoCount > 0;
    public bool HasSources => SourceSummaries.Count > 0;
    public bool IsListLayout => SelectedLayoutMode == VideoLibraryLayoutMode.List;
    public bool IsPosterLayout => SelectedLayoutMode == VideoLibraryLayoutMode.Posters;
    public bool IsSmartRuleValueVisible => SelectedSmartRuleField != VideoSmartRuleField.HasBoundSubtitle;
    public bool IsFoldersView => SelectedLibraryView == VideoLibraryView.Folders;
    public bool IsCollectionsView => SelectedLibraryView == VideoLibraryView.Collections;
    public bool IsTagsView => SelectedLibraryView == VideoLibraryView.Tags;
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
        IFileRevealService fileRevealService)
    {
        _videoLibraryService = videoLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _playerWindowService = playerWindowService;
        _thumbnailService = videoThumbnailService;
        _fileRevealService = fileRevealService;
    }

    public async Task InitializeAsync()
    {
        SubscribeToPlayerLibraryChanges();
        await LoadVideosAsync();
    }

    public void OnNavigatedFrom()
    {
        _cts.Cancel();
        UnsubscribeFromPlayerLibraryChanges();
    }

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
    }

    [RelayCommand]
    private async Task RefreshSourceAsync(VideoLibrarySourceSummary summary)
    {
        var result = await _videoLibraryService.RefreshSourceAsync(summary.Source.Id, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        await LoadVideosAsync();
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

        await _playerWindowService.OpenAsync(
            video,
            Videos.Select(video => video.Video).ToList(),
            _cts.Token);
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
        _activeFolderPath = null;
        _activeCollectionId = null;
        _activeSeriesName = null;
        _activeTag = null;
        ApplyVisibleVideos();
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
        CurrentViewTitle = GetViewTitle(value);
        OnPropertyChanged(nameof(IsFoldersView));
        OnPropertyChanged(nameof(IsCollectionsView));
        OnPropertyChanged(nameof(IsTagsView));
    }

    partial void OnSourceSummariesChanged(ObservableCollection<VideoLibrarySourceSummary> value) =>
        OnPropertyChanged(nameof(HasSources));

    private void ApplyVisibleVideos()
    {
        var filtered = FilterVideos(_allVideos).ToList();
        Videos = new ObservableCollection<VideoItemViewModel>(
            SortVideos(filtered).Select(video => new VideoItemViewModel(
                video,
                _selectedVideoIds.Contains(video.Id))));
        CurrentViewSubtitle = FormatVideoCount(Videos.Count);
        OnPropertyChanged(nameof(SmartCollectionPreviewRows));
        _ = GenerateMissingThumbnailsForVisibleVideosAsync(_cts.Token);
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
            VideoLibraryView.ContinueWatching => HasProgress(video) && !video.IsWatched,
            VideoLibraryView.Unwatched => !HasProgress(video) && !video.IsWatched,
            VideoLibraryView.Finished => video.IsWatched,
            VideoLibraryView.Watched => video.IsWatched,
            VideoLibraryView.Recent => video.LastOpenedAt.HasValue,
            VideoLibraryView.Favorites => video.IsFavorite,
            VideoLibraryView.NeedsReview => !IsCoveredByAnyCollection(video),
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
                return new VideoLibrarySourceSummary(
                    source,
                    sourceVideos.Count,
                    sourceVideos.Count(video => HasProgress(video) && !video.IsWatched),
                    sourceVideos.Count(video => !video.IsRemote && !File.Exists(video.FilePath)));
            }));
    }

    private static bool MatchesSearch(VideoItem video, string query) =>
        Contains(video.Title, query)
        || Contains(video.FilePath, query)
        || Contains(video.OriginalUrl, query)
        || Contains(video.SourceFolderPath, query)
        || Contains(video.CollectionName, query)
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
            VideoLibraryView.ContinueWatching => ResourceStringHelper.GetString(
                "VideoLibraryViewContinueWatching",
                "Continue Watching"),
            VideoLibraryView.Unwatched => ResourceStringHelper.GetString("VideoLibraryViewUnwatched", "Unwatched"),
            VideoLibraryView.Finished => ResourceStringHelper.GetString("VideoLibraryViewFinished", "Finished"),
            VideoLibraryView.Recent => ResourceStringHelper.GetString("VideoLibraryViewRecent", "Recent"),
            VideoLibraryView.Favorites => ResourceStringHelper.GetString("VideoLibraryViewFavorites", "Favorites"),
            VideoLibraryView.NeedsReview => ResourceStringHelper.GetString("VideoLibraryViewNeedsReview", "Needs Review"),
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
        !string.IsNullOrWhiteSpace(video.CollectionName)
            ? video.CollectionName
            : string.IsNullOrWhiteSpace(video.SourceFolderPath)
                ? null
                : Path.GetFileName(video.SourceFolderPath);

    private async Task GenerateMissingThumbnailsForVisibleVideosAsync(CancellationToken token)
    {
        try
        {
            var visibleVideos = Videos.Take(24).Select(row => row.Video).ToList();
            var savedThumbnail = false;

            foreach (var video in visibleVideos)
            {
                token.ThrowIfCancellationRequested();
                var result = await _thumbnailService.EnsureThumbnailAsync(video, generateIfMissing: true, token);
                if (string.IsNullOrWhiteSpace(result))
                    continue;

                if (IsPersistedGeneratedThumbnailChange(video, result))
                    savedThumbnail = true;
            }

            if (savedThumbnail && !token.IsCancellationRequested)
                await LoadVideosAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsPersistedGeneratedThumbnailChange(VideoItem video, string resultPath) =>
        !string.Equals(video.ThumbnailPath, resultPath, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(video.PosterPath, resultPath, StringComparison.OrdinalIgnoreCase);
}

public sealed record VideoLibrarySortOptionItem(
    VideoLibrarySortOption Value,
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

public sealed record VideoLibrarySourceSummary(
    VideoLibrarySource Source,
    int ItemCount,
    int InProgressCount,
    int MissingCount)
{
    public string StatusText => $"{ItemCount} videos • {InProgressCount} in progress • {MissingCount} missing";
    public string LastScannedText => Source.LastScannedAt.HasValue
        ? $"Last scanned {Source.LastScannedAt.Value.ToLocalTime():g}"
        : "Never scanned";
    public bool HasError => !string.IsNullOrWhiteSpace(Source.LastError);
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
