using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelStatisticsEditorTests
{
    [Fact]
    public void Update_ReplacesLatestDayAndNormalizesSpeed()
    {
        var updated = NovelStatisticsEditor.Update(
            [
                Statistic("2026-08-14", 100, 60, 10),
                Statistic("2026-08-14", 200, 120, 20),
            ],
            "2026-08-14",
            "Book",
            900,
            450,
            30);

        updated.Should().ContainSingle();
        updated[0].CharactersRead.Should().Be(900);
        updated[0].ReadingTime.Should().Be(450);
        updated[0].LastReadingSpeed.Should().Be(7_200);
        updated[0].MinReadingSpeed.Should().Be(7_200);
        updated[0].LastStatisticModified.Should().Be(30);
    }

    [Fact]
    public void DeleteDay_WritesNewerTombstoneAndHidesIt()
    {
        var deleted = NovelStatisticsEditor.DeleteDay(
            [Statistic("2026-08-14", 900, 450, 20)],
            "2026-08-14",
            "Book",
            30);

        deleted.Should().ContainSingle();
        deleted[0].CharactersRead.Should().Be(0);
        deleted[0].ReadingTime.Should().Be(0);
        deleted[0].LastStatisticModified.Should().Be(30);
        NovelStatisticsEditor.Visible(deleted).Should().BeEmpty();
    }

    [Fact]
    public void DeleteAll_PreservesEveryDateAsADeletionTombstone()
    {
        var deleted = NovelStatisticsEditor.DeleteAll(
            [
                Statistic("2026-08-13", 400, 300, 10),
                Statistic("2026-08-14", 900, 450, 20),
            ],
            "Book",
            30);

        deleted.Should().HaveCount(2);
        deleted.Should().OnlyContain(item =>
            item.CharactersRead == 0
            && item.ReadingTime == 0
            && item.LastStatisticModified == 30);
    }

    private static NovelReadingStatistic Statistic(
        string date,
        int characters,
        double seconds,
        long modifiedAt) =>
        new("Book", date, characters, seconds, 0, 0, 0, 0, modifiedAt);
}

