using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardRepositoryTests
{
    [Fact]
    public async Task LoadSnapshot_KeepsTotalsButMarksShortBurstsInvalidForSpeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var statistics = new NovelStatisticsSidecarService();
        var bookInfo = new NovelBookSidecarService();
        await statistics.SaveAsync(temp.Path, [Statistic(200, 30)], ct);
        await bookInfo.SaveBookInfoAsync(temp.Path, new NovelBookInfo(12_345, []), ct);
        var service = new NovelStatisticsDashboardService(
            statistics,
            bookInfo,
            new FixedTimeProvider());

        var snapshot = await service.LoadSnapshotAsync([Book(temp.Path)], ct);

        snapshot.WindowStart.Should().Be(new DateOnly(2025, 7, 12));
        snapshot.WindowEnd.Should().Be(new DateOnly(2026, 7, 11));
        snapshot.Days.Should().ContainSingle().Which.Characters.Should().Be(200);
        snapshot.Days.Single().BookContributions.Should().ContainSingle()
            .Which.IsValidSpeedSample.Should().BeFalse();
        snapshot.Books.Should().ContainSingle()
            .Which.TotalCharacterCount.Should().Be(12_345);
    }

    [Fact]
    public async Task LoadSnapshot_ReportsCorruptStatisticsWithoutOverwritingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, NovelStatisticsSidecarService.StatisticsFileName);
        await File.WriteAllTextAsync(path, "{ broken", ct);
        var before = await File.ReadAllBytesAsync(path, ct);
        var service = new NovelStatisticsDashboardService(
            new NovelStatisticsSidecarService(),
            new NovelBookSidecarService(),
            new FixedTimeProvider());

        var snapshot = await service.LoadSnapshotAsync([Book(temp.Path)], ct);

        snapshot.SkippedCorruptBookIds.Should().Equal("book-1");
        (await File.ReadAllBytesAsync(path, ct)).Should().Equal(before);
    }

    private static NovelBook Book(string path) => new()
    {
        Id = "book-1",
        Title = "Current Title",
        ExtractedPath = path,
    };

    private static NovelReadingStatistic Statistic(int characters, double seconds) =>
        new("Old Title", "2026-07-11", characters, seconds, 0, 0, 0, 0, 1);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
