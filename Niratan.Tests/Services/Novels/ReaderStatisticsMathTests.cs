using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderStatisticsMathTests
{
    [Fact]
    public void Empty_UsesTheRequestedLocalDate()
    {
        var result = ReaderStatisticsMath.Empty("Book", new DateOnly(2026, 7, 11));

        result.Title.Should().Be("Book");
        result.DateKey.Should().Be("2026-07-11");
        result.CharactersRead.Should().Be(0);
        result.ReadingTime.Should().Be(0);
    }

    [Fact]
    public void Update_UsesTtuFormulaAndClampsNegativeMovement()
    {
        var source = ReaderStatisticsMath.Empty("Book", new DateOnly(2026, 7, 11));

        var first = ReaderStatisticsMath.Update(source, 120, 60, 42);
        var second = ReaderStatisticsMath.Update(first, 1, -1_000, 43);

        first.LastReadingSpeed.Should().Be(1_800);
        first.MinReadingSpeed.Should().Be(1_800);
        first.AltMinReadingSpeed.Should().Be(1_800);
        first.MaxReadingSpeed.Should().Be(1_800);
        first.LastStatisticModified.Should().Be(42);
        second.CharactersRead.Should().Be(0);
    }

    [Fact]
    public void Update_DoesNotChangeAltMinimumForZeroCharacterCheckpoint()
    {
        var source = ReaderStatisticsMath.Empty("Book", new DateOnly(2026, 7, 11)) with
        {
            CharactersRead = 60,
            ReadingTime = 120,
            MinReadingSpeed = 1_800,
            AltMinReadingSpeed = 1_800,
            LastReadingSpeed = 1_800,
            MaxReadingSpeed = 1_800,
        };

        var result = ReaderStatisticsMath.Update(source, 120, 0, 42);

        result.LastReadingSpeed.Should().Be(900);
        result.MinReadingSpeed.Should().Be(900);
        result.AltMinReadingSpeed.Should().Be(1_800);
    }

    [Fact]
    public void Deduplicate_KeepsNewestModificationForEachDateKey()
    {
        var result = ReaderStatisticsMath.Deduplicate([
            Statistic("2026-07-11", characters: 1, modified: 10),
            Statistic("2026-07-11", characters: 2, modified: 20),
            Statistic("2026-07-10", characters: 3, modified: 30),
        ]);

        result.Select(item => item.DateKey).Should().Equal("2026-07-10", "2026-07-11");
        result[1].CharactersRead.Should().Be(2);
    }

    [Fact]
    public void Aggregate_DerivesAllTimeSpeedFromTotals()
    {
        var result = ReaderStatisticsMath.Aggregate(
            "Book",
            new DateOnly(2026, 7, 11),
            [
                Statistic("2026-07-10", characters: 100, readingTime: 100, modified: 10),
                Statistic("2026-07-11", characters: 200, readingTime: 200, modified: 20),
            ]);

        result.CharactersRead.Should().Be(300);
        result.ReadingTime.Should().Be(300);
        result.LastReadingSpeed.Should().Be(3_600);
        result.LastStatisticModified.Should().Be(20);
    }

    private static NovelReadingStatistic Statistic(
        string dateKey,
        int characters,
        double readingTime = 1,
        long modified = 1) =>
        new(
            "Book",
            dateKey,
            characters,
            readingTime,
            MinReadingSpeed: 0,
            AltMinReadingSpeed: 0,
            LastReadingSpeed: 0,
            MaxReadingSpeed: 0,
            LastStatisticModified: modified);
}
