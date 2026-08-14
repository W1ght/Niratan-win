using System;
using System.Globalization;

namespace Niratan.Services.Novels;

public static class NovelStatisticsDayBoundary
{
    public const int MinutesPerDay = 24 * 60;

    public static int NormalizeResetMinutes(int resetMinutes) =>
        Math.Clamp(resetMinutes, 0, MinutesPerDay - 1);

    public static DateOnly ReportingDate(
        DateTimeOffset timestamp,
        int resetMinutes,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(timestamp, timeZone);
        var reportingDate = DateOnly.FromDateTime(local.DateTime);
        var minuteOfDay = local.Hour * 60 + local.Minute;
        return minuteOfDay < NormalizeResetMinutes(resetMinutes)
            ? reportingDate.AddDays(-1)
            : reportingDate;
    }

    public static string DateKey(
        DateTimeOffset timestamp,
        int resetMinutes,
        TimeZoneInfo timeZone) =>
        ReportingDate(timestamp, resetMinutes, timeZone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
