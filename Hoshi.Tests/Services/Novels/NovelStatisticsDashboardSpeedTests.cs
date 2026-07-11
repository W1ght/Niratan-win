using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardSpeedTests
{
    [Fact]
    public void SpeedSummary_UsesOnlyValidSamplesAndRecentActiveDays()
    {
        var start = new DateOnly(2026, 1, 1);
        var days = Enumerable.Range(0, 8).Select(index => new NovelStatisticsDayAggregate(
            start.AddDays(index * 2),
            110,
            90,
            [
                new("valid", "Valid", null, 100, 60, true),
                new("short", "Short", null, 10, 30, false),
            ])).ToList();

        var result = NovelStatisticsDashboardCalculator.SpeedSummary(
            days,
            new(start, start.AddDays(30)));

        result.WeightedAveragePerHour.Should().Be(6_000);
        result.MedianActiveDayPerHour.Should().Be(6_000);
        result.LastSevenActiveDaysPerHour.Should().Be(6_000);
        result.ChangePercent.Should().BeNull();
        result.FastestDay!.Date.Should().Be(start);
        result.SlowestDay!.Date.Should().Be(start);
    }
}
