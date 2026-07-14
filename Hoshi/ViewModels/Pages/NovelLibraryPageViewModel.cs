using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Helpers;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Components;

namespace Hoshi.ViewModels.Pages;

public partial class NovelLibraryPageViewModel : ObservableObject
    , IRecipient<NovelLibraryChangedMessage>
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;
    private readonly ISasayakiMatchService _sasayakiMatchService;
    private readonly ISettingsService _settingsService;
    private readonly INovelShelfService _shelfService;
    private readonly ITtuSyncService _ttuSyncService;
    private NovelShelfState _currentShelfState = new([], []);
    private readonly ITtuSyncRemoteStore _ttuSyncRemoteStore;
    private readonly ITtuBookImportService _ttuBookImportService;
    private readonly IGoogleDriveAuthService _googleDriveAuthService;
    private readonly IGoogleDriveCoverCacheService _googleDriveCoverCacheService;
    private readonly SemaphoreSlim _remoteCoverGate = new(6, 6);
    private readonly SemaphoreSlim _remoteImportGate = new(3, 3);
    private readonly SemaphoreSlim _catalogRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _pageCts = new();
    private readonly ConcurrentDictionary<string, byte> _activeNovelSyncs = new();
    private CancellationTokenSource? _catalogLoadCts;
    private CancellationTokenSource? _remoteListCts;
    private bool _suppressSortApplication;
    private bool _hasExplicitSortSelection;

    public bool IsBookSyncing => !_activeNovelSyncs.IsEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial ObservableCollection<NovelBookItemViewModel> NovelBooks { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<RemoteNovelBookItemViewModel> RemoteBooks { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelShelfSectionViewModel> ShelfSections { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNovelStorageWarnings))]
    public partial ObservableCollection<string> NovelStorageWarnings { get; set; } = new();

    [ObservableProperty]
    public partial NovelLibrarySortOption SelectedSortOption { get; set; } = NovelLibrarySortOption.Recent;

    [ObservableProperty]
    public partial NovelLibrarySortOptionItem? SelectedSortOptionItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBookshelf))]
    public partial bool ShowStatisticsDashboard { get; set; }

    public bool ShowBookshelf => !ShowStatisticsDashboard;
    public NovelStatisticsDashboardViewModel StatisticsDashboard { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial bool IsContentLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRemoteBooksLoading { get; set; }

    public IReadOnlyList<NovelLibrarySortOptionItem> SortOptions { get; } =
    [
        new(NovelLibrarySortOption.Recent, "Recent"),
        new(NovelLibrarySortOption.Title, "Title"),
        new(NovelLibrarySortOption.Manual, "Manual"),
    ];

    public bool NoNovels => !IsContentLoading && NovelBooks.Count == 0;
    public bool HasNovelStorageWarnings => NovelStorageWarnings.Count > 0;
    public bool ShowAutomaticBookSyncAction =>
        _settingsService.Current.TtuSyncSettings.EnableSync
        && _settingsService.Current.TtuSyncSettings.SyncMode == TtuSettingsSyncMode.Auto;
    public bool ShowManualBookSyncAction =>
        _settingsService.Current.TtuSyncSettings.EnableSync
        && _settingsService.Current.TtuSyncSettings.SyncMode == TtuSettingsSyncMode.Manual;

    public NovelLibraryPageViewModel(
        INovelLibraryService novelLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IMessenger messenger,
        ISasayakiMatchService sasayakiMatchService,
        ISettingsService settingsService,
        NovelStatisticsDashboardViewModel statisticsDashboard,
        INovelShelfService shelfService,
        ITtuSyncService ttuSyncService,
        ITtuSyncRemoteStore ttuSyncRemoteStore,
        ITtuBookImportService ttuBookImportService,
        IGoogleDriveAuthService googleDriveAuthService,
        IGoogleDriveCoverCacheService googleDriveCoverCacheService
    )
    {
        _novelLibraryService = novelLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _messenger = messenger;
        _sasayakiMatchService = sasayakiMatchService;
        _settingsService = settingsService;
        StatisticsDashboard = statisticsDashboard;
        _shelfService = shelfService;
        _ttuSyncService = ttuSyncService;
        _ttuSyncRemoteStore = ttuSyncRemoteStore;
        _ttuBookImportService = ttuBookImportService;
        _googleDriveAuthService = googleDriveAuthService;
        _googleDriveCoverCacheService = googleDriveCoverCacheService;
        _suppressSortApplication = true;
        SelectedSortOption = _settingsService.Current.NovelLibrarySortOption;
        SelectedSortOptionItem = FindSortOptionItem(SelectedSortOption);
        _suppressSortApplication = false;
        _messenger.RegisterAll(this);
    }

    public async Task InitializeAsync()
    {
        RestoreSelectedSortOption();
        await LoadNovelsAsync();
    }

    public void Receive(NovelLibraryChangedMessage message) => _ = LoadNovelsAsync();

    public void OnNavigatedFrom()
    {
        StatisticsDashboard.Deactivate();
        _pageCts.Cancel();
        _catalogLoadCts?.Cancel();
        _remoteListCts?.Cancel();
    }

    [RelayCommand]
    private async Task ImportNovelAsync()
    {
        var filePath = await _dialogService.OpenFilePickerAsync(".epub");
        if (filePath == null)
            return;

        var result = await _novelLibraryService.ImportEpubAsync(filePath);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("EPUB imported.", "Novel imported");
        await LoadNovelsAsync();
    }

    [RelayCommand]
    private async Task ImportDroppedNovelsAsync(IEnumerable<string> filePaths)
    {
        var epubPaths = filePaths
            .Where(path => string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (epubPaths.Count == 0)
            return;

        var importedCount = 0;
        foreach (var filePath in epubPaths)
        {
            var result = await _novelLibraryService.ImportEpubAsync(filePath, _pageCts.Token);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                    _notificationService.ShowError(result.Error!, result.ErrorTitle!);
                continue;
            }

            importedCount++;
        }

        if (importedCount > 0)
        {
            _notificationService.ShowSuccess(
                importedCount == 1 ? "EPUB imported." : $"{importedCount} EPUBs imported.",
                "Novel imported");
            await LoadNovelsAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteBooksAsync()
    {
        if (!_settingsService.Current.TtuSyncSettings.EnableSync || !_googleDriveAuthService.HasCredentials)
        {
            _notificationService.ShowError(
                "Connect Google Drive in ッツ Sync settings before refreshing cloud books.",
                "Sync unavailable");
            return;
        }

        CancellationTokenSource? remoteListCts = null;
        try
        {
            IsRemoteBooksLoading = true;
            _remoteListCts?.Cancel();
            _remoteListCts?.Dispose();
            remoteListCts = CancellationTokenSource.CreateLinkedTokenSource(
                _pageCts.Token);
            _remoteListCts = remoteListCts;
            var remoteBooks = await _ttuSyncRemoteStore.ListRemoteBooksAsync(
                remoteListCts.Token);
            var localTitles = NovelBooks
                .Select(item => TtuSyncFileNames.SanitizeTtuFilename(item.Book.Title))
                .ToHashSet(StringComparer.Ordinal);
            var items = remoteBooks
                .Where(book => !localTitles.Contains(book.SanitizedTitle))
                .Select(book => new RemoteNovelBookItemViewModel(book))
                .ToList();
            RemoteBooks = new ObservableCollection<RemoteNovelBookItemViewModel>(
                items);
            RebuildShelfProjections(
                _currentShelfState,
                NovelBooks.Select(item => item.Book).ToList());
            await HydrateRemoteCoversAsync(items, remoteListCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                $"Failed to fetch books from Google Drive: {ex.Message}",
                "Sync failed");
        }
        finally
        {
            if (ReferenceEquals(_remoteListCts, remoteListCts))
            {
                IsRemoteBooksLoading = false;
                _remoteListCts = null;
                remoteListCts?.Dispose();
            }
        }
    }

    private async Task HydrateRemoteCoversAsync(
        IReadOnlyList<RemoteNovelBookItemViewModel> items,
        CancellationToken ct)
    {
        var tasks = items.Select(async item =>
        {
            await _remoteCoverGate.WaitAsync(ct);
            try
            {
                var path = await _googleDriveCoverCacheService.GetCoverPathAsync(
                    item.Book.Files.Cover,
                    ct);
                item.ApplyCoverPath(path);
            }
            finally
            {
                _remoteCoverGate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadRemoteBookAsync(RemoteNovelBookItemViewModel item)
    {
        if (item == null || item.DownloadState is
            RemoteNovelDownloadState.Queued or RemoteNovelDownloadState.Downloading)
            return;

        item.DownloadState = RemoteNovelDownloadState.Queued;
        var enteredGate = false;
        try
        {
            await _remoteImportGate.WaitAsync(_pageCts.Token);
            enteredGate = true;
            item.DownloadState = RemoteNovelDownloadState.Downloading;
            item.DownloadProgress = 0;
            var progress = new Progress<double>(value => item.DownloadProgress = Math.Clamp(value, 0, 1));
            var settings = _settingsService.Current;
            var result = await _ttuBookImportService.ImportRemoteBookAsync(
                item.Book,
                new TtuBookImportOptions(
                    SyncStatistics: settings.StatisticsSettings.EnableSync,
                    SyncAudioBook: settings.SasayakiSettings.EnableSync,
                    StatisticsSyncMode: settings.StatisticsSettings.SyncMode),
                progress,
                _pageCts.Token);

            if (!result.IsSuccess)
            {
                item.DownloadState = result.IsCancelled
                    ? RemoteNovelDownloadState.Idle
                    : RemoteNovelDownloadState.Failed;
                if (!result.IsCancelled)
                    _notificationService.ShowError(result.Error!, result.ErrorTitle!);
                return;
            }

            RemoteBooks.Remove(item);
            RebuildShelfProjections(
                _currentShelfState,
                NovelBooks.Select(book => book.Book).ToList());
            _notificationService.ShowSuccess("EPUB imported from Google Drive.", "Novel imported");
            await RefreshCatalogAfterImportAsync();
        }
        catch (OperationCanceledException)
        {
            item.DownloadState = RemoteNovelDownloadState.Idle;
        }
        catch (Exception ex)
        {
            item.DownloadState = RemoteNovelDownloadState.Failed;
            _notificationService.ShowError(
                $"Failed to import book from Google Drive: {ex.Message}",
                "Import failed");
        }
        finally
        {
            if (item.DownloadState == RemoteNovelDownloadState.Downloading)
                item.DownloadState = RemoteNovelDownloadState.Idle;
            if (enteredGate)
                _remoteImportGate.Release();
        }
    }

    [RelayCommand]
    private async Task DeleteRemoteBookAsync(RemoteNovelBookItemViewModel item)
    {
        if (item == null || item.DownloadState is
            RemoteNovelDownloadState.Queued or RemoteNovelDownloadState.Downloading)
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Delete Google Drive book",
            $"Move '{item.Book.Title}' and its sync data to the Google Drive trash?");
        if (!confirmed)
            return;

        try
        {
            await _ttuSyncRemoteStore.TrashRemoteBookAsync(item.Book, _pageCts.Token);
            RemoteBooks.Remove(item);
            RebuildShelfProjections(
                _currentShelfState,
                NovelBooks.Select(book => book.Book).ToList());
            _notificationService.ShowSuccess(
                "Remote book moved to Google Drive trash.",
                "Google Drive book deleted");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, "Delete Google Drive book failed");
        }
    }

    private async Task RefreshCatalogAfterImportAsync()
    {
        await _catalogRefreshGate.WaitAsync(_pageCts.Token);
        try
        {
            await LoadNovelsAsync();
        }
        finally
        {
            _catalogRefreshGate.Release();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SyncNovelAsync(NovelBookItemViewModel item) =>
        SyncNovelCoreAsync(item, TtuSyncDirection.Auto);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ImportNovelFromTtuAsync(NovelBookItemViewModel item) =>
        SyncNovelCoreAsync(item, TtuSyncDirection.ImportFromTtu);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ExportNovelAsync(NovelBookItemViewModel item) =>
        SyncNovelCoreAsync(item, TtuSyncDirection.ExportToTtu);

    private async Task SyncNovelCoreAsync(
        NovelBookItemViewModel item,
        TtuSyncDirection direction)
    {
        if (item == null)
            return;

        var current = _settingsService.Current;
        var global = current.TtuSyncSettings;
        if (!global.EnableSync || !_googleDriveAuthService.HasCredentials)
        {
            _notificationService.ShowError(
                ResourceStringHelper.GetString(
                    "NovelBookSyncUnavailableMessage",
                    "Enable ッツ Sync and connect Google Drive before syncing a book."),
                ResourceStringHelper.GetString(
                    "NovelBookSyncUnavailableTitle",
                    "Sync unavailable"));
            return;
        }

        if (!_activeNovelSyncs.TryAdd(item.Book.Id, 0))
            return;

        OnPropertyChanged(nameof(IsBookSyncing));

        var options = new TtuSyncOptions(
            Direction: direction,
            SyncBookData: global.UploadBooks,
            SyncStatistics: current.StatisticsSettings.EnableSync,
            StatisticsSyncMode: current.StatisticsSettings.SyncMode,
            SyncAudioBook: current.SasayakiSettings.EnableSasayaki
                && current.SasayakiSettings.EnableSync);

        try
        {
            var result = await _ttuSyncService.SyncBookAsync(
                item.Book,
                options,
                _pageCts.Token);
            switch (result.Kind)
            {
                case TtuSyncResultKind.Synced:
                    _notificationService.ShowSuccess(
                        ResourceStringHelper.FormatString(
                            "NovelBookAlreadySyncedFormat",
                            "{0} is already synced.",
                            result.Title));
                    break;
                case TtuSyncResultKind.Imported:
                    await LoadNovelsAsync();
                    _notificationService.ShowSuccess(
                        ResourceStringHelper.FormatString(
                            "NovelBookSyncedFromTtuFormat",
                            "Synced {0} from ッツ ({1} characters).",
                            result.Title,
                            result.CharacterCount));
                    break;
                case TtuSyncResultKind.Exported:
                    _notificationService.ShowSuccess(
                        ResourceStringHelper.FormatString(
                            "NovelBookSyncedToTtuFormat",
                            "Synced {0} to ッツ ({1} characters).",
                            result.Title,
                            result.CharacterCount));
                    break;
                case TtuSyncResultKind.Skipped:
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                ResourceStringHelper.FormatString(
                    "NovelBookSyncFailedFormat",
                    "Sync failed: {0}",
                    ex.Message),
                ResourceStringHelper.GetString(
                    "NovelBookSyncFailedTitle",
                    "Sync failed"));
        }
        finally
        {
            if (_activeNovelSyncs.TryRemove(item.Book.Id, out _))
                OnPropertyChanged(nameof(IsBookSyncing));
        }
    }

    [RelayCommand]
    private void OpenNovel(NovelBookItemViewModel item)
    {
        _messenger.Send(
            new SwitchAppModeMessage(AppMode.NovelReader, new NovelReaderNavigationArgs(item.Book.Id))
        );
    }

    [RelayCommand]
    private async Task EnterStatisticsAsync()
    {
        ShowStatisticsDashboard = true;
        await Task.Yield();
        await StatisticsDashboard.ActivateAsync(
            NovelBooks.Select(item => item.Book).ToList(),
            _currentShelfState,
            _pageCts.Token);
    }

    [RelayCommand]
    private void ReturnToBookshelf()
    {
        StatisticsDashboard.Deactivate();
        ShowStatisticsDashboard = false;
    }

    [RelayCommand]
    private async Task MoveNovelBeforeAsync(NovelBookMoveRequest request)
    {
        if (request.SourceBookId == request.TargetBookId)
            return;

        var source = NovelBooks.FirstOrDefault(item => item.Book.Id == request.SourceBookId);
        if (source == null)
            return;

        SetManualSortOptionWithoutReapplying();

        var sourceIndex = NovelBooks.IndexOf(source);
        NovelBooks.Remove(source);
        var targetIndex = NovelBooks
            .Select((item, index) => new { item.Book.Id, index })
            .FirstOrDefault(item => item.Id == request.TargetBookId)
            ?.index;
        if (targetIndex == null)
        {
            NovelBooks.Insert(sourceIndex, source);
            return;
        }

        NovelBooks.Insert(targetIndex.Value, source);
        await SaveCurrentManualOrderCoreAsync();
    }

    [RelayCommand]
    private async Task SaveCurrentManualOrderAsync()
    {
        SetManualSortOptionWithoutReapplying();
        await SaveCurrentManualOrderCoreAsync();
    }

    [RelayCommand]
    private async Task MoveBooksToShelfAsync(NovelShelfMoveRequest request)
    {
        var result = await _shelfService.MoveBooksAsync(
            request.BookIds,
            request.TargetShelfName,
            _pageCts.Token);
        ApplyShelfResult(result);
    }

    [RelayCommand]
    private async Task MoveBookAsync(NovelBookShelfMoveRequest request)
    {
        var result = await _shelfService.MoveBooksAsync(
            [request.BookId],
            request.TargetShelfName,
            _pageCts.Token);
        ApplyShelfResult(result);
    }

    [RelayCommand]
    private async Task ReorderShelfBookAsync(NovelShelfBookReorderRequest request)
    {
        var result = await _shelfService.ReorderBookAsync(
            request.SourceBookId,
            request.TargetBookId,
            request.ShelfName,
            _pageCts.Token);
        ApplyShelfResult(result);
    }

    [RelayCommand]
    private async Task MatchSasayakiAsync(NovelBookItemViewModel item)
    {
        var audioPath = await _dialogService.OpenFilePickerAsync(".mp3", ".m4b", ".m4a", ".wav", ".flac", ".ogg");
        if (audioPath == null)
            return;

        var subtitlePath = await _dialogService.OpenFilePickerAsync(".srt", ".vtt");
        if (subtitlePath == null)
            return;

        try
        {
            var match = await _sasayakiMatchService.MatchAsync(
                item.Book,
                audioPath,
                subtitlePath,
                _settingsService.Current.SasayakiSettings.SearchWindowSize,
                _pageCts.Token);
            _notificationService.ShowSuccess(
                $"{match.Matches.Count}/{match.Cues.Count} cues matched.",
                "Sasayaki matched");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, "Sasayaki match failed");
        }
    }

    [RelayCommand]
    private async Task DeleteNovelAsync(NovelBookItemViewModel item)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Delete novel",
            $"Delete '{item.Book.Title}'? This cannot be undone."
        );
        if (!confirmed)
            return;

        var result = await _novelLibraryService.DeleteNovelAsync(item.Book.Id);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("Novel deleted.");
        await LoadNovelsAsync();
    }

    private async Task LoadNovelsAsync()
    {
        _catalogLoadCts?.Cancel();
        _catalogLoadCts?.Dispose();
        var loadCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        _catalogLoadCts = loadCts;
        var ct = loadCts.Token;
        IsContentLoading = true;
        try
        {
            var result = await _novelLibraryService.GetNovelBooksAsync(ct: ct);

            if (result.IsSuccess)
            {
                var books = result.Value!.Books;
                NovelStorageWarnings = new ObservableCollection<string>(
                    result.Value.CorruptMetadataPaths);
                NovelBooks = new ObservableCollection<NovelBookItemViewModel>(
                    SortBooks(books).Select(book => new NovelBookItemViewModel(book)));
                var shelfResult = await _shelfService.LoadAsync(ct);
                if (shelfResult.IsSuccess)
                    RebuildShelfProjections(shelfResult.Value!, books);
                else
                {
                    RebuildShelfProjections(
                        new NovelShelfState([], books.Select(book => book.Id).ToList()),
                        books);
                    if (!shelfResult.IsCancelled)
                        _notificationService.ShowError(
                            shelfResult.Error!,
                            shelfResult.ErrorTitle ?? "Shelf load failed");
                }
                if (ShowStatisticsDashboard)
                {
                    await StatisticsDashboard.ActivateAsync(
                        books,
                        _currentShelfState,
                        ct);
                }
            }
            else if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_catalogLoadCts, loadCts))
            {
                IsContentLoading = false;
                _catalogLoadCts = null;
                loadCts.Dispose();
            }
        }
    }

    private void ApplyShelfResult(Result<NovelShelfState> result)
    {
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(
                    result.Error!,
                    result.ErrorTitle ?? "Shelf update failed");
            return;
        }

        RebuildShelfProjections(
            result.Value!,
            NovelBooks.Select(item => item.Book).ToList());
    }

    private void RebuildShelfProjections(
        NovelShelfState state,
        IReadOnlyList<NovelBook> books)
    {
        _currentShelfState = state;
        var booksById = books.ToDictionary(book => book.Id, StringComparer.Ordinal);
        var sections = new List<NovelShelfSectionViewModel>();
        var reading = SortBooks(books.Where(IsReading)).ToList();
        if (reading.Count > 0)
        {
            sections.Add(new NovelShelfSectionViewModel
            {
                Id = "reading",
                DisplayName = ResourceStringHelper.GetString(
                    "NovelShelfReadingLabel/Text",
                    "Reading"),
                IsDerived = true,
                Books = new(reading.Select(book => new NovelBookItemViewModel(book))),
            });
        }

        foreach (var shelf in state.Shelves)
        {
            var shelfBooks = shelf.BookIds
                .Where(booksById.ContainsKey)
                .Select(id => booksById[id]);
            if (SelectedSortOption != NovelLibrarySortOption.Manual)
                shelfBooks = SortBooks(shelfBooks);
            sections.Add(new NovelShelfSectionViewModel
            {
                Id = "shelf:" + shelf.Name,
                DisplayName = shelf.Name,
                Books = new(shelfBooks.Select(book => new NovelBookItemViewModel(book))),
            });
        }

        if (RemoteBooks.Count > 0)
        {
            sections.Add(new NovelShelfSectionViewModel
            {
                Id = "google-drive",
                DisplayName = "Google Drive",
                IsDerived = true,
                IsRemote = true,
                RemoteBooks = RemoteBooks,
            });
        }

        var unshelvedBooks = state.UnshelvedBookOrder
            .Where(booksById.ContainsKey)
            .Select(id => booksById[id]);
        if (SelectedSortOption != NovelLibrarySortOption.Manual)
            unshelvedBooks = SortBooks(unshelvedBooks);
        sections.Add(new NovelShelfSectionViewModel
        {
            Id = "unshelved",
            DisplayName = ResourceStringHelper.GetString(
                "NovelShelfUnshelvedLabel/Text",
                "Unshelved"),
            IsUnshelved = true,
            Books = new(unshelvedBooks.Select(book => new NovelBookItemViewModel(book))),
        });

        ShelfSections = new(sections);
    }

    private static bool IsReading(NovelBook book) =>
        book.CurrentCharacterCount > 0
        && (book.TotalCharacterCount <= 0
            || book.CurrentCharacterCount < book.TotalCharacterCount);

    partial void OnSelectedSortOptionChanged(NovelLibrarySortOption value)
    {
        SelectedSortOptionItem = FindSortOptionItem(value);

        if (!_suppressSortApplication)
            _hasExplicitSortSelection = true;

        _settingsService.Set(settings => settings.NovelLibrarySortOption, value);
        _ = _settingsService.SaveAsync();

        if (!_suppressSortApplication)
            ApplyCurrentSort();
    }

    partial void OnSelectedSortOptionItemChanged(NovelLibrarySortOptionItem? value)
    {
        if (value is not null && SelectedSortOption != value.Value)
            SelectedSortOption = value.Value;
    }

    private void ApplyCurrentSort()
    {
        if (NovelBooks.Count == 0)
            return;

        NovelBooks = new ObservableCollection<NovelBookItemViewModel>(
            SortBookItems(NovelBooks));
        RebuildShelfProjections(
            _currentShelfState,
            NovelBooks.Select(item => item.Book).ToList());
    }

    private void RestoreSelectedSortOption()
    {
        if (_hasExplicitSortSelection)
            return;

        var restored = _settingsService.Current.NovelLibrarySortOption;
        if (!Enum.IsDefined(restored))
            restored = NovelLibrarySortOption.Recent;

        _suppressSortApplication = true;
        SelectedSortOption = restored;
        SelectedSortOptionItem = FindSortOptionItem(restored);
        _suppressSortApplication = false;
    }

    private NovelLibrarySortOptionItem? FindSortOptionItem(NovelLibrarySortOption option) =>
        SortOptions.FirstOrDefault(item => item.Value == option);

    private IEnumerable<NovelBook> SortBooks(IEnumerable<NovelBook> books) =>
        SelectedSortOption switch
        {
            NovelLibrarySortOption.Title => books
                .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(book => book.LastOpenedAt ?? book.ImportedAt),
            NovelLibrarySortOption.Manual => books
                .OrderBy(book => book.ManualSortOrder)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => books
                .OrderByDescending(book => book.LastOpenedAt ?? book.ImportedAt)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
        };

    private IEnumerable<NovelBookItemViewModel> SortBookItems(IEnumerable<NovelBookItemViewModel> items) =>
        SortBooks(items.Select(item => item.Book)).Select(book => new NovelBookItemViewModel(book));

    private void SetManualSortOptionWithoutReapplying()
    {
        _suppressSortApplication = true;
        SelectedSortOption = NovelLibrarySortOption.Manual;
        _suppressSortApplication = false;
    }

    private async Task SaveCurrentManualOrderCoreAsync()
    {
        var orderedBookIds = NovelBooks.Select(item => item.Book.Id).ToList();
        var result = await _novelLibraryService.SaveNovelBookOrderAsync(
            orderedBookIds,
            _pageCts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
    }

}

public sealed record NovelShelfMoveRequest(
    IReadOnlyList<string> BookIds,
    string? TargetShelfName);

public sealed record NovelBookShelfMoveRequest(
    string BookId,
    string? TargetShelfName);

public sealed record NovelShelfBookReorderRequest(
    string SourceBookId,
    string TargetBookId,
    string? ShelfName);

public sealed record NovelBookMoveRequest(string SourceBookId, string TargetBookId);

public sealed record NovelLibrarySortOptionItem(
    NovelLibrarySortOption Value,
    string DisplayName);
