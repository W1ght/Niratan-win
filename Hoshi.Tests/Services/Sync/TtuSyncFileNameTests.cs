using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Models.Sync;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class TtuSyncFileNameTests
{
    [Fact]
    public void ProgressFileName_UsesTtuPrefixVersionTimestampAndProgress()
    {
        var modified = new DateTimeOffset(2026, 7, 8, 12, 34, 56, 789, TimeSpan.Zero);
        var progress = new TtuProgress(
            DataId: 7,
            ExploredCharCount: 1234,
            Progress: 0.617,
            LastBookmarkModified: modified);

        var fileName = TtuSyncFileNames.GetProgressFileName(progress);

        fileName.Should().Be("progress_1_6_1783514096789_0.617.json");
        TtuSyncFileNames.ParseProgressTimestamp(fileName).Should().Be(modified);
    }

    [Fact]
    public void AudioBookFileName_UsesTtuPrefixVersionTimestampAndPlaybackPosition()
    {
        var audioBook = new TtuAudioBook(
            Title: "星を読む",
            PlaybackPosition: 42.5,
            LastAudioBookModified: 1783514096789);

        var fileName = TtuSyncFileNames.GetAudioBookFileName(audioBook);

        fileName.Should().Be("audioBook_1_6_1783514096789_42.5.json");
        TtuSyncFileNames.ParseAudioBookTimestamp(fileName)
            .Should()
            .Be(DateTimeOffset.FromUnixTimeMilliseconds(1783514096789));
    }

    [Fact]
    public void StatisticsFileName_MatchesTtuAggregateShape()
    {
        var stats = new[]
        {
            Statistic("2026-07-07", charactersRead: 100, readingTime: 50, minSpeed: 100, altMinSpeed: 90, lastSpeed: 120, maxSpeed: 140, modified: 1000),
            Statistic("2026-07-08", charactersRead: 300, readingTime: 150, minSpeed: 80, altMinSpeed: 70, lastSpeed: 200, maxSpeed: 220, modified: 2000),
        };

        var fileName = TtuSyncFileNames.GetStatisticsFileName(stats);

        fileName.Should().Be("statistics_1_6_2000_400_200_80_70_7200_220_100_125_200_250_7200_7200_na.json");
    }

    [Theory]
    [InlineData("星/本* ", "星%2F本~ttu-star~~ttu-spc~")]
    [InlineData("title.", "title~ttu-dend~")]
    [InlineData("a?b<c>d\\e:f|g%h\"i", "a%3Fb%3Cc%3Ed%5Ce%3Af%7Cg%25h%22i")]
    public void SanitizeTtuFilename_MatchesTtuEscapes(string title, string expected)
    {
        TtuSyncFileNames.SanitizeTtuFilename(title).Should().Be(expected);
        TtuSyncFileNames.DesanitizeTtuFilename(expected).Should().Be(title);
    }

    private static NovelReadingStatistic Statistic(
        string dateKey,
        int charactersRead,
        double readingTime,
        int minSpeed,
        int altMinSpeed,
        int lastSpeed,
        int maxSpeed,
        long modified) =>
        new(
            Title: "Book",
            DateKey: dateKey,
            CharactersRead: charactersRead,
            ReadingTime: readingTime,
            MinReadingSpeed: minSpeed,
            AltMinReadingSpeed: altMinSpeed,
            LastReadingSpeed: lastSpeed,
            MaxReadingSpeed: maxSpeed,
            LastStatisticModified: modified);
}
