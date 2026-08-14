using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public static class NovelStatisticsEditor
{
    public static IReadOnlyList<NovelReadingStatistic> Visible(
        IEnumerable<NovelReadingStatistic> statistics) =>
        ReaderStatisticsMath.Deduplicate(statistics)
            .Where(item => item.CharactersRead > 0 || item.ReadingTime > 0)
            .ToArray();

    public static IReadOnlyList<NovelReadingStatistic> Update(
        IEnumerable<NovelReadingStatistic> statistics,
        string dateKey,
        string fallbackTitle,
        int charactersRead,
        double readingTime,
        long modifiedAt)
    {
        var records = ReaderStatisticsMath.Deduplicate(statistics).ToList();
        var safeCharacters = Math.Max(charactersRead, 0);
        var safeReadingTime = Math.Max(readingTime, 0);
        var speed = safeReadingTime > 0
            ? (int)(safeCharacters / safeReadingTime * 3600d)
            : 0;
        var existing = records.FirstOrDefault(item =>
            string.Equals(item.DateKey, dateKey, StringComparison.Ordinal));
        var updated = new NovelReadingStatistic(
            string.IsNullOrWhiteSpace(existing?.Title) ? fallbackTitle : existing.Title,
            dateKey,
            safeCharacters,
            safeReadingTime,
            speed,
            speed,
            speed,
            speed,
            modifiedAt);
        if (existing == null)
            records.Add(updated);
        else
            records[records.IndexOf(existing)] = updated;
        return records.OrderBy(item => item.DateKey, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<NovelReadingStatistic> DeleteDay(
        IEnumerable<NovelReadingStatistic> statistics,
        string dateKey,
        string fallbackTitle,
        long modifiedAt) =>
        Update(statistics, dateKey, fallbackTitle, 0, 0, modifiedAt);

    public static IReadOnlyList<NovelReadingStatistic> DeleteAll(
        IEnumerable<NovelReadingStatistic> statistics,
        string fallbackTitle,
        long modifiedAt) =>
        ReaderStatisticsMath.Deduplicate(statistics)
            .Select(item => new NovelReadingStatistic(
                string.IsNullOrWhiteSpace(item.Title) ? fallbackTitle : item.Title,
                item.DateKey,
                0,
                0,
                0,
                0,
                0,
                0,
                modifiedAt))
            .OrderBy(item => item.DateKey, StringComparer.Ordinal)
            .ToArray();
}
