using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardTrendTests
{
    [Fact]
    public void TrendAndCalendar_FillInteriorDaysAndPreserveContributors()
    {
        var start = new DateOnly(2026, 7, 1);
        var days = new[] { Day(start, "A"), Day(start.AddDays(2), "B") };

        var trend = NovelStatisticsDashboardCalculator.TrendPoints(
            NovelStatisticsTrendGrain.Day,
            new(start, start.AddDays(5)),
            days);

        trend.Should().HaveCount(3);
        trend[1].Characters.Should().Be(0);
        trend[0].TopBooks.Should().ContainSingle().Which.Title.Should().Be("A");

        var snapshot = new NovelStatisticsDashboardSnapshot(
            start, start.AddDays(2), days, [], []);
        var calendar = NovelStatisticsDashboardCalculator.CalendarDays(
            snapshot,
            start.AddDays(2),
            new(StatisticsDailyTargetType.Characters, 100, 30, 4));
        calendar.Should().HaveCount(3);
        calendar[2].IsToday.Should().BeTrue();
    }

    private static NovelStatisticsDayAggregate Day(DateOnly date, string title) =>
        new(date, 100, 60, [new(title, title, null, 100, 60, true)]);
}
