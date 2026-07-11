using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardSummaryTests
{
    [Fact]
    public void TargetsAndRanges_MatchNiratanSnappingAndClipping()
    {
        NovelStatisticsDashboardTargets.SnapCharacterTarget(749).Should().Be(500);
        NovelStatisticsDashboardTargets.SnapCharacterTarget(750).Should().Be(1_000);
        NovelStatisticsDashboardTargets.SnapDurationTarget(33).Should().Be(35);
        NovelStatisticsDashboardTargets.SnapWeeklyTargetDays(9).Should().Be(7);

        var window = new NovelStatisticsDateRange(new(2025, 7, 12), new(2026, 7, 11));
        NovelStatisticsDashboardCalculator.SelectedRange(
            NovelStatisticsRangeMode.Month, new(2025, 7, 20), window)
            .Should().Be(new NovelStatisticsDateRange(new(2025, 7, 12), new(2025, 7, 31)));
    }

    [Fact]
    public void WeekSummary_ExposesFullWeekButExcludesFuturePercentages()
    {
        var today = new DateOnly(2026, 7, 8);
        var settings = new NovelStatisticsDashboardTargetSettings(
            StatisticsDailyTargetType.Characters, 100, 30, 2);
        var snapshot = new NovelStatisticsDashboardSnapshot(
            [Day(new(2026, 7, 6), 100), Day(new(2026, 7, 7), 200)], []);

        var result = NovelStatisticsDashboardCalculator.WeekSummary(snapshot, today, settings);

        result.ElapsedDays.Should().Be(3);
        result.Days.Should().HaveCount(7);
        result.Days.Where(day => day.IsFuture).Should().OnlyContain(day => day.Percent == null);
        result.MetTargetDays.Should().Be(2);
        result.WeeklyStreakWeeks.Should().Be(1);
    }

    private static NovelStatisticsDayAggregate Day(DateOnly date, int characters) =>
        new(date, characters, 60, [new("book", "Book", null, characters, 60, true)]);
}
