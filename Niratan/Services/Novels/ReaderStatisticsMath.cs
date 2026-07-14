using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public static class ReaderStatisticsMath
{
    public static NovelReadingStatistic Empty(string title, DateOnly localDate) =>
        new(
            title,
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CharactersRead: 0,
            ReadingTime: 0,
            MinReadingSpeed: 0,
            AltMinReadingSpeed: 0,
            LastReadingSpeed: 0,
            MaxReadingSpeed: 0,
            LastStatisticModified: 0);

    public static NovelReadingStatistic Update(
        NovelReadingStatistic statistic,
        double elapsedSeconds,
        int rawCharacterDelta,
        long modifiedAt)
    {
        var readingTime = statistic.ReadingTime + Math.Max(elapsedSeconds, 0);
        var charactersRead = Math.Max(statistic.CharactersRead + rawCharacterDelta, 0);
        var lastReadingSpeed = readingTime > 0
            ? (int)(charactersRead / readingTime * 3600d)
            : 0;
        var minReadingSpeed = statistic.MinReadingSpeed != 0
            ? Math.Min(statistic.MinReadingSpeed, lastReadingSpeed)
            : lastReadingSpeed;
        var altMinReadingSpeed = rawCharacterDelta != 0
            ? statistic.AltMinReadingSpeed != 0
                ? Math.Min(statistic.AltMinReadingSpeed, lastReadingSpeed)
                : lastReadingSpeed
            : statistic.AltMinReadingSpeed;

        return statistic with
        {
            CharactersRead = charactersRead,
            ReadingTime = readingTime,
            MinReadingSpeed = minReadingSpeed,
            AltMinReadingSpeed = altMinReadingSpeed,
            LastReadingSpeed = lastReadingSpeed,
            MaxReadingSpeed = Math.Max(statistic.MaxReadingSpeed, lastReadingSpeed),
            LastStatisticModified = modifiedAt,
        };
    }

    public static NovelReadingStatistic Aggregate(
        string title,
        DateOnly localDate,
        IEnumerable<NovelReadingStatistic> statistics)
    {
        var items = statistics.ToList();
        var readingTime = items.Sum(item => item.ReadingTime);
        var charactersRead = items.Sum(item => item.CharactersRead);
        var positiveMinimums = items
            .Select(item => item.MinReadingSpeed)
            .Where(speed => speed > 0)
            .ToList();
        var positiveAltMinimums = items
            .Select(item => item.AltMinReadingSpeed)
            .Where(speed => speed > 0)
            .ToList();

        return Empty(title, localDate) with
        {
            CharactersRead = charactersRead,
            ReadingTime = readingTime,
            MinReadingSpeed = positiveMinimums.Count > 0 ? positiveMinimums.Min() : 0,
            AltMinReadingSpeed = positiveAltMinimums.Count > 0 ? positiveAltMinimums.Min() : 0,
            LastReadingSpeed = readingTime > 0
                ? (int)(charactersRead / readingTime * 3600d)
                : 0,
            MaxReadingSpeed = items.Count > 0 ? items.Max(item => item.MaxReadingSpeed) : 0,
            LastStatisticModified = items.Count > 0
                ? items.Max(item => item.LastStatisticModified)
                : 0,
        };
    }

    public static IReadOnlyList<NovelReadingStatistic> Deduplicate(
        IEnumerable<NovelReadingStatistic> statistics) =>
        statistics
            .GroupBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(statistic => statistic.LastStatisticModified)
                .First())
            .OrderBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .ToList();
}
