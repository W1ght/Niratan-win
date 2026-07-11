using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Moq;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardServiceTests
{
    [Fact]
    public void Calculator_BuildsMacAlignedDashboardSummaries()
    {
        var today = new DateOnly(2026, 7, 1);
        var settings = new NovelStatisticsDashboardTargetSettings(
            StatisticsDailyTargetType.Characters,
            DailyCharacterTarget: 5_000,
            DailyDurationTargetMinutes: 30,
            WeeklyTargetDays: 2);
        var days = new[]
        {
            Day(new DateOnly(2026, 6, 24), 4_000, 1_800, Book("alpha", "Alpha", 4_000, 1_800)),
            Day(new DateOnly(2026, 6, 25), 5_200, 2_600, Book("alpha", "Alpha", 5_200, 2_600)),
            Day(new DateOnly(2026, 6, 26), 5_100, 2_400, Book("alpha", "Alpha", 5_100, 2_400)),
            Day(new DateOnly(2026, 6, 30), 6_000, 2_700, Book("alpha", "Alpha", 4_000, 1_800), Book("beta", "Beta", 2_000, 900)),
            Day(today, 3_500, 1_800, Book("beta", "Beta", 3_500, 1_800)),
        };
        var snapshot = new NovelStatisticsDashboardSnapshot(days, []);

        var todaySummary = NovelStatisticsDashboardCalculator.TodaySummary(snapshot, today, settings);
        todaySummary.Characters.Should().Be(3_500);
        todaySummary.ReadingTime.Should().Be(1_800);
        todaySummary.TargetPercent.Should().Be(70);
        todaySummary.DailyStreakDays.Should().Be(1);

        var weekSummary = NovelStatisticsDashboardCalculator.WeekSummary(snapshot, today, settings);
        weekSummary.Range.Start.Should().Be(new DateOnly(2026, 6, 29));
        weekSummary.Range.End.Should().Be(new DateOnly(2026, 7, 5));
        weekSummary.Characters.Should().Be(9_500);
        weekSummary.MetTargetDays.Should().Be(1);
        weekSummary.AverageSpeedPerHour.Should().Be(7_600);

        var range = new NovelStatisticsDateRange(new DateOnly(2026, 6, 24), today);
        var rangeSummary = NovelStatisticsDashboardCalculator.RangeSummary(days, range, settings);
        rangeSummary.Characters.Should().Be(23_800);
        rangeSummary.ReadingTime.Should().Be(11_300);
        rangeSummary.AverageSpeedPerHour.Should().Be(7_582);
        rangeSummary.TargetDays.Should().Be(3);

        var distribution = NovelStatisticsDashboardCalculator.DistributionRows(
            days,
            range,
            StatisticsDailyTargetType.Characters);
        distribution.Select(row => row.Title).Should().Equal("Alpha", "Beta");
        distribution.Select(row => row.Percent).Should().Equal(77, 23);
    }

    [Fact]
    public async Task Service_LoadsSnapshotAcrossBooksAndKeepsCurrentBookMetadata()
    {
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        var alpha = new NovelBook
        {
            Id = "alpha",
            Title = "Alpha Current",
            CoverPath = "D:\\Books\\alpha\\cover.jpg",
            ExtractedPath = "D:\\Books\\alpha",
        };
        var beta = new NovelBook
        {
            Id = "beta",
            Title = "Beta",
            ExtractedPath = "D:\\Books\\beta",
        };
        sidecars
            .Setup(s => s.LoadAsync(alpha.ExtractedPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NovelReadingStatistic("Ignored", "2026-07-01", 1_200, 600, 0, 0, 7_200, 7_200, 1),
            ]);
        sidecars
            .Setup(s => s.LoadAsync(beta.ExtractedPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NovelReadingStatistic("Beta", "2026-07-01", 800, 300, 0, 0, 9_600, 9_600, 1),
            ]);
        var service = new NovelStatisticsDashboardService(sidecars.Object);

        var snapshot = await service.LoadSnapshotAsync([alpha, beta], TestContext.Current.CancellationToken);

        snapshot.Days.Should().ContainSingle();
        var day = snapshot.Days[0];
        day.Characters.Should().Be(2_000);
        day.BookContributions.Select(contribution => contribution.Title)
            .Should().Equal("Alpha Current", "Beta");
        day.BookContributions[0].CoverPath.Should().Be(alpha.CoverPath);
    }

    private static NovelStatisticsDayAggregate Day(
        DateOnly date,
        int characters,
        double readingTime,
        params NovelStatisticsBookContribution[] contributions) =>
        new(date, characters, readingTime, contributions);

    private static NovelStatisticsBookContribution Book(
        string id,
        string title,
        int characters,
        double readingTime) =>
        new(id, title, CoverPath: null, characters, readingTime);
}
