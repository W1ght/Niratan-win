using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Hoshi.Models.Settings;

namespace Hoshi.Models.Novel;

public sealed record NovelStatisticsDashboardTargetSettings(
    StatisticsDailyTargetType DailyTargetType,
    int DailyCharacterTarget,
    int DailyDurationTargetMinutes,
    int WeeklyTargetDays);

public readonly record struct NovelStatisticsDateRange(DateOnly Start, DateOnly End);

public enum NovelStatisticsRangeMode { Year, Month, Week, Day }
public enum NovelStatisticsTrendMetric { Characters, Duration, Speed }
public enum NovelStatisticsTrendGrain { Day, Week, Month }
public enum NovelStatisticsTrendChartStyle { Bar, Line }
public enum NovelStatisticsBookRankingMetric { Characters, Duration, Speed }

public sealed record NovelStatisticsBookContribution(
    string BookId,
    string Title,
    string? CoverPath,
    int Characters,
    double ReadingTime,
    bool IsValidSpeedSample)
{
    public NovelStatisticsBookContribution(
        string BookId,
        string Title,
        string? CoverPath,
        int Characters,
        double ReadingTime)
        : this(
            BookId,
            Title,
            CoverPath,
            Characters,
            ReadingTime,
            Characters > 0 && ReadingTime >= 60)
    {
    }
}

public sealed record NovelStatisticsBookRecord(
    string Id,
    string Title,
    string? CoverPath,
    int TotalCharacterCount);

public sealed record NovelStatisticsDayAggregate(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    IReadOnlyList<NovelStatisticsBookContribution> BookContributions);

[method: JsonConstructor]
public sealed partial record NovelStatisticsDashboardSnapshot(
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<NovelStatisticsDayAggregate> Days,
    IReadOnlyList<NovelStatisticsBookRecord> Books,
    IReadOnlyList<string> SkippedCorruptBookIds);

public sealed partial record NovelStatisticsDashboardSnapshot
{
    public NovelStatisticsDashboardSnapshot(
        IReadOnlyList<NovelStatisticsDayAggregate> days,
        IReadOnlyList<string> skippedCorruptBookIds)
        : this(
            days.Count == 0 ? DateOnly.MinValue : days.Min(day => day.Date),
            days.Count == 0 ? DateOnly.MinValue : days.Max(day => day.Date),
            days,
            [],
            skippedCorruptBookIds)
    {
    }
}

public enum NovelStatisticsSidecarLoadStatus
{
    Missing,
    Loaded,
    Corrupt,
    Unavailable,
}

public sealed record NovelStatisticsSidecarLoadResult(
    NovelStatisticsSidecarLoadStatus Status,
    IReadOnlyList<NovelReadingStatistic> Statistics);

public sealed record NovelStatisticsTodaySummary(
    DateOnly Date,
    int TargetPercent,
    int Characters,
    double ReadingTime,
    int? AverageSpeedPerHour,
    int DailyStreakDays);

public sealed record NovelStatisticsWeekSummary(
    NovelStatisticsDateRange Range,
    int ElapsedDays,
    int Characters,
    double ReadingTime,
    int? AverageSpeedPerHour,
    int TargetDays,
    int MetTargetDays,
    int DailyStreakDays,
    int WeeklyStreakWeeks,
    int AverageCharactersPerElapsedDay,
    double AverageReadingTimePerElapsedDay,
    IReadOnlyList<NovelStatisticsWeekDaySummary> Days);

public sealed record NovelStatisticsWeekDaySummary(
    DateOnly Date,
    bool IsToday,
    bool IsFuture,
    int? Percent,
    bool MetTarget);

public sealed record NovelStatisticsRangeSummary(
    int Characters,
    double ReadingTime,
    int? AverageSpeedPerHour,
    int TargetDays,
    int TargetProgressPercent);

public sealed record NovelStatisticsSpeedDay(DateOnly Date, int SpeedPerHour);

public sealed record NovelStatisticsSpeedSummary(
    int? WeightedAveragePerHour,
    int? MedianActiveDayPerHour,
    int? LastSevenActiveDaysPerHour,
    int? ChangePercent,
    NovelStatisticsSpeedDay? FastestDay,
    NovelStatisticsSpeedDay? SlowestDay);

public sealed record NovelStatisticsTrendBookBreakdown(string Title, int Characters);

public sealed record NovelStatisticsTrendPoint(
    string Id,
    string Label,
    int Characters,
    double ReadingTime,
    int? AverageSpeedPerHour,
    IReadOnlyList<NovelStatisticsTrendBookBreakdown> TopBooks);

public sealed record NovelStatisticsCalendarDay(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    int ActiveBookCount,
    int TargetPercent,
    bool MetTarget,
    bool IsToday);

public sealed record NovelStatisticsBookRankingRow(
    string Id,
    string Title,
    int Characters,
    double ReadingTime,
    int? AverageSpeedPerHour);

public sealed record NovelStatisticsShelfComparisonRow(
    string Id,
    string Name,
    int BookCount,
    int TotalBookCharacters,
    int RecordedCharacters,
    double ReadingTime,
    int? AverageSpeedPerHour);

public sealed record NovelStatisticsTrendDisplayPoint(
    string Id,
    string Label,
    string ValueText,
    double NormalizedValue,
    string ToolTipText)
{
    public NovelStatisticsTrendDisplayPoint(
        string id,
        string label,
        string valueText)
        : this(id, label, valueText, 0, valueText)
    {
    }
}

public sealed record NovelStatisticsBookRankingDisplayRow(
    string Id,
    string Title,
    string ValueText,
    double NormalizedValue)
{
    public NovelStatisticsBookRankingDisplayRow(
        string id,
        string title,
        string valueText)
        : this(id, title, valueText, 0)
    {
    }
}

public sealed record NovelStatisticsMetricDisplay(string Label, string Value);

public sealed record NovelStatisticsWeekDayDisplay(
    DateOnly Date,
    string Weekday,
    string PercentText,
    bool IsToday,
    bool IsFuture,
    bool MetTarget);

public sealed record NovelStatisticsCalendarDayDisplay(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    int ActiveBookCount,
    int TargetPercent,
    string AccessibleText,
    double HeatOpacity,
    bool IsInSelectedRange,
    bool IsToday);

public sealed record NovelStatisticsCalendarDetailDisplay(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    int ActiveBookCount,
    string Text);

public sealed record NovelStatisticsShelfComparisonDisplayRow(
    string Id,
    string Name,
    string DetailText,
    string SpeedText,
    double RecordedProgress,
    double NormalizedVolume);

public static class NovelStatisticsDashboardTargets
{
    public static int SnapCharacterTarget(int value) => Snap(value, 500, 20_000, 500);
    public static int SnapDurationTarget(int value) => Snap(value, 5, 240, 5);
    public static int SnapWeeklyTargetDays(int value) => Math.Clamp(value, 1, 7);

    private static int Snap(int value, int minimum, int maximum, int step)
    {
        var clamped = Math.Clamp(value, minimum, maximum);
        var snapped = minimum + ((clamped - minimum + step / 2) / step) * step;
        return Math.Clamp(snapped, minimum, maximum);
    }
}
