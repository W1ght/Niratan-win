using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardComparisonTests
{
    [Fact]
    public void RankingAndShelves_UseValidSpeedAndKnownBookIds()
    {
        var date = new DateOnly(2026, 7, 11);
        var contributions = Enumerable.Range(1, 14).Select(index =>
            new NovelStatisticsBookContribution(
                $"b{index}", $"Book {index:00}", null, index * 100, index == 1 ? 30 : 60, index != 1)).ToList();
        var day = new NovelStatisticsDayAggregate(
            date, contributions.Sum(item => item.Characters), contributions.Sum(item => item.ReadingTime), contributions);
        var books = contributions.Select(item =>
            new NovelStatisticsBookRecord(item.BookId, item.Title, null, 1_000)).ToList();
        var snapshot = new NovelStatisticsDashboardSnapshot(date, date, [day], books, []);

        NovelStatisticsDashboardCalculator.BookRankingRows(
            [day], new(date, date), NovelStatisticsBookRankingMetric.Characters)
            .Should().HaveCount(12);

        var shelves = NovelStatisticsDashboardCalculator.ShelfComparisonRows(
            snapshot,
            new NovelShelfState([new NovelShelf("Shelf", ["b1", "b2", "missing"])], []),
            new(date, date));

        shelves.Should().Contain(row => row.Name == "Shelf" && row.BookCount == 2);
        shelves.Should().Contain(row => row.Id == "unshelved" && row.BookCount == 12);
        shelves.Single(row => row.Name == "Shelf").AverageSpeedPerHour.Should().NotBeNull();
    }
}
