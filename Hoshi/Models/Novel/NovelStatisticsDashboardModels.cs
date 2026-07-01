using System;
using System.Collections.Generic;
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
    double ReadingTime);

public sealed record NovelStatisticsDayAggregate(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    IReadOnlyList<NovelStatisticsBookContribution> BookContributions);

public sealed record NovelStatisticsDashboardSnapshot(
    IReadOnlyList<NovelStatisticsDayAggregate> Days,
    IReadOnlyList<string> SkippedCorruptBookIds);

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
