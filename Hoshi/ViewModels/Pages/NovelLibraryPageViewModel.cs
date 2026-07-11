using System;
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
    private readonly INovelStatisticsDashboardService _statisticsDashboardService;
    private readonly INovelShelfService _shelfService;
    private NovelShelfState _currentShelfState = new([], []);
    private NovelStatisticsDashboardSnapshot? _statisticsSnapshot;
    private bool _isInitializingStatisticsControls = true;
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
    public partial bool ShowStatisticsDashboard { get; set; }

    [ObservableProperty]
    public partial string StatisticsTodayText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string StatisticsWeekText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string StatisticsRangeText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string StatisticsSpeedText { get; set; } = "— / h";

    [ObservableProperty]
    public partial string StatisticsRangeTitle { get; set; } = "Recent year";

    [ObservableProperty]
    public partial NovelStatisticsRangeMode SelectedStatisticsRangeMode { get; set; } = NovelStatisticsRangeMode.Year;

    [ObservableProperty]
    public partial DateTimeOffset? StatisticsAnchorDate { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsTrendGrain SelectedStatisticsTrendGrain { get; set; } = NovelStatisticsTrendGrain.Day;

    [ObservableProperty]
    public partial NovelStatisticsTrendMetric SelectedStatisticsTrendMetric { get; set; } = NovelStatisticsTrendMetric.Characters;

    [ObservableProperty]
    public partial NovelStatisticsBookRankingMetric SelectedStatisticsRankingMetric { get; set; } = NovelStatisticsBookRankingMetric.Characters;

    [ObservableProperty]
    public partial StatisticsDailyTargetType SelectedStatisticsDailyTargetType { get; set; }

    [ObservableProperty]
    public partial int StatisticsDailyCharacterTarget { get; set; }

    public double StatisticsDailyCharacterTargetValue
    {
        get => StatisticsDailyCharacterTarget;
        set => StatisticsDailyCharacterTarget =
            NovelStatisticsDashboardTargets.SnapCharacterTarget((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial int StatisticsDailyDurationTargetMinutes { get; set; }

    public double StatisticsDailyDurationTargetMinutesValue
    {
        get => StatisticsDailyDurationTargetMinutes;
        set => StatisticsDailyDurationTargetMinutes =
            NovelStatisticsDashboardTargets.SnapDurationTarget((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial int StatisticsWeeklyTargetDays { get; set; }

    public double StatisticsWeeklyTargetDaysValue
    {
        get => StatisticsWeeklyTargetDays;
        set => StatisticsWeeklyTargetDays =
            NovelStatisticsDashboardTargets.SnapWeeklyTargetDays((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsTrendDisplayPoint> StatisticsTrendPoints { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsCalendarDay> StatisticsCalendarDays { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsBookRankingDisplayRow> StatisticsBookRankingRows { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsShelfComparisonRow> StatisticsShelfComparisonRows { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatisticsCorruptBooks))]
    [NotifyPropertyChangedFor(nameof(StatisticsCorruptWarningText))]
    public partial ObservableCollection<string> StatisticsSkippedCorruptBookIds { get; set; } = new();

    [ObservableProperty]
    public partial NovelStatisticsCalendarDay? SelectedStatisticsCalendarDay { get; set; }

    [ObservableProperty]
    public partial string StatisticsCalendarDetailText { get; set; } = "No reading records";

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

    public NovelStatisticsRangeMode[] StatisticsRangeModes { get; } = Enum.GetValues<NovelStatisticsRangeMode>();
    public NovelStatisticsTrendGrain[] StatisticsTrendGrains { get; } = Enum.GetValues<NovelStatisticsTrendGrain>();
    public NovelStatisticsTrendMetric[] StatisticsTrendMetrics { get; } = Enum.GetValues<NovelStatisticsTrendMetric>();
    public NovelStatisticsBookRankingMetric[] StatisticsRankingMetrics { get; } = Enum.GetValues<NovelStatisticsBookRankingMetric>();
    public StatisticsDailyTargetType[] StatisticsDailyTargetTypes { get; } = Enum.GetValues<StatisticsDailyTargetType>();

    public bool NoNovels => !IsContentLoading && NovelBooks.Count == 0;
    public bool HasNovelStorageWarnings => NovelStorageWarnings.Count > 0;
    public bool HasStatisticsCorruptBooks => StatisticsSkippedCorruptBookIds.Count > 0;
    public string StatisticsCorruptWarningText => HasStatisticsCorruptBooks
        ? "Some statistics are temporarily unavailable. The affected sidecar files were left unchanged."
        : string.Empty;

    public NovelLibraryPageViewModel(
        INovelLibraryService novelLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IMessenger messenger,
        ISasayakiMatchService sasayakiMatchService,
        ISettingsService settingsService,
        INovelStatisticsDashboardService statisticsDashboardService,
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
        _statisticsDashboardService = statisticsDashboardService;
        _shelfService = shelfService;
        _ttuSyncRemoteStore = ttuSyncRemoteStore;
        _ttuBookImportService = ttuBookImportService;
        _googleDriveAuthService = googleDriveAuthService;
        SelectedSortOption = _settingsService.Current.NovelLibrarySortOption;
        var statisticsSettings = _settingsService.Current.StatisticsSettings;
        SelectedStatisticsDailyTargetType = statisticsSettings.DailyTargetType;
        StatisticsDailyCharacterTarget = NovelStatisticsDashboardTargets.SnapCharacterTarget(
            statisticsSettings.DailyCharacterTarget);
        StatisticsDailyDurationTargetMinutes = NovelStatisticsDashboardTargets.SnapDurationTarget(
            statisticsSettings.DailyDurationTargetMinutes);
        StatisticsWeeklyTargetDays = NovelStatisticsDashboardTargets.SnapWeeklyTargetDays(
            statisticsSettings.WeeklyTargetDays);
        _isInitializingStatisticsControls = false;
        _messenger.RegisterAll(this);
    }

    public async Task InitializeAsync()
    {
        _statisticsDashboardService.SnapshotRefreshed -= OnStatisticsSnapshotRefreshed;
        _statisticsDashboardService.SnapshotRefreshed += OnStatisticsSnapshotRefreshed;
        await LoadNovelsAsync();
    }

    public void Receive(NovelLibraryChangedMessage message) => _ = LoadNovelsAsync();

    public void OnNavigatedFrom()
    {
        _statisticsDashboardService.SnapshotRefreshed -= OnStatisticsSnapshotRefreshed;
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
            await LoadStatisticsDashboardAsync(books);
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

    private async Task LoadStatisticsDashboardAsync(IReadOnlyList<NovelBook> books)
    {
        var snapshot = await _statisticsDashboardService.LoadSnapshotAsync(books, _cts.Token);
        ApplyStatisticsDashboardSnapshot(snapshot);
    }

    private void OnStatisticsSnapshotRefreshed(
        object? sender,
        NovelStatisticsDashboardSnapshot snapshot) =>
        ApplyStatisticsDashboardSnapshot(snapshot);

    private void ApplyStatisticsDashboardSnapshot(
        NovelStatisticsDashboardSnapshot snapshot)
    {
        _statisticsSnapshot = snapshot;
        if (StatisticsAnchorDate == null)
        {
            var initialAnchor = snapshot.Days.LastOrDefault()?.Date
                ?? DateOnly.FromDateTime(DateTime.Now);
            StatisticsAnchorDate = LocalDateTimeOffset(initialAnchor);
        }
        RecalculateStatisticsDashboard();
    }

    private void RecalculateStatisticsDashboard()
    {
        if (_statisticsSnapshot == null)
            return;

        var snapshot = _statisticsSnapshot;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var targetSettings = new NovelStatisticsDashboardTargetSettings(
            SelectedStatisticsDailyTargetType,
            StatisticsDailyCharacterTarget,
            StatisticsDailyDurationTargetMinutes,
            StatisticsWeeklyTargetDays);

        var todaySummary = NovelStatisticsDashboardCalculator.TodaySummary(
            snapshot,
            today,
            targetSettings);
        var weekSummary = NovelStatisticsDashboardCalculator.WeekSummary(
            snapshot,
            today,
            targetSettings);
        var window = snapshot.WindowStart == DateOnly.MinValue
            ? NovelStatisticsDashboardCalculator.RecentYear(today)
            : new NovelStatisticsDateRange(snapshot.WindowStart, snapshot.WindowEnd);
        var anchor = StatisticsAnchorDate is { } selectedAnchor
            ? DateOnly.FromDateTime(selectedAnchor.LocalDateTime)
            : snapshot.Days.LastOrDefault()?.Date ?? today;
        var range = NovelStatisticsDashboardCalculator.SelectedRange(
            SelectedStatisticsRangeMode,
            anchor,
            window);
        var rangeSummary = NovelStatisticsDashboardCalculator.RangeSummary(
            snapshot.Days,
            range,
            targetSettings);
        var speed = NovelStatisticsDashboardCalculator.SpeedSummary(snapshot.Days, range);
        var trend = NovelStatisticsDashboardCalculator.TrendPoints(
            SelectedStatisticsTrendGrain,
            range,
            snapshot.Days);
        var calendarSnapshot = snapshot.WindowStart == DateOnly.MinValue
            ? snapshot with { WindowStart = window.Start, WindowEnd = window.End }
            : snapshot;
        var calendar = NovelStatisticsDashboardCalculator.CalendarDays(
            calendarSnapshot,
            today,
            targetSettings);
        var ranking = NovelStatisticsDashboardCalculator.BookRankingRows(
            snapshot.Days,
            range,
            SelectedStatisticsRankingMetric);
        var shelves = NovelStatisticsDashboardCalculator.ShelfComparisonRows(
            snapshot,
            _currentShelfState,
            range,
            ResourceStringHelper.GetString("NovelShelfUnshelvedLabel/Text", "Unshelved"));

        StatisticsTodayText = $"{FormatCharacters(todaySummary.Characters)} chars · {FormatDuration(todaySummary.ReadingTime)} · {todaySummary.TargetPercent}%";
        StatisticsWeekText = $"{FormatCharacters(weekSummary.Characters)} chars · {weekSummary.MetTargetDays}/{weekSummary.TargetDays} days";
        StatisticsRangeText = $"{FormatCharacters(rangeSummary.Characters)} chars · {FormatDuration(rangeSummary.ReadingTime)}";
        StatisticsRangeTitle = FormatRangeTitle(SelectedStatisticsRangeMode, range);
        StatisticsSpeedText = speed.WeightedAveragePerHour is { } weighted
            ? $"{FormatCharacters(weighted)} / h"
            : "— / h";
        StatisticsTrendPoints = new ObservableCollection<NovelStatisticsTrendDisplayPoint>(
            trend.Select(point => new NovelStatisticsTrendDisplayPoint(
                point.Id,
                point.Label,
                FormatTrendValue(point, SelectedStatisticsTrendMetric))));
        StatisticsCalendarDays = new ObservableCollection<NovelStatisticsCalendarDay>(calendar);
        StatisticsBookRankingRows = new ObservableCollection<NovelStatisticsBookRankingDisplayRow>(
            ranking.Select(row => new NovelStatisticsBookRankingDisplayRow(
                row.Id,
                row.Title,
                FormatRankingValue(row, SelectedStatisticsRankingMetric))));
        StatisticsShelfComparisonRows = new ObservableCollection<NovelStatisticsShelfComparisonRow>(shelves);
        StatisticsSkippedCorruptBookIds = new ObservableCollection<string>(snapshot.SkippedCorruptBookIds);

        var selectedDate = SelectedStatisticsCalendarDay?.Date ?? anchor;
        SelectedStatisticsCalendarDay = StatisticsCalendarDays.FirstOrDefault(day => day.Date == selectedDate)
            ?? StatisticsCalendarDays.LastOrDefault();
        UpdateStatisticsCalendarDetail();
    }

    partial void OnSelectedStatisticsRangeModeChanged(NovelStatisticsRangeMode value) => RecalculateStatisticsDashboard();
    partial void OnStatisticsAnchorDateChanged(DateTimeOffset? value) => RecalculateStatisticsDashboard();
    partial void OnSelectedStatisticsTrendGrainChanged(NovelStatisticsTrendGrain value) => RecalculateStatisticsDashboard();
    partial void OnSelectedStatisticsTrendMetricChanged(NovelStatisticsTrendMetric value) => RecalculateStatisticsDashboard();
    partial void OnSelectedStatisticsRankingMetricChanged(NovelStatisticsBookRankingMetric value) => RecalculateStatisticsDashboard();
    partial void OnSelectedStatisticsCalendarDayChanged(NovelStatisticsCalendarDay? value)
    {
        if (value != null)
        {
            StatisticsAnchorDate = LocalDateTimeOffset(value.Date);
            UpdateStatisticsCalendarDetail();
        }
    }

    partial void OnSelectedStatisticsDailyTargetTypeChanged(StatisticsDailyTargetType value) => SaveStatisticsTargetsAndRecalculate();
    partial void OnStatisticsDailyCharacterTargetChanged(int value)
    {
        OnPropertyChanged(nameof(StatisticsDailyCharacterTargetValue));
        SaveStatisticsTargetsAndRecalculate();
    }

    partial void OnStatisticsDailyDurationTargetMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(StatisticsDailyDurationTargetMinutesValue));
        SaveStatisticsTargetsAndRecalculate();
    }

    partial void OnStatisticsWeeklyTargetDaysChanged(int value)
    {
        OnPropertyChanged(nameof(StatisticsWeeklyTargetDaysValue));
        SaveStatisticsTargetsAndRecalculate();
    }

    private void SaveStatisticsTargetsAndRecalculate()
    {
        if (_isInitializingStatisticsControls)
            return;

        var current = _settingsService.Current.StatisticsSettings;
        _settingsService.Set(
            settings => settings.StatisticsSettings,
            new NovelStatisticsSettings
            {
                EnableStatistics = current.EnableStatistics,
                AutostartMode = current.AutostartMode,
                DailyTargetType = SelectedStatisticsDailyTargetType,
                DailyCharacterTarget = StatisticsDailyCharacterTarget,
                DailyDurationTargetMinutes = StatisticsDailyDurationTargetMinutes,
                WeeklyTargetDays = StatisticsWeeklyTargetDays,
                EnableSync = current.EnableSync,
                SyncMode = current.SyncMode,
            });
        _ = _settingsService.SaveAsync();
        RecalculateStatisticsDashboard();
    }

    private void UpdateStatisticsCalendarDetail()
    {
        var day = SelectedStatisticsCalendarDay;
        StatisticsCalendarDetailText = day == null
            ? "No reading records"
            : $"{day.Date:yyyy-MM-dd} · {FormatCharacters(day.Characters)} chars · {FormatDuration(day.ReadingTime)} · {day.ActiveBookCount} {(day.ActiveBookCount == 1 ? "book" : "books")}";
    }

    private static DateTimeOffset LocalDateTimeOffset(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }

    private static string FormatRangeTitle(
        NovelStatisticsRangeMode mode,
        NovelStatisticsDateRange range) => mode switch
        {
            NovelStatisticsRangeMode.Year => "Recent year",
            NovelStatisticsRangeMode.Month => range.Start.ToString("yyyy-MM"),
            NovelStatisticsRangeMode.Week => $"{range.Start:MM-dd} – {range.End:MM-dd}",
            _ => range.Start.ToString("yyyy-MM-dd"),
        };

    private static string FormatTrendValue(
        NovelStatisticsTrendPoint point,
        NovelStatisticsTrendMetric metric) => metric switch
        {
            NovelStatisticsTrendMetric.Duration => FormatDuration(point.ReadingTime),
            NovelStatisticsTrendMetric.Speed => point.AverageSpeedPerHour is { } speed
                ? $"{FormatCharacters(speed)} / h"
                : "— / h",
            _ => $"{FormatCharacters(point.Characters)} chars",
        };

    private static string FormatRankingValue(
        NovelStatisticsBookRankingRow row,
        NovelStatisticsBookRankingMetric metric) => metric switch
        {
            NovelStatisticsBookRankingMetric.Duration => FormatDuration(row.ReadingTime),
            NovelStatisticsBookRankingMetric.Speed => row.AverageSpeedPerHour is { } speed
                ? $"{FormatCharacters(speed)} / h"
                : "— / h",
            _ => $"{FormatCharacters(row.Characters)} chars",
        };

    private static string FormatCharacters(int characters) =>
        characters.ToString("N0");

    private static string FormatDuration(double seconds)
    {
        var minutes = Math.Max((int)Math.Round(seconds / 60), 0);
        if (minutes < 60)
            return $"{minutes}m";

        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
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
