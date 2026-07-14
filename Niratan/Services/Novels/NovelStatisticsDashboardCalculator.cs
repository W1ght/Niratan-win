using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Niratan.Models.Novel;
using Niratan.Models.Settings;

namespace Niratan.Services.Novels;

public static partial class NovelStatisticsDashboardCalculator
{
    public static NovelStatisticsDateRange RecentYear(DateOnly today) =>
        new(today.AddYears(-1).AddDays(1), today);

    public static NovelStatisticsDateRange SelectedRange(
        NovelStatisticsRangeMode mode,
        DateOnly anchor,
        NovelStatisticsDateRange window)
    {
        anchor = anchor < window.Start ? window.Start : anchor > window.End ? window.End : anchor;
        var range = mode switch
        {
            NovelStatisticsRangeMode.Year => window,
            NovelStatisticsRangeMode.Month => new(
                new DateOnly(anchor.Year, anchor.Month, 1),
                new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month))),
            NovelStatisticsRangeMode.Week => new(
                MondayStartOfWeek(anchor),
                MondayStartOfWeek(anchor).AddDays(6)),
            _ => new(anchor, anchor),
        };
        return new(
            range.Start < window.Start ? window.Start : range.Start,
            range.End > window.End ? window.End : range.End);
    }

    public static IReadOnlyList<NovelStatisticsDateRange> SelectableRanges(
        NovelStatisticsRangeMode mode,
        NovelStatisticsDateRange window)
    {
        if (window.Start == DateOnly.MinValue || window.End < window.Start)
            return [];
        if (mode == NovelStatisticsRangeMode.Year)
            return [window];

        var cursor = mode switch
        {
            NovelStatisticsRangeMode.Month => new DateOnly(
                window.Start.Year,
                window.Start.Month,
                1),
            NovelStatisticsRangeMode.Week => MondayStartOfWeek(window.Start),
            _ => window.Start,
        };
        var ranges = new List<NovelStatisticsDateRange>();
        while (cursor <= window.End)
        {
            var range = SelectedRange(mode, cursor, window);
            if (ranges.Count == 0 || ranges[^1] != range)
                ranges.Add(range);
            cursor = mode switch
            {
                NovelStatisticsRangeMode.Month => cursor.AddMonths(1),
                NovelStatisticsRangeMode.Week => cursor.AddDays(7),
                _ => cursor.AddDays(1),
            };
        }
        return ranges;
    }

    public static NovelStatisticsSpeedSummary SpeedSummary(
        IEnumerable<NovelStatisticsDayAggregate> days,
        NovelStatisticsDateRange range)
    {
        var activeDays = days
            .Where(day => Contains(range, day.Date) && SpeedSamples(day).Any())
            .OrderBy(day => day.Date)
            .ToList();
        var speedDays = activeDays.Select(day => new NovelStatisticsSpeedDay(
            day.Date,
            ValidAverageSpeedPerHour([day]) ?? 0)).ToList();
        var speeds = speedDays.Select(day => day.SpeedPerHour).Order().ToList();
        int? median = speeds.Count switch
        {
            0 => null,
            _ when speeds.Count % 2 == 1 => speeds[speeds.Count / 2],
            _ => (int)Math.Round((speeds[speeds.Count / 2 - 1] + speeds[speeds.Count / 2]) / 2d),
        };
        int? change = null;
        if (activeDays.Count >= 28)
        {
            var early = ValidAverageSpeedPerHour(activeDays.Take(14));
            var recent = ValidAverageSpeedPerHour(activeDays.TakeLast(14));
            if (early > 0 && recent.HasValue)
                change = (int)Math.Round((recent.Value - early.Value) / (double)early.Value * 100);
        }

        return new(
            ValidAverageSpeedPerHour(activeDays),
            median,
            ValidAverageSpeedPerHour(activeDays.TakeLast(7)),
            change,
            speedDays.OrderByDescending(day => day.SpeedPerHour).ThenBy(day => day.Date).FirstOrDefault(),
            speedDays.OrderBy(day => day.SpeedPerHour).ThenBy(day => day.Date).FirstOrDefault());
    }

    public static IReadOnlyList<NovelStatisticsTrendPoint> TrendPoints(
        NovelStatisticsTrendGrain grain,
        NovelStatisticsDateRange range,
        IEnumerable<NovelStatisticsDayAggregate> days)
    {
        var inRange = days.Where(day => Contains(range, day.Date))
            .OrderBy(day => day.Date).ToList();
        if (inRange.Count == 0)
            return [];
        var active = new NovelStatisticsDateRange(inRange[0].Date, inRange[^1].Date);

        return grain switch
        {
            NovelStatisticsTrendGrain.Day => Dates(active)
                .Select(date => TrendPoint(
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    date.ToString("M/d", CultureInfo.InvariantCulture),
                    inRange.Where(day => day.Date == date)))
                .ToList(),
            NovelStatisticsTrendGrain.Week => PeriodPoints(
                MondayStartOfWeek(active.Start),
                active.End,
                cursor => cursor.AddDays(7),
                cursor => $"{ISOWeek.GetYear(cursor.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(cursor.ToDateTime(TimeOnly.MinValue)):00}",
                date => MondayStartOfWeek(date),
                inRange),
            _ => PeriodPoints(
                new DateOnly(active.Start.Year, active.Start.Month, 1),
                new DateOnly(active.End.Year, active.End.Month, 1),
                cursor => cursor.AddMonths(1),
                cursor => cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                date => new DateOnly(date.Year, date.Month, 1),
                inRange),
        };
    }

    public static IReadOnlyList<NovelStatisticsCalendarDay> CalendarDays(
        NovelStatisticsDashboardSnapshot snapshot,
        DateOnly today,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var byDate = snapshot.Days.ToDictionary(day => day.Date);
        return Dates(new(snapshot.WindowStart, snapshot.WindowEnd)).Select(date =>
        {
            var day = byDate.GetValueOrDefault(date) ?? new(date, 0, 0, []);
            var percent = (int)Math.Round(TargetRatioForPublic(day, settings) * 100);
            return new NovelStatisticsCalendarDay(
                date,
                day.Characters,
                day.ReadingTime,
                day.BookContributions.Count(item => item.Characters > 0 || item.ReadingTime > 0),
                percent,
                percent >= 100,
                date == today);
        }).ToList();
    }

    public static IReadOnlyList<NovelStatisticsBookRankingRow> BookRankingRows(
        IEnumerable<NovelStatisticsDayAggregate> days,
        NovelStatisticsDateRange range,
        NovelStatisticsBookRankingMetric metric,
        int limit = 12)
    {
        return days.Where(day => Contains(range, day.Date))
            .SelectMany(day => day.BookContributions)
            .Where(item => item.Characters > 0 || item.ReadingTime > 0)
            .GroupBy(item => item.BookId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new NovelStatisticsBookRankingRow(
                    group.Key,
                    first.Title,
                    group.Sum(item => item.Characters),
                    group.Sum(item => item.ReadingTime),
                    ValidContributionSpeed(group));
            })
            .Where(row => RankingValue(row, metric) > 0)
            .OrderByDescending(row => RankingValue(row, metric))
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .Take(Math.Max(limit, 0))
            .ToList();
    }

    public static IReadOnlyList<NovelStatisticsShelfComparisonRow> ShelfComparisonRows(
        NovelStatisticsDashboardSnapshot snapshot,
        NovelShelfState shelfState,
        NovelStatisticsDateRange range,
        string unshelvedName = "Unshelved")
    {
        var booksById = snapshot.Books.ToDictionary(book => book.Id, StringComparer.Ordinal);
        var contributions = snapshot.Days.Where(day => Contains(range, day.Date))
            .SelectMany(day => day.BookContributions)
            .GroupBy(item => item.BookId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var rows = new List<NovelStatisticsShelfComparisonRow>();
        var shelved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shelf in shelfState.Shelves)
        {
            var ids = shelf.BookIds.Where(booksById.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            shelved.UnionWith(ids);
            if (ids.Count > 0)
                rows.Add(ShelfRow($"shelf:{shelf.Name}", shelf.Name, ids, booksById, contributions));
        }
        var unshelved = booksById.Keys.Where(id => !shelved.Contains(id)).ToList();
        if (unshelved.Count > 0)
            rows.Add(ShelfRow("unshelved", unshelvedName, unshelved, booksById, contributions));
        return rows.OrderByDescending(row => row.RecordedCharacters)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IReadOnlyList<NovelStatisticsTrendPoint> PeriodPoints(
        DateOnly start,
        DateOnly end,
        Func<DateOnly, DateOnly> next,
        Func<DateOnly, string> label,
        Func<DateOnly, DateOnly> key,
        IReadOnlyList<NovelStatisticsDayAggregate> days)
    {
        var grouped = days.GroupBy(day => key(day.Date)).ToDictionary(group => group.Key, group => group.AsEnumerable());
        var result = new List<NovelStatisticsTrendPoint>();
        for (var cursor = start; cursor <= end; cursor = next(cursor))
            result.Add(TrendPoint(label(cursor), label(cursor), grouped.GetValueOrDefault(cursor) ?? []));
        return result;
    }

    private static NovelStatisticsTrendPoint TrendPoint(
        string id,
        string label,
        IEnumerable<NovelStatisticsDayAggregate> source)
    {
        var days = source.ToList();
        var top = days.SelectMany(day => day.BookContributions)
            .Where(item => item.Characters > 0)
            .GroupBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new NovelStatisticsTrendBookBreakdown(group.First().Title, group.Sum(item => item.Characters)))
            .OrderByDescending(item => item.Characters)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(5).ToList();
        return new(id, label, days.Sum(day => day.Characters), days.Sum(day => day.ReadingTime), ValidAverageSpeedPerHour(days), top);
    }

    private static NovelStatisticsShelfComparisonRow ShelfRow(
        string id,
        string name,
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, NovelStatisticsBookRecord> books,
        IReadOnlyDictionary<string, List<NovelStatisticsBookContribution>> contributions)
    {
        var items = ids.SelectMany(bookId => contributions.GetValueOrDefault(bookId) ?? []).ToList();
        return new(id, name, ids.Count, ids.Sum(bookId => books[bookId].TotalCharacterCount),
            items.Sum(item => item.Characters), items.Sum(item => item.ReadingTime), ValidContributionSpeed(items));
    }

    private static int? ValidContributionSpeed(IEnumerable<NovelStatisticsBookContribution> source)
    {
        var samples = source.Where(item => item.IsValidSpeedSample).ToList();
        return samples.Count == 0 ? null : AverageSpeedPerHour(samples.Sum(item => item.Characters), samples.Sum(item => item.ReadingTime));
    }

    private static double RankingValue(NovelStatisticsBookRankingRow row, NovelStatisticsBookRankingMetric metric) =>
        metric switch { NovelStatisticsBookRankingMetric.Duration => row.ReadingTime, NovelStatisticsBookRankingMetric.Speed => row.AverageSpeedPerHour ?? 0, _ => row.Characters };

    private static bool Contains(NovelStatisticsDateRange range, DateOnly date) => date >= range.Start && date <= range.End;

    private static IReadOnlyList<DateOnly> Dates(NovelStatisticsDateRange range)
    {
        var result = new List<DateOnly>();
        for (var date = range.Start; date <= range.End; date = date.AddDays(1)) result.Add(date);
        return result;
    }

    private static double TargetRatioForPublic(NovelStatisticsDayAggregate day, NovelStatisticsDashboardTargetSettings settings) =>
        settings.DailyTargetType == StatisticsDailyTargetType.Duration
            ? settings.DailyDurationTargetMinutes <= 0 ? 0 : day.ReadingTime / (settings.DailyDurationTargetMinutes * 60d)
            : settings.DailyCharacterTarget <= 0 ? 0 : day.Characters / (double)settings.DailyCharacterTarget;
}
