using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Novels;

public sealed class NovelStatisticsDashboardService : INovelStatisticsDashboardService
{
    private readonly INovelStatisticsSidecarService _statisticsSidecarService;
    private readonly INovelBookSidecarService? _bookSidecarService;
    private readonly TimeProvider _timeProvider;
    private readonly NovelStatisticsDashboardCache? _cache;

    public NovelStatisticsDashboardService(INovelStatisticsSidecarService statisticsSidecarService)
        : this(statisticsSidecarService, null, TimeProvider.System, null)
    {
    }

    public NovelStatisticsDashboardService(
        INovelStatisticsSidecarService statisticsSidecarService,
        INovelBookSidecarService bookSidecarService)
        : this(statisticsSidecarService, bookSidecarService, TimeProvider.System, null)
    {
    }

    internal NovelStatisticsDashboardService(
        INovelStatisticsSidecarService statisticsSidecarService,
        INovelBookSidecarService bookSidecarService,
        NovelStatisticsDashboardCache cache)
        : this(statisticsSidecarService, bookSidecarService, TimeProvider.System, cache)
    {
    }

    public NovelStatisticsDashboardService(
        INovelStatisticsSidecarService statisticsSidecarService,
        INovelBookSidecarService? bookSidecarService,
        TimeProvider timeProvider)
        : this(statisticsSidecarService, bookSidecarService, timeProvider, null)
    {
    }

    internal NovelStatisticsDashboardService(
        INovelStatisticsSidecarService statisticsSidecarService,
        INovelBookSidecarService? bookSidecarService,
        TimeProvider timeProvider,
        NovelStatisticsDashboardCache? cache)
    {
        _statisticsSidecarService = statisticsSidecarService;
        _bookSidecarService = bookSidecarService;
        _timeProvider = timeProvider;
        _cache = cache;
    }

    public async Task<NovelStatisticsDashboardSnapshot> LoadSnapshotAsync(
        IReadOnlyList<NovelBook> books,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var cacheKey = NovelStatisticsDashboardCache.CreateKey(books, today);
        if (_cache != null
            && await _cache.TryLoadAsync(cacheKey, ct) is { } cached)
        {
            return cached;
        }
        var windowStart = today.AddYears(-1).AddDays(1);
        var contributionsByDate = new Dictionary<DateOnly, List<NovelStatisticsBookContribution>>();
        var bookRecords = new List<NovelStatisticsBookRecord>();
        var skippedCorruptBookIds = new List<string>();

        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            var totalCharacterCount = 0;
            if (!string.IsNullOrWhiteSpace(book.ExtractedPath)
                && _bookSidecarService != null)
            {
                totalCharacterCount = (await _bookSidecarService.LoadBookInfoAsync(
                    book.ExtractedPath,
                    ct))?.CharacterCount ?? 0;
            }
            bookRecords.Add(new NovelStatisticsBookRecord(
                book.Id,
                book.Title,
                book.CoverPath,
                totalCharacterCount));

            if (string.IsNullOrWhiteSpace(book.ExtractedPath))
                continue;

            var loadResult = await _statisticsSidecarService.LoadWithStatusAsync(
                book.ExtractedPath,
                ct);
            if (loadResult == null)
            {
                loadResult = new NovelStatisticsSidecarLoadResult(
                    NovelStatisticsSidecarLoadStatus.Loaded,
                    await _statisticsSidecarService.LoadAsync(book.ExtractedPath, ct));
            }
            if (loadResult.Status is NovelStatisticsSidecarLoadStatus.Corrupt
                or NovelStatisticsSidecarLoadStatus.Unavailable)
            {
                skippedCorruptBookIds.Add(book.Id);
                continue;
            }

            var statistics = loadResult.Statistics;
            foreach (var statistic in statistics)
            {
                if (statistic.CharactersRead <= 0 && statistic.ReadingTime <= 0)
                    continue;
                if (!DateOnly.TryParseExact(
                    statistic.DateKey,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
                {
                    continue;
                }
                if (date < windowStart || date > today)
                    continue;

                var contribution = new NovelStatisticsBookContribution(
                    book.Id,
                    string.IsNullOrWhiteSpace(book.Title) ? statistic.Title : book.Title,
                    book.CoverPath,
                    statistic.CharactersRead,
                    statistic.ReadingTime,
                    statistic.CharactersRead > 0 && statistic.ReadingTime >= 60);
                contributionsByDate.GetOrAdd(date).Add(contribution);
            }
        }

        var days = contributionsByDate
            .Select(pair =>
            {
                var contributions = pair.Value
                    .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return new NovelStatisticsDayAggregate(
                    pair.Key,
                    contributions.Sum(item => item.Characters),
                    contributions.Sum(item => item.ReadingTime),
                    contributions);
            })
            .OrderBy(day => day.Date)
            .ToList();

        var snapshot = new NovelStatisticsDashboardSnapshot(
            windowStart,
            today,
            days,
            bookRecords
                .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => book.Id, StringComparer.Ordinal)
                .ToList(),
            skippedCorruptBookIds.Distinct(StringComparer.Ordinal).ToList());
        if (_cache != null)
            await _cache.StoreAsync(cacheKey, snapshot, ct);
        return snapshot;
    }
}

