using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;
using Moq;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderStatisticsSessionTests
{
    [Fact]
    public async Task LoadStartAndCheckpoint_ProjectsAndSavesOneCanonicalHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(8)));
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);

        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(100), ct);
        session.Start(new ReaderStatisticsPosition(100));
        clock.Advance(TimeSpan.FromMinutes(2));
        await session.CheckpointAsync(
            new ReaderStatisticsPosition(160),
            ReaderStatisticsCheckpointReason.ReadingMovement,
            ct);

        session.State.IsTracking.Should().BeTrue();
        session.State.IsPaused.Should().BeFalse();
        session.State.Session.CharactersRead.Should().Be(60);
        session.State.Session.ReadingTime.Should().Be(120);
        session.State.Session.LastReadingSpeed.Should().Be(1_800);
        session.State.Today.CharactersRead.Should().Be(60);
        session.State.AllTime.CharactersRead.Should().Be(60);
        session.State.History.Should().ContainSingle(x => x.DateKey == "2026-07-11");
        sidecars.Verify(x => x.SaveAsync(
            "root",
            It.Is<IReadOnlyList<NovelReadingStatistic>>(items =>
                items.Count == 1 && items[0].CharactersRead == 60),
            ct), Times.Once);
    }

    [Fact]
    public async Task Tick_UpdatesProjectionWithoutWriting_AndCheckpointSavesAccumulatedState()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(8)));
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);
        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(10), ct);
        session.Start(new ReaderStatisticsPosition(10));

        clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick(new ReaderStatisticsPosition(15));

        session.State.Session.CharactersRead.Should().Be(5);
        session.State.Session.ReadingTime.Should().Be(1);
        sidecars.Verify(x => x.SaveAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<NovelReadingStatistic>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        clock.Advance(TimeSpan.FromSeconds(1));
        await session.CheckpointAsync(
            new ReaderStatisticsPosition(20),
            ReaderStatisticsCheckpointReason.ReadingMovement,
            ct);

        session.State.Session.CharactersRead.Should().Be(10);
        session.State.Session.ReadingTime.Should().Be(2);
        sidecars.Verify(x => x.SaveAsync(
            "root",
            It.IsAny<IReadOnlyList<NovelReadingStatistic>>(),
            ct), Times.Once);
    }

    [Fact]
    public async Task PauseAndStop_DoNotAccumulatePausedTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(8)));
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);
        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(0), ct);
        session.Start(new ReaderStatisticsPosition(0));
        clock.Advance(TimeSpan.FromSeconds(10));

        await session.PauseAsync(new ReaderStatisticsPosition(10), ct);
        clock.Advance(TimeSpan.FromMinutes(5));
        session.Tick(new ReaderStatisticsPosition(20));

        session.State.IsTracking.Should().BeTrue();
        session.State.IsPaused.Should().BeTrue();
        session.State.Session.ReadingTime.Should().Be(10);

        session.Start(new ReaderStatisticsPosition(20));
        clock.Advance(TimeSpan.FromSeconds(5));
        await session.StopAsync(new ReaderStatisticsPosition(25), ct);

        session.State.IsTracking.Should().BeFalse();
        session.State.IsPaused.Should().BeFalse();
        session.State.Session.ReadingTime.Should().Be(15);
        sidecars.Verify(x => x.SaveAsync(
            "root",
            It.IsAny<IReadOnlyList<NovelReadingStatistic>>(),
            ct), Times.Exactly(2));
    }

    [Fact]
    public async Task LocalMidnight_ArchivesPriorDayAndAppliesCheckpointToCurrentDay()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 23, 59, 59, TimeSpan.FromHours(8)));
        var prior = Statistic("2026-07-11", characters: 100, readingTime: 100, modified: 10);
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([prior]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);
        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(100), ct);
        session.Start(new ReaderStatisticsPosition(100));

        clock.Advance(TimeSpan.FromSeconds(2));
        await session.CheckpointAsync(
            new ReaderStatisticsPosition(120),
            ReaderStatisticsCheckpointReason.ReadingMovement,
            ct);

        session.State.Today.DateKey.Should().Be("2026-07-12");
        session.State.Today.CharactersRead.Should().Be(20);
        session.State.Today.ReadingTime.Should().Be(2);
        session.State.History.Select(x => x.DateKey).Should().Equal("2026-07-11", "2026-07-12");
        session.State.History[0].Should().Be(prior);
        session.State.AllTime.CharactersRead.Should().Be(120);
    }

    [Fact]
    public async Task NegativeMovement_CannotReduceSessionBelowZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(8)));
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);
        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(100), ct);
        session.Start(new ReaderStatisticsPosition(100));
        clock.Advance(TimeSpan.FromSeconds(1));
        session.Tick(new ReaderStatisticsPosition(110));
        clock.Advance(TimeSpan.FromSeconds(1));

        await session.CheckpointAsync(
            new ReaderStatisticsPosition(0),
            ReaderStatisticsCheckpointReason.ProgrammaticDeparture,
            ct);

        session.State.Session.CharactersRead.Should().Be(0);
        session.State.Today.CharactersRead.Should().Be(0);
    }

    [Fact]
    public async Task Reload_DeduplicatesHistoryAndRestoresTodayAndAllTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = ClockAtLocal(new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.FromHours(8)));
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(x => x.LoadAsync("root", ct)).ReturnsAsync([
            Statistic("2026-07-10", 30, 30, 10),
            Statistic("2026-07-11", 40, 40, 20),
            Statistic("2026-07-11", 50, 50, 30),
        ]);
        var session = new ReaderStatisticsSession(sidecars.Object, clock);

        await session.LoadAsync("root", "Book", new ReaderStatisticsPosition(500), ct);

        session.State.Today.CharactersRead.Should().Be(50);
        session.State.AllTime.CharactersRead.Should().Be(80);
        session.State.History.Should().HaveCount(2);
        session.State.Session.CharactersRead.Should().Be(0);
        session.State.IsTracking.Should().BeFalse();
    }

    private static ManualTimeProvider ClockAtLocal(DateTimeOffset localNow) =>
        new(localNow.ToUniversalTime(), TimeZoneInfo.CreateCustomTimeZone(
            "Test +08",
            TimeSpan.FromHours(8),
            "Test +08",
            "Test +08"));

    private static NovelReadingStatistic Statistic(
        string dateKey,
        int characters,
        double readingTime,
        long modified) =>
        new("Book", dateKey, characters, readingTime, 0, 0, 0, 0, modified);

    private sealed class ManualTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
