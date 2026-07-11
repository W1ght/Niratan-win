using System;
using System.Collections.Generic;
using System.Linq;
using Hoshi.Models.Settings;

namespace Hoshi.Models.Novel;

public sealed record NovelStatisticsDashboardTargetSettings(
    StatisticsDailyTargetType DailyTargetType,
    int DailyCharacterTarget,
    int DailyDurationTargetMinutes,
    int WeeklyTargetDays);

public readonly record struct NovelStatisticsDateRange(DateOnly Start, DateOnly End);

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
    int AverageSpeedPerHour,
    int DailyStreakDays);

public sealed record NovelStatisticsWeekSummary(
    NovelStatisticsDateRange Range,
    int Characters,
    double ReadingTime,
    int AverageSpeedPerHour,
    int TargetDays,
    int MetTargetDays,
    int DailyStreakDays,
    int AverageCharactersPerElapsedDay,
    double AverageReadingTimePerElapsedDay);

public sealed record NovelStatisticsRangeSummary(
    int Characters,
    double ReadingTime,
    int AverageSpeedPerHour,
    int TargetDays,
    int TargetProgressPercent);

public sealed record NovelStatisticsDistributionRow(
    string BookId,
    string Title,
    string? CoverPath,
    int Characters,
    double ReadingTime,
    int Percent)
{
    public string CharactersText => $"{Characters:N0} chars";
    public string PercentText => $"{Percent}%";
}