public static partial class NovelStatisticsDashboardCalculator
{
    public static NovelStatisticsTodaySummary TodaySummary(
        NovelStatisticsDashboardSnapshot snapshot,
        DateOnly today,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var daysByDate = DictionaryByDate(snapshot.Days);
        var aggregate = daysByDate.GetValueOrDefault(today) ?? EmptyDay(today);
        return new NovelStatisticsTodaySummary(
            today,
            Percent(TargetRatio(aggregate, settings)),
            aggregate.Characters,
            aggregate.ReadingTime,
            ValidAverageSpeedPerHour([aggregate]) ?? 0,
            DailyGoalStreak(daysByDate, today, settings));
    }

    public static NovelStatisticsWeekSummary WeekSummary(
        NovelStatisticsDashboardSnapshot snapshot,
        DateOnly today,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var start = MondayStartOfWeek(today);
        var range = new NovelStatisticsDateRange(start, start.AddDays(6));
        var daysByDate = DictionaryByDate(snapshot.Days);
        var days = DatesInRange(range).Select(date => daysByDate.GetValueOrDefault(date) ?? EmptyDay(date)).ToList();
        var characters = days.Sum(day => day.Characters);
        var readingTime = days.Sum(day => day.ReadingTime);
        var elapsedDays = Math.Clamp(today.DayNumber - start.DayNumber + 1, 1, 7);

        return new NovelStatisticsWeekSummary(
            range,
            elapsedDays,
            characters,
            readingTime,
            ValidAverageSpeedPerHour(days),
            settings.WeeklyTargetDays,
            days.Count(day => TargetRatio(day, settings) >= 1),
            DailyGoalStreak(daysByDate, today, settings),
            WeeklyGoalStreak(daysByDate, today, settings),
            (int)Math.Round(characters / (double)elapsedDays),
            readingTime / elapsedDays,
            days.Select(day => new NovelStatisticsWeekDaySummary(
                day.Date,
                day.Date == today,
                day.Date > today,
                day.Date > today || (day.Characters <= 0 && day.ReadingTime <= 0)
                    ? null
                    : Percent(TargetRatio(day, settings)),
                day.Date <= today && TargetRatio(day, settings) >= 1)).ToList());
    }

    public static NovelStatisticsRangeSummary RangeSummary(
        IEnumerable<NovelStatisticsDayAggregate> days,
        NovelStatisticsDateRange range,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var rangeDays = days.Where(day => day.Date >= range.Start && day.Date <= range.End).ToList();
        var characters = rangeDays.Sum(day => day.Characters);
        var readingTime = rangeDays.Sum(day => day.ReadingTime);
        return new NovelStatisticsRangeSummary(
            characters,
            readingTime,
            ValidAverageSpeedPerHour(rangeDays) ?? 0,
            rangeDays.Count(day => TargetRatio(day, settings) >= 1),
            range.Start == range.End && rangeDays.Count == 1 ? Percent(TargetRatio(rangeDays[0], settings)) : 0);
    }

    public static IReadOnlyList<NovelStatisticsDistributionRow> DistributionRows(
        IEnumerable<NovelStatisticsDayAggregate> days,
        NovelStatisticsDateRange range,
        StatisticsDailyTargetType targetType)
    {
        var totals = days
            .Where(day => day.Date >= range.Start && day.Date <= range.End)
            .SelectMany(day => day.BookContributions)
            .Where(contribution => contribution.Characters > 0 || contribution.ReadingTime > 0)
            .GroupBy(contribution => contribution.BookId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new NovelStatisticsDistributionRow(
                    first.BookId,
                    first.Title,
                    first.CoverPath,
                    group.Sum(item => item.Characters),
                    group.Sum(item => item.ReadingTime),
                    Percent: 0);
            })
            .ToList();
        var totalMetric = totals.Sum(row => MetricValue(row, targetType));

