using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Settings;

namespace Hoshi.ViewModels.Pages;

public partial class NovelStatisticsDashboardViewModel : ObservableObject
{
    private readonly INovelStatisticsDashboardService _dashboardService;
    private readonly ISettingsService _settingsService;
    private readonly TimeProvider _timeProvider;
    private NovelStatisticsDashboardSnapshot? _snapshot;
    private NovelShelfState _shelfState = new([], []);
    private bool _isInitializing = true;

    public NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService)
        : this(dashboardService, settingsService, TimeProvider.System)
    {
    }

    internal NovelStatisticsDashboardViewModel(
        INovelStatisticsDashboardService dashboardService,
        ISettingsService settingsService,
        TimeProvider timeProvider)
    {
        _dashboardService = dashboardService;
        _settingsService = settingsService;
        _timeProvider = timeProvider;

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
    public partial NovelStatisticsRangeMode SelectedRangeMode { get; set; } =
        NovelStatisticsRangeMode.Year;

    [ObservableProperty]
    public partial DateTimeOffset? AnchorDate { get; set; }

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
    public partial ObservableCollection<NovelStatisticsWeekDayDisplay> WeekDays { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsMetricDisplay> SpeedMetrics { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsTrendDisplayPoint> TrendPoints { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsCalendarDayDisplay> CalendarDays { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsBookRankingDisplayRow> BookRankingRows { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<NovelStatisticsShelfComparisonDisplayRow> ShelfComparisonRows { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCorruptBooks))]
    [NotifyPropertyChangedFor(nameof(CorruptWarningText))]
    public partial ObservableCollection<string> SkippedCorruptBookIds { get; set; } = [];

    public bool HasCorruptBooks => SkippedCorruptBookIds.Count > 0;
    public string CorruptWarningText => HasCorruptBooks
        ? "Some statistics are temporarily unavailable. The affected sidecar files were left unchanged."
        : string.Empty;

    [ObservableProperty]
    public partial NovelStatisticsCalendarDayDisplay? SelectedCalendarDay { get; set; }

    [ObservableProperty]
    public partial NovelStatisticsCalendarDetailDisplay CalendarDetail { get; set; } =
        new(default, 0, 0, 0, "No reading records");

    public async Task ActivateAsync(
        IReadOnlyList<NovelBook> books,
        NovelShelfState shelfState,
        CancellationToken ct)
    {
        _shelfState = shelfState;
        _dashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
        _dashboardService.SnapshotRefreshed += OnSnapshotRefreshed;
        IsActive = true;
        IsLoading = true;
        try
        {
            ApplySnapshot(await _dashboardService.LoadSnapshotAsync(books, ct));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        IsRefreshing = false;
        _dashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
    }

    private void OnSnapshotRefreshed(
        object? sender,
        NovelStatisticsDashboardSnapshot snapshot)
    {
        if (!IsActive)
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
        _snapshot = snapshot;
        HasData = snapshot.Days.Count > 0;
        if (AnchorDate == null)
        {
            var initialAnchor = snapshot.Days.LastOrDefault()?.Date ?? TodayDate();
            AnchorDate = LocalDateTimeOffset(initialAnchor);
        }
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
        var anchor = AnchorDate is { } selectedAnchor
            ? DateOnly.FromDateTime(selectedAnchor.LocalDateTime)
            : snapshot.Days.LastOrDefault()?.Date ?? today;
        var range = NovelStatisticsDashboardCalculator.SelectedRange(
            SelectedRangeMode,
            anchor,
            window);

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

        WeekDays = new(Week.Days.Select(day => new NovelStatisticsWeekDayDisplay(
            day.Date,
            day.Date.ToString("ddd", CultureInfo.CurrentCulture),
            day.Percent is { } percent ? $"{percent}%" : "—",
            day.IsToday,
            day.IsFuture,
            day.MetTarget)));
        SpeedMetrics = new(
        [
            new("Weighted", FormatSpeed(Speed.WeightedAveragePerHour)),
            new("Median Active Day", FormatSpeed(Speed.MedianActiveDayPerHour)),
            new("Last 7 Active Days", FormatSpeed(Speed.LastSevenActiveDaysPerHour)),
            new("Change", Speed.ChangePercent is { } change ? $"{change:+0;-0;0}%" : "—"),
            new("Fastest", FormatSpeedDay(Speed.FastestDay)),
            new("Slowest", FormatSpeedDay(Speed.SlowestDay)),
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

        var ranking = NovelStatisticsDashboardCalculator.BookRankingRows(
            snapshot.Days,
            range,
            SelectedRankingMetric);
        var rankingValues = ranking.Select(RankingRawValue).ToArray();
        var rankingMaximum = Math.Max(rankingValues.DefaultIfEmpty().Max(), 1);
        BookRankingRows = new(ranking.Select((row, index) =>
            new NovelStatisticsBookRankingDisplayRow(
                row.Id,
                row.Title,
                FormatRankingValue(row, SelectedRankingMetric),
                Math.Clamp(rankingValues[index] / rankingMaximum, 0, 1))));

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
        var selectedDate = SelectedCalendarDay?.Date ?? anchor;
        SelectedCalendarDay = CalendarDays.FirstOrDefault(day => day.Date == selectedDate)
            ?? CalendarDays.LastOrDefault();
        UpdateCalendarDetail();
    }

    partial void OnSelectedRangeModeChanged(NovelStatisticsRangeMode value) => Recalculate();
    partial void OnAnchorDateChanged(DateTimeOffset? value) => Recalculate();
    partial void OnSelectedTrendGrainChanged(NovelStatisticsTrendGrain value) => Recalculate();
    partial void OnSelectedTrendMetricChanged(NovelStatisticsTrendMetric value) => Recalculate();
    partial void OnSelectedRankingMetricChanged(NovelStatisticsBookRankingMetric value) => Recalculate();

    partial void OnSelectedCalendarDayChanged(NovelStatisticsCalendarDayDisplay? value)
    {
        if (value == null)
            return;

        AnchorDate = LocalDateTimeOffset(value.Date);
        UpdateCalendarDetail();
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
            ? new(default, 0, 0, 0, "No reading records")
            : new(
                day.Date,
                day.Characters,
                day.ReadingTime,
                day.ActiveBookCount,
                $"{day.Date:yyyy-MM-dd} · {FormatCharacters(day.Characters)} chars · {FormatDuration(day.ReadingTime)} · {day.ActiveBookCount} {(day.ActiveBookCount == 1 ? "book" : "books")}");
    }

    private DateOnly TodayDate() =>
        DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);

    private DateTimeOffset LocalDateTimeOffset(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(value, _timeProvider.LocalTimeZone.GetUtcOffset(value));
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
