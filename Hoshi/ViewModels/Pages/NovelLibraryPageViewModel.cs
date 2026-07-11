using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private NovelShelfState _currentShelfState = new([], []);
    private readonly ITtuSyncRemoteStore _ttuSyncRemoteStore;
    private readonly ITtuBookImportService _ttuBookImportService;
    private readonly IGoogleDriveAuthService _googleDriveAuthService;
    private CancellationTokenSource _cts = new();
    private bool _suppressSortApplication;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial ObservableCollection<NovelBookItemViewModel> NovelBooks { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<RemoteNovelBookItemViewModel> RemoteBooks { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelShelfSectionViewModel> RailSections { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelBookItemViewModel> UnshelvedBooks { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNovelStorageWarnings))]
    public partial ObservableCollection<string> NovelStorageWarnings { get; set; } = new();

    [ObservableProperty]
    public partial NovelLibrarySortOption SelectedSortOption { get; set; } = NovelLibrarySortOption.Recent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBookshelf))]
    public partial bool ShowStatisticsDashboard { get; set; }

    public bool ShowBookshelf => !ShowStatisticsDashboard;
    public NovelStatisticsDashboardViewModel StatisticsDashboard { get; }

    public string StatisticsTodayText => StatisticsDashboard.TodayText;
    public string StatisticsWeekText => StatisticsDashboard.WeekText;
    public string StatisticsRangeText => StatisticsDashboard.RangeText;
    public string StatisticsSpeedText => StatisticsDashboard.SpeedText;
    public string StatisticsRangeTitle => StatisticsDashboard.RangeTitle;

    public NovelStatisticsRangeMode SelectedStatisticsRangeMode
    {
        get => StatisticsDashboard.SelectedRangeMode;
        set => StatisticsDashboard.SelectedRangeMode = value;
    }

    public DateTimeOffset? StatisticsAnchorDate
    {
        get => StatisticsDashboard.AnchorDate;
        set => StatisticsDashboard.AnchorDate = value;
    }

    public NovelStatisticsTrendGrain SelectedStatisticsTrendGrain
    {
        get => StatisticsDashboard.SelectedTrendGrain;
        set => StatisticsDashboard.SelectedTrendGrain = value;
    }

    public NovelStatisticsTrendMetric SelectedStatisticsTrendMetric
    {
        get => StatisticsDashboard.SelectedTrendMetric;
        set => StatisticsDashboard.SelectedTrendMetric = value;
    }

    public NovelStatisticsBookRankingMetric SelectedStatisticsRankingMetric
    {
        get => StatisticsDashboard.SelectedRankingMetric;
        set => StatisticsDashboard.SelectedRankingMetric = value;
    }

    public StatisticsDailyTargetType SelectedStatisticsDailyTargetType
    {
        get => StatisticsDashboard.SelectedDailyTargetType;
        set => StatisticsDashboard.SelectedDailyTargetType = value;
    }

    public int StatisticsDailyCharacterTarget => StatisticsDashboard.DailyCharacterTarget;

    public double StatisticsDailyCharacterTargetValue
    {
        get => StatisticsDashboard.DailyCharacterTargetValue;
        set => StatisticsDashboard.DailyCharacterTargetValue = value;
    }

    public int StatisticsDailyDurationTargetMinutes => StatisticsDashboard.DailyDurationTargetMinutes;

    public double StatisticsDailyDurationTargetMinutesValue
    {
        get => StatisticsDashboard.DailyDurationTargetMinutesValue;
        set => StatisticsDashboard.DailyDurationTargetMinutesValue = value;
    }

    public int StatisticsWeeklyTargetDays => StatisticsDashboard.WeeklyTargetDays;

    public double StatisticsWeeklyTargetDaysValue
    {
        get => StatisticsDashboard.WeeklyTargetDaysValue;
        set => StatisticsDashboard.WeeklyTargetDaysValue = value;
    }

    public ObservableCollection<NovelStatisticsTrendDisplayPoint> StatisticsTrendPoints => StatisticsDashboard.TrendPoints;
    public ObservableCollection<NovelStatisticsCalendarDayDisplay> StatisticsCalendarDays => StatisticsDashboard.CalendarDays;
    public ObservableCollection<NovelStatisticsBookRankingDisplayRow> StatisticsBookRankingRows => StatisticsDashboard.BookRankingRows;
    public ObservableCollection<NovelStatisticsShelfComparisonDisplayRow> StatisticsShelfComparisonRows => StatisticsDashboard.ShelfComparisonRows;
    public ObservableCollection<string> StatisticsSkippedCorruptBookIds => StatisticsDashboard.SkippedCorruptBookIds;

    public NovelStatisticsCalendarDayDisplay? SelectedStatisticsCalendarDay
    {
        get => StatisticsDashboard.SelectedCalendarDay;
        set => StatisticsDashboard.SelectedCalendarDay = value;
    }

    public string StatisticsCalendarDetailText => StatisticsDashboard.CalendarDetail.Text;

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

    public NovelStatisticsRangeMode[] StatisticsRangeModes => StatisticsDashboard.RangeModes;
    public NovelStatisticsTrendGrain[] StatisticsTrendGrains => StatisticsDashboard.TrendGrains;
    public NovelStatisticsTrendMetric[] StatisticsTrendMetrics => StatisticsDashboard.TrendMetrics;
    public NovelStatisticsBookRankingMetric[] StatisticsRankingMetrics => StatisticsDashboard.RankingMetrics;
    public StatisticsDailyTargetType[] StatisticsDailyTargetTypes => StatisticsDashboard.DailyTargetTypes;

    public bool NoNovels => !IsContentLoading && NovelBooks.Count == 0;
    public bool HasNovelStorageWarnings => NovelStorageWarnings.Count > 0;
    public bool HasStatisticsCorruptBooks => StatisticsDashboard.HasCorruptBooks;
    public string StatisticsCorruptWarningText => StatisticsDashboard.CorruptWarningText;

    public NovelLibraryPageViewModel(
        INovelLibraryService novelLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IMessenger messenger,
        ISasayakiMatchService sasayakiMatchService,
        ISettingsService settingsService,
        NovelStatisticsDashboardViewModel statisticsDashboard,
        INovelShelfService shelfService,
        ITtuSyncRemoteStore ttuSyncRemoteStore,
        ITtuBookImportService ttuBookImportService,
        IGoogleDriveAuthService googleDriveAuthService
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
        _ttuSyncRemoteStore = ttuSyncRemoteStore;
        _ttuBookImportService = ttuBookImportService;
        _googleDriveAuthService = googleDriveAuthService;
        SelectedSortOption = _settingsService.Current.NovelLibrarySortOption;
        StatisticsDashboard.PropertyChanged += OnStatisticsDashboardPropertyChanged;
        _messenger.RegisterAll(this);
    }

    public async Task InitializeAsync()
    {
        await LoadNovelsAsync();
    }

    public void Receive(NovelLibraryChangedMessage message) => _ = LoadNovelsAsync();

    public void OnNavigatedFrom()
    {
        StatisticsDashboard.Deactivate();
        _cts.Cancel();
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
            var result = await _novelLibraryService.ImportEpubAsync(filePath, _cts.Token);
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

        try
        {
            IsRemoteBooksLoading = true;
            var remoteBooks = await _ttuSyncRemoteStore.ListRemoteBooksAsync(_cts.Token);
            var localTitles = NovelBooks
                .Select(item => TtuSyncFileNames.SanitizeTtuFilename(item.Book.Title))
                .ToHashSet(StringComparer.Ordinal);
            RemoteBooks = new ObservableCollection<RemoteNovelBookItemViewModel>(
                remoteBooks
                    .Where(book => !localTitles.Contains(book.SanitizedTitle))
                    .Select(book => new RemoteNovelBookItemViewModel(book)));
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
            IsRemoteBooksLoading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadRemoteBookAsync(RemoteNovelBookItemViewModel item)
    {
        if (item == null || item.IsDownloading)
            return;

        item.IsDownloading = true;
        item.DownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(value => item.DownloadProgress = Math.Clamp(value, 0, 1));
            var settings = _settingsService.Current;
            var result = await _ttuBookImportService.ImportRemoteBookAsync(
                item.Book,
                new TtuBookImportOptions(
                    SyncStatistics: settings.StatisticsSettings.EnableSync,
                    SyncAudioBook: settings.SasayakiSettings.EnableSync,
                    StatisticsSyncMode: settings.StatisticsSettings.SyncMode),
                progress,
                _cts.Token);

            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                    _notificationService.ShowError(result.Error!, result.ErrorTitle!);
                return;
            }

            RemoteBooks.Remove(item);
            _notificationService.ShowSuccess("EPUB imported from Google Drive.", "Novel imported");
            await LoadNovelsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                $"Failed to import book from Google Drive: {ex.Message}",
                "Import failed");
        }
        finally
        {
            item.IsDownloading = false;
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
        await StatisticsDashboard.ActivateAsync(
            NovelBooks.Select(item => item.Book).ToList(),
            _currentShelfState,
            _cts.Token);
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
            _cts.Token);
        ApplyShelfResult(result);
    }

    [RelayCommand]
    private async Task MoveBookAsync(NovelBookShelfMoveRequest request)
    {
        var result = await _shelfService.MoveBooksAsync(
            [request.BookId],
            request.TargetShelfName,
            _cts.Token);
        ApplyShelfResult(result);
    }

    [RelayCommand]
    private async Task ReorderShelfBookAsync(NovelShelfBookReorderRequest request)
    {
        var result = await _shelfService.ReorderBookAsync(
            request.SourceBookId,
            request.TargetBookId,
            request.ShelfName,
            _cts.Token);
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
                _cts.Token);
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
        var result = await _novelLibraryService.GetNovelBooksAsync(ct: _cts.Token);

        if (result.IsSuccess)
        {
            var books = result.Value!.Books;
            NovelStorageWarnings = new ObservableCollection<string>(
                result.Value.CorruptMetadataPaths);
            NovelBooks = new ObservableCollection<NovelBookItemViewModel>(
                SortBooks(books).Select(book => new NovelBookItemViewModel(book)));
            var shelfResult = await _shelfService.LoadAsync(_cts.Token);
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
                    _cts.Token);
            }
        }
        else if (!result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);

        IsContentLoading = false;
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
        var rails = new List<NovelShelfSectionViewModel>();
        if (_settingsService.Current.BookshelfShowReading)
        {
            var reading = books
                .Where(IsReading)
                .Select(book => new NovelBookItemViewModel(book));
            rails.Add(new NovelShelfSectionViewModel(
                "reading",
                ResourceStringHelper.GetString(
                    "NovelShelfReadingLabel/Text",
                    "Reading"),
                IsDerived: true,
                IsUnshelved: false,
                new ObservableCollection<NovelBookItemViewModel>(reading)));
        }

        foreach (var shelf in state.Shelves)
        {
            var items = shelf.BookIds
                .Where(booksById.ContainsKey)
                .Select(id => new NovelBookItemViewModel(booksById[id]));
            rails.Add(new NovelShelfSectionViewModel(
                "shelf:" + shelf.Name,
                shelf.Name,
                IsDerived: false,
                IsUnshelved: false,
                new ObservableCollection<NovelBookItemViewModel>(items)));
        }

        RailSections = new ObservableCollection<NovelShelfSectionViewModel>(rails);
        UnshelvedBooks = new ObservableCollection<NovelBookItemViewModel>(
            state.UnshelvedBookOrder
                .Where(booksById.ContainsKey)
                .Select(id => new NovelBookItemViewModel(booksById[id])));
    }

    private static bool IsReading(NovelBook book) =>
        book.CurrentCharacterCount > 0
        && (book.TotalCharacterCount <= 0
            || book.CurrentCharacterCount < book.TotalCharacterCount);

    partial void OnSelectedSortOptionChanged(NovelLibrarySortOption value)
    {
        _settingsService.Set(settings => settings.NovelLibrarySortOption, value);
        _ = _settingsService.SaveAsync();

        if (!_suppressSortApplication)
            ApplyCurrentSort();
    }

    private void ApplyCurrentSort()
    {
        if (NovelBooks.Count == 0)
            return;

        NovelBooks = new ObservableCollection<NovelBookItemViewModel>(
            SortBookItems(NovelBooks));
    }

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
        var result = await _novelLibraryService.SaveNovelBookOrderAsync(orderedBookIds, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
    }

    private void OnStatisticsDashboardPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NovelStatisticsDashboardViewModel.DailyCharacterTarget))
        {
            OnPropertyChanged(nameof(StatisticsDailyCharacterTarget));
            OnPropertyChanged(nameof(StatisticsDailyCharacterTargetValue));
            return;
        }
        if (e.PropertyName == nameof(NovelStatisticsDashboardViewModel.DailyDurationTargetMinutes))
        {
            OnPropertyChanged(nameof(StatisticsDailyDurationTargetMinutes));
            OnPropertyChanged(nameof(StatisticsDailyDurationTargetMinutesValue));
            return;
        }
        if (e.PropertyName == nameof(NovelStatisticsDashboardViewModel.WeeklyTargetDays))
        {
            OnPropertyChanged(nameof(StatisticsWeeklyTargetDays));
            OnPropertyChanged(nameof(StatisticsWeeklyTargetDaysValue));
            return;
        }

        var propertyName = e.PropertyName switch
        {
            nameof(NovelStatisticsDashboardViewModel.TodayText) => nameof(StatisticsTodayText),
            nameof(NovelStatisticsDashboardViewModel.WeekText) => nameof(StatisticsWeekText),
            nameof(NovelStatisticsDashboardViewModel.RangeText) => nameof(StatisticsRangeText),
            nameof(NovelStatisticsDashboardViewModel.SpeedText) => nameof(StatisticsSpeedText),
            nameof(NovelStatisticsDashboardViewModel.RangeTitle) => nameof(StatisticsRangeTitle),
            nameof(NovelStatisticsDashboardViewModel.SelectedRangeMode) => nameof(SelectedStatisticsRangeMode),
            nameof(NovelStatisticsDashboardViewModel.AnchorDate) => nameof(StatisticsAnchorDate),
            nameof(NovelStatisticsDashboardViewModel.SelectedTrendGrain) => nameof(SelectedStatisticsTrendGrain),
            nameof(NovelStatisticsDashboardViewModel.SelectedTrendMetric) => nameof(SelectedStatisticsTrendMetric),
            nameof(NovelStatisticsDashboardViewModel.SelectedRankingMetric) => nameof(SelectedStatisticsRankingMetric),
            nameof(NovelStatisticsDashboardViewModel.SelectedDailyTargetType) => nameof(SelectedStatisticsDailyTargetType),
            nameof(NovelStatisticsDashboardViewModel.TrendPoints) => nameof(StatisticsTrendPoints),
            nameof(NovelStatisticsDashboardViewModel.CalendarDays) => nameof(StatisticsCalendarDays),
            nameof(NovelStatisticsDashboardViewModel.BookRankingRows) => nameof(StatisticsBookRankingRows),
            nameof(NovelStatisticsDashboardViewModel.ShelfComparisonRows) => nameof(StatisticsShelfComparisonRows),
            nameof(NovelStatisticsDashboardViewModel.SkippedCorruptBookIds) => nameof(StatisticsSkippedCorruptBookIds),
            nameof(NovelStatisticsDashboardViewModel.HasCorruptBooks) => nameof(HasStatisticsCorruptBooks),
            nameof(NovelStatisticsDashboardViewModel.CorruptWarningText) => nameof(StatisticsCorruptWarningText),
            nameof(NovelStatisticsDashboardViewModel.SelectedCalendarDay) => nameof(SelectedStatisticsCalendarDay),
            nameof(NovelStatisticsDashboardViewModel.CalendarDetail) => nameof(StatisticsCalendarDetailText),
            _ => null,
        };
        if (propertyName != null)
            OnPropertyChanged(propertyName);
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
