using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Niratan.Models.Novel;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public static class TtuSyncFileNames
{
    private static readonly HashSet<char> EscapedTtuFilenameChars = ['/', '?', '<', '>', '\\', ':', '*', '|', '%', '"'];

    public static string GetProgressFileName(TtuProgress progress)
    {
        var timestamp = progress.LastBookmarkModified.ToUnixTimeMilliseconds();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"progress_1_6_{timestamp}_{FormatNumber(progress.Progress)}.json");
    }

    public static string GetAudioBookFileName(TtuAudioBook audioBook) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"audioBook_1_6_{audioBook.LastAudioBookModified}_{FormatNumber(audioBook.PlaybackPosition)}.json");

    public static string GetStatisticsFileName(IReadOnlyList<NovelReadingStatistic> statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        double readingTime = 0;
        var charactersRead = 0;
        var minReadingSpeed = 0;
        var altMinReadingSpeed = 0;
        var maxReadingSpeed = 0;
        var weightedSum = 0;
        var validReadingDays = 0;
        long lastStatisticModified = 0;

        foreach (var stat in statistics)
        {
            readingTime += stat.ReadingTime;
            charactersRead += stat.CharactersRead;
            minReadingSpeed = minReadingSpeed > 0
                ? Math.Min(minReadingSpeed, stat.MinReadingSpeed)
                : stat.MinReadingSpeed;
            altMinReadingSpeed = altMinReadingSpeed > 0
                ? Math.Min(altMinReadingSpeed, stat.AltMinReadingSpeed)
                : stat.AltMinReadingSpeed;
            maxReadingSpeed = Math.Max(maxReadingSpeed, stat.MaxReadingSpeed);
            weightedSum += (int)stat.ReadingTime * stat.CharactersRead;
            lastStatisticModified = Math.Max(lastStatisticModified, stat.LastStatisticModified);
            if (stat.ReadingTime > 0)
                validReadingDays++;
        }

        var averageReadingTime = validReadingDays > 0
            ? Math.Ceiling(readingTime / validReadingDays)
            : 0;
        var averageWeightedReadingTime = charactersRead > 0
            ? Math.Ceiling((double)weightedSum / charactersRead)
            : 0;
        var averageCharactersRead = validReadingDays > 0
            ? Math.Ceiling((double)charactersRead / validReadingDays)
            : 0;
        var averageWeightedCharactersRead = readingTime > 0
            ? Math.Ceiling(weightedSum / readingTime)
            : 0;
        var lastReadingSpeed = readingTime > 0
            ? Math.Ceiling(3600.0 * charactersRead / readingTime)
            : 0;
        var averageReadingSpeed = averageReadingTime > 0
            ? Math.Ceiling(3600 * averageCharactersRead / averageReadingTime)
            : 0;
        var averageWeightedReadingSpeed = averageWeightedReadingTime > 0
            ? Math.Ceiling(3600 * averageWeightedCharactersRead / averageWeightedReadingTime)
            : 0;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"statistics_1_6_{lastStatisticModified}_{charactersRead}_{FormatNumber(readingTime)}_{minReadingSpeed}_{altMinReadingSpeed}_{FormatNumber(lastReadingSpeed)}_{maxReadingSpeed}_{FormatNumber(averageReadingTime)}_{FormatNumber(averageWeightedReadingTime)}_{FormatNumber(averageCharactersRead)}_{FormatNumber(averageWeightedCharactersRead)}_{FormatNumber(averageReadingSpeed)}_{FormatNumber(averageWeightedReadingSpeed)}_na.json");
    }

    public static DateTimeOffset? ParseProgressTimestamp(string? fileName) =>
        ParseTimestamp(fileName, "progress_", minimumPartCount: 5);

    public static DateTimeOffset? ParseAudioBookTimestamp(string? fileName) =>
        ParseTimestamp(fileName, "audioBook_", minimumPartCount: 4);

    public static string SanitizeTtuFilename(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var result = title;
        if (result.EndsWith(' '))
            result = result[..^1] + "~ttu-spc~";
        if (result.EndsWith('.'))
            result = result[..^1] + "~ttu-dend~";

        var builder = new StringBuilder(result.Length);
        foreach (var c in result)
        {
            if (c == '*')
            {
                builder.Append("~ttu-star~");
                continue;
            }

            if (EscapedTtuFilenameChars.Contains(c))
            {
                foreach (var scalar in c.ToString().EnumerateRunes())
                    builder.Append(CultureInfo.InvariantCulture, $"%{scalar.Value:X2}");
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    public static string DesanitizeTtuFilename(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        return Uri.UnescapeDataString(title)
            .Replace("~ttu-star~", "*", StringComparison.Ordinal)
            .Replace("~ttu-dend~", ".", StringComparison.Ordinal)
            .Replace("~ttu-spc~", " ", StringComparison.Ordinal);
    }

    private static DateTimeOffset? ParseTimestamp(
        string? fileName,
        string prefix,
        int minimumPartCount)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = fileName.Split('_');
        if (parts.Length < minimumPartCount
            || !long.TryParse(
                parts[3],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.################", CultureInfo.InvariantCulture);
}