        return totals
            .Select(row => row with
            {
                Percent = totalMetric > 0 ? Percent(MetricValue(row, targetType) / totalMetric) : 0,
            })
            .OrderByDescending(row => MetricValue(row, targetType))
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static DateOnly MondayStartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    public static int AverageSpeedPerHour(int characters, double readingTime)
    {
        if (readingTime <= 0)
            return 0;
        return (int)Math.Round(characters / readingTime * 3600);
    }

    private static IReadOnlyList<DateOnly> DatesInRange(NovelStatisticsDateRange range)
    {
        var dates = new List<DateOnly>();
        for (var cursor = range.Start; cursor <= range.End; cursor = cursor.AddDays(1))
            dates.Add(cursor);
        return dates;
    }

    private static int DailyGoalStreak(
        IReadOnlyDictionary<DateOnly, NovelStatisticsDayAggregate> daysByDate,
        DateOnly today,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var cursor = today;
        if (TargetRatio(daysByDate.GetValueOrDefault(today) ?? EmptyDay(today), settings) < 1)
            cursor = cursor.AddDays(-1);

        var streak = 0;
        while (TargetRatio(daysByDate.GetValueOrDefault(cursor) ?? EmptyDay(cursor), settings) >= 1)
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    private static int WeeklyGoalStreak(
        IReadOnlyDictionary<DateOnly, NovelStatisticsDayAggregate> daysByDate,
        DateOnly today,
        NovelStatisticsDashboardTargetSettings settings)
    {
        var weekStart = MondayStartOfWeek(today);
        if (!WeekMet(daysByDate, weekStart, settings))
            weekStart = weekStart.AddDays(-7);

        var streak = 0;
        while (WeekMet(daysByDate, weekStart, settings))
        {
            streak++;
            weekStart = weekStart.AddDays(-7);
        }
        return streak;
    }

    private static bool WeekMet(
        IReadOnlyDictionary<DateOnly, NovelStatisticsDayAggregate> daysByDate,
        DateOnly weekStart,
        NovelStatisticsDashboardTargetSettings settings) =>
        Enumerable.Range(0, 7).Count(offset =>
            TargetRatio(daysByDate.GetValueOrDefault(weekStart.AddDays(offset))
                ?? EmptyDay(weekStart.AddDays(offset)), settings) >= 1)
        >= settings.WeeklyTargetDays;

    private static int? ValidAverageSpeedPerHour(
        IEnumerable<NovelStatisticsDayAggregate> days)
    {
        var samples = days.SelectMany(SpeedSamples).ToList();
        return samples.Count == 0
            ? null
            : AverageSpeedPerHour(
                samples.Sum(sample => sample.Characters),
                samples.Sum(sample => sample.ReadingTime));
    }

    private static IEnumerable<(int Characters, double ReadingTime)> SpeedSamples(
        NovelStatisticsDayAggregate day)
    {
        if (day.BookContributions.Count == 0)
        {
            if (day.Characters > 0 && day.ReadingTime >= 60)
                yield return (day.Characters, day.ReadingTime);
            yield break;
        }

        foreach (var contribution in day.BookContributions)
        {
            if (contribution.IsValidSpeedSample)
                yield return (contribution.Characters, contribution.ReadingTime);
        }
    }

    private static IReadOnlyDictionary<DateOnly, NovelStatisticsDayAggregate> DictionaryByDate(
        IEnumerable<NovelStatisticsDayAggregate> days) =>
        days.ToDictionary(day => day.Date);

    private static NovelStatisticsDayAggregate EmptyDay(DateOnly date) =>
        new(date, 0, 0, []);

    private static double TargetRatio(
        NovelStatisticsDayAggregate day,
        NovelStatisticsDashboardTargetSettings settings)
    {
        return settings.DailyTargetType switch
        {
            StatisticsDailyTargetType.Duration when settings.DailyDurationTargetMinutes > 0 =>
                day.ReadingTime / (settings.DailyDurationTargetMinutes * 60.0),
            StatisticsDailyTargetType.Characters when settings.DailyCharacterTarget > 0 =>
                day.Characters / (double)settings.DailyCharacterTarget,
            _ => 0,
        };
    }

    private static double MetricValue(NovelStatisticsDistributionRow row, StatisticsDailyTargetType targetType) =>
        targetType == StatisticsDailyTargetType.Duration ? row.ReadingTime : row.Characters;

    private static int Percent(double ratio) =>
        (int)Math.Round(ratio * 100);
}

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(
        this IDictionary<TKey, TValue> dictionary,
        TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = new TValue();
            dictionary[key] = value;
        }

        return value;
    }
}
