using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Models.Settings;
using Niratan.Services.Novels;
using Niratan.Services.Settings;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public partial class NovelStatisticsDashboardViewModel : ObservableObject
{
    private const int BookRankingPageSize = 12;

    private readonly INovelStatisticsDashboardService _dashboardService;
    private readonly ISettingsService _settingsService;
    private readonly INovelStatisticsSidecarService? _statisticsSidecarService;
    private readonly INovelStatisticsMutationCoordinator? _statisticsMutationCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly NovelStatisticsBookCoverCache _bookCoverCache;
    private NovelStatisticsDashboardSnapshot? _snapshot;
    private IReadOnlyList<NovelStatisticsDateRange> _selectableRanges = [];
    private NovelShelfState _shelfState = new([], []);
    private IReadOnlyDictionary<string, NovelBook> _booksById =
        new Dictionary<string, NovelBook>(StringComparer.Ordinal);
    private bool _isInitializing = true;
    private bool _isUpdatingProjection;
    private bool _isUpdatingRangeState;
    private CancellationTokenSource? _activationCts;
    private SynchronizationContext? _uiContext;
    private int _activationGeneration;

    public NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService)
        : this(dashboardService, settingsService, null, TimeProvider.System)
    {
    }

    public NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService,
        INovelStatisticsSidecarService statisticsSidecarService,
        INovelStatisticsMutationCoordinator? statisticsMutationCoordinator = null)
        : this(
            dashboardService,
            settingsService,
            statisticsSidecarService,
            TimeProvider.System,
            new NovelStatisticsBookCoverCache(),
            statisticsMutationCoordinator)
    {
    }

    internal NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService,
        TimeProvider timeProvider)
        : this(dashboardService, settingsService, null, timeProvider)
    {
    }

    internal NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService,
        INovelStatisticsSidecarService? statisticsSidecarService,
        TimeProvider timeProvider)
        : this(
            dashboardService,
            settingsService,
            statisticsSidecarService,
            timeProvider,
            new NovelStatisticsBookCoverCache())
    {
    }

    internal NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService,
        INovelStatisticsSidecarService? statisticsSidecarService,
        TimeProvider timeProvider,
        NovelStatisticsBookCoverCache bookCoverCache,
        INovelStatisticsMutationCoordinator? statisticsMutationCoordinator = null)
    {
        _dashboardService = dashboardService;
        _settingsService = settingsService;
        _statisticsSidecarService = statisticsSidecarService;
        _statisticsMutationCoordinator = statisticsMutationCoordinator;
        _timeProvider = timeProvider;
        _bookCoverCache = bookCoverCache;

        var settings = _settingsService.Current.StatisticsSettings;
        SelectedDailyTargetType = settings.DailyTargetType;
        DailyCharacterTarget = NovelStatisticsDashboardTargets.SnapCharacterTarget(
            settings.DailyCharacterTarget);
        DailyDurationTargetMinutes = NovelStatisticsDashboardTargets.SnapDurationTarget(
            settings.DailyDurationTargetMinutes);
        WeeklyTargetDays = NovelStatisticsDashboardTargets.SnapWeeklyTargetDays(
            settings.WeeklyTargetDays);
        _isInitializing = false;
    }

    public NovelStatisticsRangeMode[] RangeModes { get; } =
        Enum.GetValues<NovelStatisticsRangeMode>();
    public NovelStatisticsTrendGrain[] TrendGrains { get; } =
        Enum.GetValues<NovelStatisticsTrendGrain>();
    public NovelStatisticsTrendMetric[] TrendMetrics { get; } =
        Enum.GetValues<NovelStatisticsTrendMetric>();
    public NovelStatisticsTrendChartStyle[] TrendStyles { get; } =
        Enum.GetValues<NovelStatisticsTrendChartStyle>();
    public NovelStatisticsBookRankingMetric[] RankingMetrics { get; } =
        Enum.GetValues<NovelStatisticsBookRankingMetric>();
    public StatisticsDailyTargetType[] DailyTargetTypes { get; } =
        Enum.GetValues<StatisticsDailyTargetType>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoData))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoData))]
    public partial bool HasData { get; set; }

    public bool HasNoData => !IsLoading && !HasData;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeScrollLargeChange))]
    public partial NovelStatisticsRangeMode SelectedRangeMode { get; set; } =
        NovelStatisticsRangeMode.Year;

    [ObservableProperty]
    public partial int SelectedRangeOffset { get; set; }

    public double SelectedRangeOffsetValue
    {
        get => SelectedRangeOffset;
        set
        {
            var rounded = Math.Clamp(
                (int)Math.Round(value),
                0,
                Math.Max(_selectableRanges.Count - 1, 0));
            if (rounded == SelectedRangeOffset)
            {
                OnPropertyChanged(nameof(SelectedRangeOffsetValue));
                return;
            }
            SelectedRangeOffset = rounded;
        }
    }

    public double RangeScrollMaximum => Math.Max(_selectableRanges.Count - 1, 0);

    public double RangeScrollLargeChange => SelectedRangeMode switch
    {
        NovelStatisticsRangeMode.Day => 7,
        NovelStatisticsRangeMode.Week => 4,
        NovelStatisticsRangeMode.Month => 3,
        _ => 1,
    };

    public bool CanScrollRange => _selectableRanges.Count > 1;

    public string RangeScrollAccessibleText => RangeTitle;

    [ObservableProperty]
    public partial NovelStatisticsTrendGrain SelectedTrendGrain { get; set; } =
        NovelStatisticsTrendGrain.Day;

    [ObservableProperty]
    public partial NovelStatisticsTrendMetric SelectedTrendMetric { get; set; } =
        NovelStatisticsTrendMetric.Characters;

    [ObservableProperty]
    public partial NovelStatisticsTrendChartStyle SelectedTrendStyle { get; set; } =
        NovelStatisticsTrendChartStyle.Bar;

    [ObservableProperty]
    public partial NovelStatisticsBookRankingMetric SelectedRankingMetric { get; set; } =
        NovelStatisticsBookRankingMetric.Characters;

    [ObservableProperty]
    public partial int VisibleBookRankingLimit { get; private set; } = BookRankingPageSize;

    [ObservableProperty]
    public partial bool CanShowMoreBookRankings { get; private set; }

    [ObservableProperty]
    public partial StatisticsDailyTargetType SelectedDailyTargetType { get; set; }

    [ObservableProperty]
    public partial int DailyCharacterTarget { get; set; }

    public double DailyCharacterTargetValue
    {
        get => DailyCharacterTarget;
        set => DailyCharacterTarget = NovelStatisticsDashboardTargets
            .SnapCharacterTarget((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial int DailyDurationTargetMinutes { get; set; }

    public double DailyDurationTargetMinutesValue
    {
        get => DailyDurationTargetMinutes;
        set => DailyDurationTargetMinutes = NovelStatisticsDashboardTargets
            .SnapDurationTarget((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial int WeeklyTargetDays { get; set; }

    public double WeeklyTargetDaysValue
    {
        get => WeeklyTargetDays;
        set => WeeklyTargetDays = NovelStatisticsDashboardTargets
            .SnapWeeklyTargetDays((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial NovelStatisticsTodaySummary? Today { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsWeekSummary? Week { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsRangeSummary? SelectedRange { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsSpeedSummary? Speed { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsDateRange SelectedDateRange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeScrollAccessibleText))]
    public partial string RangeTitle { get; set; } = "Recent year";

    [ObservableProperty]
    public partial string TodayText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string WeekText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string RangeText { get; set; } = "0 chars";

    [ObservableProperty]
    public partial string SpeedText { get; set; } = "— / h";

    [ObservableProperty]
    public partial string TodayGoalPercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsMetricDisplay> TodayMetrics { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsMetricDisplay> WeekMetrics { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsMetricDisplay> RangeMetrics { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsWeekDayDisplay> WeekDays { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsMetricDisplay> SpeedMetrics { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsTrendDisplayPoint> TrendPoints { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsAxisTickDisplay> TrendAxisTicks { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsCalendarDayDisplay> CalendarDays { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsBookRankingItemViewModel> BookRankingRows { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsShelfComparisonDisplayRow> ShelfComparisonRows { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCorruptBooks))]
    [NotifyPropertyChangedFor(nameof(CorruptWarningText))]
    public partial ObservableCollection<string> SkippedCorruptBookIds { get; set; } = [];

    public bool HasCorruptBooks => SkippedCorruptBookIds.Count > 0;
    public string CorruptWarningText => HasCorruptBooks
        ? Localized(
            "NovelStatisticsCorruptWarning",
            "Some statistics are temporarily unavailable. The affected sidecar files were left unchanged.")
        : string.Empty;

    [ObservableProperty]
    public partial NovelStatisticsCalendarDayDisplay? SelectedCalendarDay { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsCalendarDetailDisplay CalendarDetail { get; set; } =
        new(default, 0, 0, 0, Localized("NovelStatisticsNoReadingRecords", "No reading records"));

    public async Task ActivateAsync(
        IReadOnlyList<NovelBook> books,
        NovelShelfState shelfState,
        CancellationToken ct)
    {
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        var activationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activationCts = activationCts;
        var generation = ++_activationGeneration;
        _uiContext = SynchronizationContext.Current;
        _shelfState = shelfState;
        _bookCoverCache.Clear();
        _booksById = books
            .GroupBy(book => book.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        ResetBookRankingPagination();
        _dashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
        _dashboardService.SnapshotRefreshed += OnSnapshotRefreshed;
        IsActive = true;
        IsLoading = true;
        try
        {
            var snapshot = await _dashboardService.LoadSnapshotAsync(
                books,
                activationCts.Token);
            if (generation == _activationGeneration
                && IsActive
                && !activationCts.IsCancellationRequested)
            {
                ApplySnapshot(snapshot);
            }
        }
        catch (OperationCanceledException) when (activationCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == _activationGeneration)
                IsLoading = false;
            if (ReferenceEquals(_activationCts, activationCts))
            {
                _activationCts = null;
                activationCts.Dispose();
            }
        }
    }

    public void Deactivate()
    {
        _activationGeneration++;
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _activationCts = null;
        IsActive = false;
        IsLoading = false;
        IsRefreshing = false;
        _dashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
    }

    private void OnSnapshotRefreshed(
        object? sender,
        NovelStatisticsDashboardSnapshot snapshot)
    {
        var generation = _activationGeneration;
        if (!IsActive)
            return;

        if (_uiContext != null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(
                _ => ApplyRefreshedSnapshot(snapshot, generation),
                null);
            return;
        }

        ApplyRefreshedSnapshot(snapshot, generation);
    }

    private void ApplyRefreshedSnapshot(
        NovelStatisticsDashboardSnapshot snapshot,
        int generation)
    {
        if (!IsActive || generation != _activationGeneration)
            return;

        IsRefreshing = true;
        try
        {
            ApplySnapshot(snapshot);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ApplySnapshot(NovelStatisticsDashboardSnapshot snapshot)
    {
        var previousRange = CurrentSelectableRange();
        _snapshot = snapshot;
        HasData = snapshot.Days.Count > 0;
        RebuildSelectableRanges(
            previousRange?.Start,
            selectNewest: previousRange == null);
        Recalculate();
    }

    private void Recalculate()
    {
        if (_snapshot == null)
            return;

        var snapshot = _snapshot;
        var today = TodayDate();
        var targetSettings = new NovelStatisticsDashboardTargetSettings(
            SelectedDailyTargetType,
            DailyCharacterTarget,
            DailyDurationTargetMinutes,
            WeeklyTargetDays);
        var window = snapshot.WindowStart == DateOnly.MinValue
            ? NovelStatisticsDashboardCalculator.RecentYear(today)
            : new NovelStatisticsDateRange(snapshot.WindowStart, snapshot.WindowEnd);
        var range = CurrentSelectableRange() ?? window;

        Today = NovelStatisticsDashboardCalculator.TodaySummary(snapshot, today, targetSettings);
        Week = NovelStatisticsDashboardCalculator.WeekSummary(snapshot, today, targetSettings);
        SelectedDateRange = range;
        SelectedRange = NovelStatisticsDashboardCalculator.RangeSummary(
            snapshot.Days,
            range,
            targetSettings);
        Speed = NovelStatisticsDashboardCalculator.SpeedSummary(snapshot.Days, range);
        RangeTitle = FormatRangeTitle(SelectedRangeMode, range);
        TodayText = $"{FormatCharacters(Today.Characters)} chars · {FormatDuration(Today.ReadingTime)} · {Today.TargetPercent}%";
        WeekText = $"{FormatCharacters(Week.Characters)} chars · {Week.MetTargetDays}/{Week.TargetDays} days";
        RangeText = $"{FormatCharacters(SelectedRange.Characters)} chars · {FormatDuration(SelectedRange.ReadingTime)}";
        SpeedText = FormatSpeed(Speed.WeightedAveragePerHour);
        TodayGoalPercentText = $"{Today.TargetPercent}%";

        TodayMetrics = new(
        [
            new(Localized("NovelStatisticsMetricDuration", "Duration"), FormatDuration(Today.ReadingTime)),
            new(Localized("NovelStatisticsMetricCharacters", "Characters"), FormatCharacters(Today.Characters)),
            new(Localized("NovelStatisticsMetricSpeed", "Speed"), FormatSpeed(Today.AverageSpeedPerHour)),
            new(Localized("NovelStatisticsMetricStreak", "Streak"), $"{Today.DailyStreakDays} days"),
        ]);
        WeekMetrics = new(
        [
            new(Localized("NovelStatisticsMetricDuration", "Duration"), FormatDuration(Week.ReadingTime)),
            new(Localized("NovelStatisticsMetricCharacters", "Characters"), FormatCharacters(Week.Characters)),
            new(Localized("NovelStatisticsMetricAverageCharacters", "Avg Characters"), FormatCharacters(Week.AverageCharactersPerElapsedDay)),
            new(Localized("NovelStatisticsMetricSpeed", "Speed"), FormatSpeed(Week.AverageSpeedPerHour)),
        ]);
        RangeMetrics = new(
        [
            new(Localized("NovelStatisticsMetricDuration", "Duration"), FormatDuration(SelectedRange.ReadingTime)),
            new(Localized("NovelStatisticsMetricCharacters", "Characters"), FormatCharacters(SelectedRange.Characters)),
            new(Localized("NovelStatisticsMetricSpeed", "Speed"), FormatSpeed(SelectedRange.AverageSpeedPerHour)),
            SelectedRangeMode == NovelStatisticsRangeMode.Day
                ? new(Localized("NovelStatisticsMetricGoalProgress", "Goal Progress"), $"{SelectedRange.TargetProgressPercent}%")
                : new(Localized("NovelStatisticsMetricDaysMet", "Days Met"), $"{SelectedRange.TargetDays} days"),
        ]);

        WeekDays = new(Week.Days.Select(day => new NovelStatisticsWeekDayDisplay(
            day.Date,
            day.Date.ToString("ddd", CultureInfo.CurrentCulture),
            day.Percent is { } percent ? $"{percent}%" : "—",
            day.IsToday,
            day.IsFuture,
            day.MetTarget)));
        SpeedMetrics = new(
        [
            new(Localized("NovelStatisticsMetricWeighted", "Weighted"), FormatSpeed(Speed.WeightedAveragePerHour)),
            new(Localized("NovelStatisticsMetricMedianActiveDay", "Median Active Day"), FormatSpeed(Speed.MedianActiveDayPerHour)),
            new(Localized("NovelStatisticsMetricLastSevenActiveDays", "Last 7 Active Days"), FormatSpeed(Speed.LastSevenActiveDaysPerHour)),
            new(Localized("NovelStatisticsMetricChange", "Change"), Speed.ChangePercent is { } change ? $"{change:+0;-0;0}%" : "—"),
            new(Localized("NovelStatisticsMetricFastest", "Fastest"), FormatSpeedDay(Speed.FastestDay)),
            new(Localized("NovelStatisticsMetricSlowest", "Slowest"), FormatSpeedDay(Speed.SlowestDay)),
        ]);

        var trend = NovelStatisticsDashboardCalculator.TrendPoints(
            SelectedTrendGrain,
            range,
            snapshot.Days);
        var trendValues = trend.Select(TrendRawValue).ToArray();
        var trendMaximum = Math.Max(trendValues.DefaultIfEmpty().Max(), 1);
        TrendPoints = new(trend.Select((point, index) =>
            new NovelStatisticsTrendDisplayPoint(
                point.Id,
                point.Label,
                FormatTrendValue(point, SelectedTrendMetric),
                Math.Clamp(trendValues[index] / trendMaximum, 0, 1),
                BuildTrendToolTip(point))));
        TrendAxisTicks = new(Enumerable.Range(0, 5).Select(index =>
        {
            var normalized = index / 4d;
            return new NovelStatisticsAxisTickDisplay(
                normalized,
                FormatTrendAxisValue(
                    trendMaximum * normalized,
                    SelectedTrendMetric));
        }));

        var calendarSnapshot = snapshot.WindowStart == DateOnly.MinValue
            ? snapshot with { WindowStart = window.Start, WindowEnd = window.End }
            : snapshot;
        var calendar = NovelStatisticsDashboardCalculator.CalendarDays(
            calendarSnapshot,
            today,
            targetSettings);
        var maxCharacters = Math.Max(calendar.Select(day => day.Characters).DefaultIfEmpty().Max(), 1);
        CalendarDays = new(calendar.Select(day => new NovelStatisticsCalendarDayDisplay(
            day.Date,
            day.Characters,
            day.ReadingTime,
            day.ActiveBookCount,
            day.TargetPercent,
            $"{day.Date:yyyy-MM-dd}, {FormatCharacters(day.Characters)} chars",
            day.Characters <= 0 ? 0.08 : 0.16 + 0.84 * day.Characters / maxCharacters,
            day.Date >= range.Start && day.Date <= range.End,
            day.IsToday)));

        var rankingCandidates = NovelStatisticsDashboardCalculator.BookRankingRows(
            snapshot.Days,
            range,
            SelectedRankingMetric,
            VisibleBookRankingLimit + 1);
        CanShowMoreBookRankings = rankingCandidates.Count > VisibleBookRankingLimit;
        var ranking = rankingCandidates
            .Take(VisibleBookRankingLimit)
            .ToArray();
        var rankingValues = ranking.Select(RankingRawValue).ToArray();
        var rankingMaximum = Math.Max(rankingValues.DefaultIfEmpty().Max(), 1);
        BookRankingRows = new(ranking.Select((row, index) =>
            new NovelStatisticsBookRankingItemViewModel(
                ResolveRankingBook(row, snapshot),
                row.Title,
                FormatRankingValue(row, SelectedRankingMetric),
                Math.Clamp(rankingValues[index] / rankingMaximum, 0, 1),
                _bookCoverCache)));

        var shelves = NovelStatisticsDashboardCalculator.ShelfComparisonRows(
            snapshot,
            _shelfState,
            range,
            ResourceStringHelper.GetString("NovelShelfUnshelvedLabel/Text", "Unshelved"));
        var shelfMaximum = Math.Max(shelves.Select(row => row.RecordedCharacters).DefaultIfEmpty().Max(), 1);
        ShelfComparisonRows = new(shelves.Select(row =>
            new NovelStatisticsShelfComparisonDisplayRow(
                row.Id,
                row.Name,
                $"{row.BookCount} books · {FormatCharacters(row.RecordedCharacters)} chars · {FormatDuration(row.ReadingTime)}",
                FormatSpeed(row.AverageSpeedPerHour),
                row.TotalBookCharacters <= 0
                    ? 0
                    : Math.Clamp(row.RecordedCharacters / (double)row.TotalBookCharacters, 0, 1),
                Math.Clamp(row.RecordedCharacters / (double)shelfMaximum, 0, 1))));

        SkippedCorruptBookIds = new(snapshot.SkippedCorruptBookIds);
        var selectedDate = SelectedCalendarDay?.Date ?? range.End;
        _isUpdatingProjection = true;
        try
        {
            SelectedCalendarDay = CalendarDays.FirstOrDefault(
                    day => day.Date == selectedDate)
                ?? CalendarDays.FirstOrDefault(day => day.Date == range.End)
                ?? CalendarDays.LastOrDefault();
        }
        finally
        {
            _isUpdatingProjection = false;
        }
        UpdateCalendarDetail();
    }

    partial void OnSelectedRangeModeChanged(NovelStatisticsRangeMode value)
    {
        ResetBookRankingPagination();
        _isUpdatingProjection = true;
        try
        {
            SelectedCalendarDay = null;
        }
        finally
        {
            _isUpdatingProjection = false;
        }
        RebuildSelectableRanges(preferredStart: null, selectNewest: true);
        Recalculate();
    }

    partial void OnSelectedRangeOffsetChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedRangeOffsetValue));
        OnPropertyChanged(nameof(RangeScrollAccessibleText));
        if (_isUpdatingRangeState)
            return;

        ResetBookRankingPagination();
        _isUpdatingProjection = true;
        try
        {
            SelectedCalendarDay = null;
        }
        finally
        {
            _isUpdatingProjection = false;
        }
        Recalculate();
    }

    partial void OnSelectedTrendGrainChanged(NovelStatisticsTrendGrain value) => Recalculate();
    partial void OnSelectedTrendMetricChanged(NovelStatisticsTrendMetric value) => Recalculate();
    partial void OnSelectedRankingMetricChanged(NovelStatisticsBookRankingMetric value)
    {
        ResetBookRankingPagination();
        Recalculate();
    }

    public void ShowMoreBookRankings()
    {
        if (!CanShowMoreBookRankings)
            return;

        VisibleBookRankingLimit += BookRankingPageSize;
        Recalculate();
    }

    private void ResetBookRankingPagination() =>
        VisibleBookRankingLimit = BookRankingPageSize;

    public async Task<NovelStatisticsBookDetailViewModel?> LoadBookStatisticsAsync(
        string bookId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bookId)
            || !_booksById.TryGetValue(bookId, out var book))
        {
            return null;
        }

        var rankingItem = BookRankingRows.FirstOrDefault(row => row.Id == bookId)
            ?? new NovelStatisticsBookRankingItemViewModel(
                book,
                book.Title,
                string.Empty,
                0,
                _bookCoverCache);
        IReadOnlyList<NovelReadingStatistic> statistics;
        string? errorMessage = null;
        if (_statisticsSidecarService != null
            && !string.IsNullOrWhiteSpace(book.ExtractedPath))
        {
            var result = await _statisticsSidecarService.LoadWithStatusAsync(
                book.ExtractedPath,
                ct);
            statistics = result.Statistics;
            if (result.Status is NovelStatisticsSidecarLoadStatus.Corrupt
                or NovelStatisticsSidecarLoadStatus.Unavailable)
            {
                errorMessage = ResourceStringHelper.GetString(
                    "NovelStatisticsBookDetailUnavailable",
                    "This book's statistics could not be read. The sidecar was left unchanged.");
            }
        }
        else
        {
            statistics = StatisticsFromSnapshot(bookId, book.Title);
        }

        var visible = NovelStatisticsEditor.Visible(statistics);
        var totalCharacters = visible.Sum(item => item.CharactersRead);
        var totalDuration = visible.Sum(item => item.ReadingTime);
        var speedSamples = visible
            .Where(item => item.CharactersRead > 0 && item.ReadingTime >= 60)
            .ToArray();
        var speedCharacters = speedSamples.Sum(item => item.CharactersRead);
        var speedDuration = speedSamples.Sum(item => item.ReadingTime);
        var averageSpeed = speedCharacters > 0 && speedDuration > 0
            ? (int?)Math.Round(speedCharacters / speedDuration * 3600)
            : null;
        var days = visible.Select(item =>
        {
            var speed = item.CharactersRead > 0 && item.ReadingTime >= 60
                ? (int?)Math.Round(item.CharactersRead / item.ReadingTime * 3600)
                : null;
            var dateText = NovelStatisticsBookDetailViewModel.FormatDate(item.DateKey);
            var charactersText = FormatCharacters(item.CharactersRead);
            var durationText = FormatDuration(item.ReadingTime);
            var speedText = FormatSpeed(speed);
            return new NovelStatisticsBookDayDisplay(
                item.DateKey,
                dateText,
                item.CharactersRead,
                item.ReadingTime,
                charactersText,
                durationText,
                speedText,
                ResourceStringHelper.FormatString(
                    "NovelStatisticsBookDetailDayAccessibleFormat",
                    "{0}, {1} characters, {2}, {3}",
                    dateText,
                    charactersText,
                    durationText,
                    speedText));
        }).ToArray();

        return new NovelStatisticsBookDetailViewModel(
            rankingItem,
            FormatCharacters(totalCharacters),
            FormatDuration(totalDuration),
            FormatSpeed(averageSpeed),
            ResourceStringHelper.FormatString(
                "NovelStatisticsBookDetailActiveDaysFormat",
                "{0} days",
                days.Length),
            days,
            errorMessage);
    }

    public Task<NovelStatisticsBookDetailViewModel?> UpdateBookStatisticsDayAsync(
        string bookId,
        string dateKey,
        int charactersRead,
        int hours,
        int minutes,
        CancellationToken ct = default)
    {
        var safeHours = Math.Max(hours, 0);
        var safeMinutes = Math.Clamp(minutes, 0, 59);
        var readingTime = (safeHours * 60d + safeMinutes) * 60d;
        return MutateBookStatisticsAsync(
            bookId,
            (statistics, book, modifiedAt) => NovelStatisticsEditor.Update(
                statistics,
                dateKey,
                book.Title,
                Math.Max(charactersRead, 0),
                readingTime,
                modifiedAt),
            ct);
    }

    public Task<NovelStatisticsBookDetailViewModel?> DeleteBookStatisticsDayAsync(
        string bookId,
        string dateKey,
        CancellationToken ct = default) =>
        MutateBookStatisticsAsync(
            bookId,
            (statistics, book, modifiedAt) => NovelStatisticsEditor.DeleteDay(
                statistics,
                dateKey,
                book.Title,
                modifiedAt),
            ct);

    public Task<NovelStatisticsBookDetailViewModel?> DeleteAllBookStatisticsAsync(
        string bookId,
        CancellationToken ct = default) =>
        MutateBookStatisticsAsync(
            bookId,
            (statistics, book, modifiedAt) => NovelStatisticsEditor.DeleteAll(
                statistics,
                book.Title,
                modifiedAt),
            ct);

    private async Task<NovelStatisticsBookDetailViewModel?> MutateBookStatisticsAsync(
        string bookId,
        Func<
            IReadOnlyList<NovelReadingStatistic>,
            NovelBook,
            long,
            IReadOnlyList<NovelReadingStatistic>> transform,
        CancellationToken ct)
    {
        if (_statisticsSidecarService == null
            || string.IsNullOrWhiteSpace(bookId)
            || !_booksById.TryGetValue(bookId, out var book)
            || string.IsNullOrWhiteSpace(book.ExtractedPath))
        {
            return null;
        }

        async Task SaveMutationAsync(CancellationToken mutationCt)
        {
            var current = await _statisticsSidecarService.LoadWithStatusAsync(
                book.ExtractedPath,
                mutationCt);
            if (current.Status is NovelStatisticsSidecarLoadStatus.Corrupt
                or NovelStatisticsSidecarLoadStatus.Unavailable)
            {
                throw new InvalidOperationException(ResourceStringHelper.GetString(
                    "NovelStatisticsBookDetailUnavailable",
                    "This book's statistics could not be read. The sidecar was left unchanged."));
            }

            var updated = transform(
                current.Statistics,
                book,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await _statisticsSidecarService.SaveAsync(
                book.ExtractedPath,
                updated,
                mutationCt);
        }

        if (_statisticsMutationCoordinator != null)
        {
            await _statisticsMutationCoordinator.ExecuteAsync(
                bookId,
                SaveMutationAsync,
                ct);
        }
        else
        {
            await SaveMutationAsync(ct);
        }

        await RefreshAfterBookStatisticsMutationAsync(ct);
        return await LoadBookStatisticsAsync(bookId, ct);
    }

    private async Task RefreshAfterBookStatisticsMutationAsync(CancellationToken ct)
    {
        if (!IsActive)
            return;

        var generation = _activationGeneration;
        var snapshot = await _dashboardService.LoadSnapshotAsync(
            _booksById.Values.ToArray(),
            ct);
        if (IsActive && generation == _activationGeneration)
            ApplySnapshot(snapshot);
    }

    private IReadOnlyList<NovelReadingStatistic> StatisticsFromSnapshot(
        string bookId,
        string title)
    {
        if (_snapshot == null)
            return [];

        return _snapshot.Days
            .Select(day =>
            {
                var contributions = day.BookContributions
                    .Where(item => item.BookId == bookId)
                    .ToArray();
                return new NovelReadingStatistic(
                    title,
                    day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    contributions.Sum(item => item.Characters),
                    contributions.Sum(item => item.ReadingTime),
                    0,
                    0,
                    0,
                    0,
                    0);
            })
            .Where(item => item.CharactersRead > 0 || item.ReadingTime > 0)
            .ToArray();
    }

    partial void OnSelectedCalendarDayChanged(NovelStatisticsCalendarDayDisplay? value)
    {
        if (value == null || _isUpdatingProjection)
            return;

        var targetIndex = IndexContaining(value.Date);
        if (targetIndex >= 0 && targetIndex != SelectedRangeOffset)
        {
            _isUpdatingRangeState = true;
            try
            {
                SelectedRangeOffset = targetIndex;
            }
            finally
            {
                _isUpdatingRangeState = false;
            }
            ResetBookRankingPagination();
            Recalculate();
            return;
        }

        UpdateCalendarDetail();
    }

    private NovelStatisticsDateRange? CurrentSelectableRange()
    {
        if (_selectableRanges.Count == 0)
            return null;

        var index = Math.Clamp(
            SelectedRangeOffset,
            0,
            _selectableRanges.Count - 1);
        return _selectableRanges[index];
    }

    private int IndexContaining(DateOnly date)
    {
        for (var index = 0; index < _selectableRanges.Count; index++)
        {
            var range = _selectableRanges[index];
            if (date >= range.Start && date <= range.End)
                return index;
        }
        return -1;
    }

    private void RebuildSelectableRanges(
        DateOnly? preferredStart,
        bool selectNewest)
    {
        if (_snapshot == null)
        {
            _selectableRanges = [];
            SetSelectedRangeOffset(0);
            NotifyRangeScrollProperties();
            return;
        }

        var today = TodayDate();
        var window = _snapshot.WindowStart == DateOnly.MinValue
            ? NovelStatisticsDashboardCalculator.RecentYear(today)
            : new NovelStatisticsDateRange(
                _snapshot.WindowStart,
                _snapshot.WindowEnd);
        _selectableRanges = NovelStatisticsDashboardCalculator.SelectableRanges(
            SelectedRangeMode,
            window);

        var offset = 0;
        if (_selectableRanges.Count > 0)
        {
            if (selectNewest || preferredStart == null)
            {
                offset = _selectableRanges.Count - 1;
            }
            else
            {
                var exact = IndexContaining(preferredStart.Value);
                offset = exact >= 0
                    ? exact
                    : Enumerable.Range(0, _selectableRanges.Count)
                        .MinBy(index => Math.Abs(
                            _selectableRanges[index].Start.DayNumber
                            - preferredStart.Value.DayNumber));
            }
        }

        SetSelectedRangeOffset(offset);
        NotifyRangeScrollProperties();
    }

    private void SetSelectedRangeOffset(int value)
    {
        _isUpdatingRangeState = true;
        try
        {
            SelectedRangeOffset = value;
        }
        finally
        {
            _isUpdatingRangeState = false;
        }
    }

    private void NotifyRangeScrollProperties()
    {
        OnPropertyChanged(nameof(SelectedRangeOffsetValue));
        OnPropertyChanged(nameof(RangeScrollMaximum));
        OnPropertyChanged(nameof(RangeScrollLargeChange));
        OnPropertyChanged(nameof(CanScrollRange));
        OnPropertyChanged(nameof(RangeScrollAccessibleText));
    }

    partial void OnSelectedDailyTargetTypeChanged(StatisticsDailyTargetType value) =>
        SaveTargetsAndRecalculate();

    partial void OnDailyCharacterTargetChanged(int value)
    {
        OnPropertyChanged(nameof(DailyCharacterTargetValue));
        SaveTargetsAndRecalculate();
    }

    partial void OnDailyDurationTargetMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(DailyDurationTargetMinutesValue));
        SaveTargetsAndRecalculate();
    }

    partial void OnWeeklyTargetDaysChanged(int value)
    {
        OnPropertyChanged(nameof(WeeklyTargetDaysValue));
        SaveTargetsAndRecalculate();
    }

    private void SaveTargetsAndRecalculate()
    {
        if (_isInitializing)
            return;

        var current = _settingsService.Current.StatisticsSettings;
        _settingsService.Set(
            settings => settings.StatisticsSettings,
            new NovelStatisticsSettings
            {
                EnableStatistics = current.EnableStatistics,
                AutostartMode = current.AutostartMode,
                ResetTimeMinutes = current.ResetTimeMinutes,
                DailyTargetType = SelectedDailyTargetType,
                DailyCharacterTarget = DailyCharacterTarget,
                DailyDurationTargetMinutes = DailyDurationTargetMinutes,
                WeeklyTargetDays = WeeklyTargetDays,
                EnableSync = current.EnableSync,
                SyncMode = current.SyncMode,
            });
        _ = _settingsService.SaveAsync();
        Recalculate();
    }

    private void UpdateCalendarDetail()
    {
        var day = SelectedCalendarDay;
        CalendarDetail = day == null
            ? new(default, 0, 0, 0, Localized("NovelStatisticsNoReadingRecords", "No reading records"))
            : new(
                day.Date,
                day.Characters,
                day.ReadingTime,
                day.ActiveBookCount,
                $"{day.Date:yyyy-MM-dd} · {FormatCharacters(day.Characters)} chars · {FormatDuration(day.ReadingTime)} · {day.ActiveBookCount} {(day.ActiveBookCount == 1 ? "book" : "books")}");
    }

    private DateOnly TodayDate() =>
        NovelStatisticsDayBoundary.ReportingDate(
            _timeProvider.GetUtcNow(),
            _settingsService.Current.StatisticsSettings.ResetTimeMinutes,
            _timeProvider.LocalTimeZone);

    private static string FormatRangeTitle(
        NovelStatisticsRangeMode mode,
        NovelStatisticsDateRange range) => mode switch
        {
            NovelStatisticsRangeMode.Year => Localized("NovelStatisticsCalendarRecentYear", "Recent year"),
            NovelStatisticsRangeMode.Month => range.Start.ToString("yyyy-MM"),
            NovelStatisticsRangeMode.Week => $"{range.Start:MM-dd} – {range.End:MM-dd}",
            _ => range.Start.ToString("yyyy-MM-dd"),
        };

    private static string Localized(string uid, string fallback) =>
        ResourceStringHelper.GetString($"{uid}/Text", fallback);

    private double TrendRawValue(NovelStatisticsTrendPoint point) =>
        SelectedTrendMetric switch
        {
            NovelStatisticsTrendMetric.Duration => point.ReadingTime,
            NovelStatisticsTrendMetric.Speed => point.AverageSpeedPerHour ?? 0,
            _ => point.Characters,
        };

    private double RankingRawValue(NovelStatisticsBookRankingRow row) =>
        SelectedRankingMetric switch
        {
            NovelStatisticsBookRankingMetric.Duration => row.ReadingTime,
            NovelStatisticsBookRankingMetric.Speed => row.AverageSpeedPerHour ?? 0,
            _ => row.Characters,
        };

    private NovelBook ResolveRankingBook(
        NovelStatisticsBookRankingRow row,
        NovelStatisticsDashboardSnapshot snapshot)
    {
        if (_booksById.TryGetValue(row.Id, out var book))
            return book;

        var record = snapshot.Books.FirstOrDefault(item => item.Id == row.Id);
        return new NovelBook
        {
            Id = row.Id,
            Title = row.Title,
            CoverPath = record?.CoverPath,
        };
    }

    private string BuildTrendToolTip(NovelStatisticsTrendPoint point)
    {
        var books = point.TopBooks.Count == 0
            ? string.Empty
            : "\n" + string.Join("\n", point.TopBooks.Select(book =>
                $"{book.Title}: {FormatCharacters(book.Characters)} chars"));
        return $"{point.Label}\n{FormatTrendValue(point, SelectedTrendMetric)}\n{FormatCharacters(point.Characters)} chars · {FormatDuration(point.ReadingTime)} · {FormatSpeed(point.AverageSpeedPerHour)}{books}";
    }

    private static string FormatTrendValue(
        NovelStatisticsTrendPoint point,
        NovelStatisticsTrendMetric metric) => metric switch
        {
            NovelStatisticsTrendMetric.Duration => FormatDuration(point.ReadingTime),
            NovelStatisticsTrendMetric.Speed => FormatSpeed(point.AverageSpeedPerHour),
            _ => $"{FormatCharacters(point.Characters)} chars",
        };

    private static string FormatTrendAxisValue(
        double value,
        NovelStatisticsTrendMetric metric) => metric switch
        {
            NovelStatisticsTrendMetric.Duration => FormatAxisDuration(value),
            NovelStatisticsTrendMetric.Speed => $"{FormatCompactNumber(value)} / h",
            _ => $"{FormatCompactNumber(value)} chars",
        };

    private static string FormatCompactNumber(double value)
    {
        var absolute = Math.Abs(value);
        if (absolute < 1_000)
        {
            return Math.Round(value)
                .ToString("N0", CultureInfo.CurrentCulture);
        }
        if (absolute < 1_000_000)
        {
            return (value / 1_000)
                .ToString("0.#", CultureInfo.CurrentCulture) + "k";
        }
        return (value / 1_000_000)
            .ToString("0.#", CultureInfo.CurrentCulture) + "M";
    }

    private static string FormatAxisDuration(double seconds)
    {
        if (seconds < 3_600)
            return $"{Math.Max((int)Math.Round(seconds / 60), 0)}m";
        return $"{(seconds / 3_600).ToString("0.#", CultureInfo.CurrentCulture)}h";
    }

    private static string FormatRankingValue(
        NovelStatisticsBookRankingRow row,
        NovelStatisticsBookRankingMetric metric) => metric switch
        {
            NovelStatisticsBookRankingMetric.Duration => FormatDuration(row.ReadingTime),
            NovelStatisticsBookRankingMetric.Speed => FormatSpeed(row.AverageSpeedPerHour),
            _ => $"{FormatCharacters(row.Characters)} chars",
        };

    private static string FormatSpeed(int? speed) =>
        speed is { } value ? $"{FormatCharacters(value)} / h" : "— / h";

    private static string FormatSpeedDay(NovelStatisticsSpeedDay? day) =>
        day is null ? "—" : $"{day.Date:yyyy-MM-dd} · {FormatSpeed(day.SpeedPerHour)}";

    private static string FormatCharacters(int characters) =>
        characters.ToString("N0", CultureInfo.CurrentCulture);

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
