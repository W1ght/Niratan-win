using System.Linq.Expressions;
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Models.Settings;
using Niratan.Services.Novels;
using Niratan.Services.Settings;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Moq;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class NovelStatisticsDashboardViewModelTests
{
    private static readonly DateOnly Today = new(2026, 7, 11);

    [Fact]
    public async Task ActivateAsync_ProjectsAllReferenceModules()
    {
        var sut = CreateSut(out _, out _);

        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.HasData.Should().BeTrue();
        sut.Today.Should().NotBeNull();
        sut.TodayMetrics.Should().HaveCount(4);
        sut.WeekDays.Should().HaveCount(7);
        sut.WeekMetrics.Should().HaveCount(4);
        sut.SelectedRange.Should().NotBeNull();
        sut.RangeMetrics.Should().HaveCount(4);
        sut.SpeedMetrics.Should().HaveCount(6);
        sut.TrendPoints.Should().NotBeEmpty();
        sut.CalendarDays.Should().HaveCount(365);
        sut.BookRankingRows.Should().HaveCountLessThanOrEqualTo(12);
        sut.ShelfComparisonRows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TrendStyleChange_DoesNotRecalculateRangeSummary()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        var before = sut.SelectedRange;

        sut.SelectedTrendStyle = NovelStatisticsTrendChartStyle.Line;

        sut.SelectedRange.Should().BeSameAs(before);
        sut.SelectedTrendStyle.Should().Be(NovelStatisticsTrendChartStyle.Line);
    }

    [Theory]
    [InlineData(NovelStatisticsTrendMetric.Characters, "chars")]
    [InlineData(NovelStatisticsTrendMetric.Duration, "m")]
    [InlineData(NovelStatisticsTrendMetric.Speed, "/ h")]
    public async Task TrendMetric_ProjectsNormalizedValuesAndUnits(
        NovelStatisticsTrendMetric metric,
        string unit)
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SelectedTrendMetric = metric;

        sut.TrendPoints.Max(point => point.NormalizedValue).Should().Be(1);
        sut.TrendPoints.Should().OnlyContain(
            point => point.NormalizedValue >= 0 && point.NormalizedValue <= 1);
        sut.TrendPoints.Should().Contain(point => point.ValueText.Contains(unit));
    }

    [Theory]
    [InlineData(NovelStatisticsTrendMetric.Characters, "chars")]
    [InlineData(NovelStatisticsTrendMetric.Duration, "m")]
    [InlineData(NovelStatisticsTrendMetric.Speed, "/ h")]
    public async Task TrendAxis_ExposesFiveMetricSpecificTicks(
        NovelStatisticsTrendMetric metric,
        string expectedUnit)
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SelectedTrendMetric = metric;

        sut.TrendAxisTicks.Should().HaveCount(5);
        sut.TrendAxisTicks.Select(tick => tick.NormalizedValue)
            .Should().BeInAscendingOrder();
        sut.TrendAxisTicks[0].NormalizedValue.Should().Be(0);
        sut.TrendAxisTicks[^1].NormalizedValue.Should().Be(1);
        sut.TrendAxisTicks.Should().Contain(
            tick => tick.Label.Contains(expectedUnit));
    }

    [Fact]
    public async Task SpeedCard_ExposesSixLocalizedNiratanMetrics()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SpeedMetrics.Should().HaveCount(6);
        sut.SpeedMetrics.Select(metric => metric.Label)
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(label => !string.IsNullOrWhiteSpace(label));
    }

    [Fact]
    public async Task Calendar_ProjectsRecentYearHeatAndSelectedRange()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.CalendarDays.Should().HaveCount(365);
        sut.CalendarDays.Should().OnlyContain(
            day => day.HeatOpacity >= 0.08 && day.HeatOpacity <= 1);
        sut.CalendarDays.Should().Contain(day => day.IsInSelectedRange);
        sut.CalendarDays.Single(
                day => day.Characters == sut.CalendarDays.Max(value => value.Characters))
            .HeatOpacity.Should().Be(1);
    }

    [Fact]
    public async Task RankingAndShelves_NormalizeVisibleBars()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.BookRankingRows.Max(row => row.NormalizedValue).Should().Be(1);
        sut.ShelfComparisonRows.Max(row => row.NormalizedVolume).Should().Be(1);
        sut.ShelfComparisonRows.Should().OnlyContain(
            row => row.RecordedProgress >= 0 && row.RecordedProgress <= 1);
    }

    [Fact]
    public async Task BookRanking_RevealsTwelveMoreRowsAndResetsForMetricChanges()
    {
        var books = Enumerable.Range(1, 14)
            .Select(index => new NovelBook
            {
                Id = $"book-{index:00}",
                Title = $"Book {index:00}",
            })
            .ToArray();
        var contributions = books
            .Select((book, index) => new NovelStatisticsBookContribution(
                book.Id,
                book.Title,
                null,
                14_000 - index * 100,
                600 + index * 60,
                true))
            .ToArray();
        var snapshot = new NovelStatisticsDashboardSnapshot(
            Today.AddYears(-1).AddDays(1),
            Today,
            [new NovelStatisticsDayAggregate(
                Today,
                contributions.Sum(item => item.Characters),
                contributions.Sum(item => item.ReadingTime),
                contributions)],
            books.Select(book => new NovelStatisticsBookRecord(
                book.Id,
                book.Title,
                book.CoverPath,
                20_000)).ToArray(),
            []);
        var service = new RecordingDashboardService(snapshot);
        var settings = CreateSettings();
        var sut = new NovelStatisticsDashboardViewModel(
            service,
            settings.Object,
            new FixedTimeProvider());

        await sut.ActivateAsync(books, new NovelShelfState([], books.Select(book => book.Id).ToArray()), CancellationToken.None);

        sut.BookRankingRows.Should().HaveCount(12);
        sut.CanShowMoreBookRankings.Should().BeTrue();

        sut.ShowMoreBookRankings();

        sut.BookRankingRows.Should().HaveCount(14);
        sut.CanShowMoreBookRankings.Should().BeFalse();

        sut.SelectedRankingMetric = NovelStatisticsBookRankingMetric.Duration;

        sut.VisibleBookRankingLimit.Should().Be(12);
        sut.BookRankingRows.Should().HaveCount(12);
        sut.CanShowMoreBookRankings.Should().BeTrue();
    }

    [Fact]
    public async Task BookRanking_ReusesCoverResolutionAcrossProjectionRecalculations()
    {
        var coverLoadCount = 0;
        var coverCache = new NovelStatisticsBookCoverCache(_ =>
        {
            coverLoadCount++;
            return null;
        });
        var books = Books().ToArray();
        books[0].CoverPath = "D:\\Books\\a\\cover.jpg";
        var sut = new NovelStatisticsDashboardViewModel(
            new RecordingDashboardService(Snapshot()),
            CreateSettings().Object,
            null,
            new FixedTimeProvider(),
            coverCache);

        await sut.ActivateAsync(
            books,
            Shelves(),
            TestContext.Current.CancellationToken);

        coverLoadCount.Should().Be(1);

        sut.SelectedRankingMetric = NovelStatisticsBookRankingMetric.Duration;
        sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;

        coverLoadCount.Should().Be(1);
        sut.BookRankingRows.Single(row => row.Id == "a").HasNoCover.Should().BeTrue();
    }

    [Fact]
    public async Task BookDetail_LoadsEveryVisibleDayFromTheBookSidecar()
    {
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        var books = Books().ToArray();
        books[0].ExtractedPath = "D:\\Books\\a";
        sidecars.Setup(service => service.LoadWithStatusAsync(
                books[0].ExtractedPath!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NovelStatisticsSidecarLoadResult(
                NovelStatisticsSidecarLoadStatus.Loaded,
                [
                    new NovelReadingStatistic("A", "2026-07-10", 600, 300, 0, 0, 7_200, 7_200, 1),
                    new NovelReadingStatistic("A", "2026-07-11", 1_200, 600, 0, 0, 7_200, 7_200, 2),
                ]));
        var sut = new NovelStatisticsDashboardViewModel(
            new RecordingDashboardService(Snapshot()),
            CreateSettings().Object,
            sidecars.Object,
            new FixedTimeProvider());
        await sut.ActivateAsync(books, Shelves(), CancellationToken.None);

        var detail = await sut.LoadBookStatisticsAsync(
            "a",
            TestContext.Current.CancellationToken);

        detail.Should().NotBeNull();
        detail!.Days.Should().HaveCount(2);
        detail.TotalCharactersText.Should().Be("1,800");
        detail.TotalDurationText.Should().Be("15m");
        detail.AverageSpeedText.Should().Contain("7,200");
        detail.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task BookDetail_AverageSpeedExcludesDaysShorterThanOneMinute()
    {
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        var books = Books().ToArray();
        books[0].ExtractedPath = "D:\\Books\\a";
        sidecars.Setup(service => service.LoadWithStatusAsync(
                books[0].ExtractedPath!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NovelStatisticsSidecarLoadResult(
                NovelStatisticsSidecarLoadStatus.Loaded,
                [
                    new NovelReadingStatistic("A", "2026-07-10", 100, 30, 0, 0, 0, 0, 1),
                    new NovelReadingStatistic("A", "2026-07-11", 100, 30, 0, 0, 0, 0, 2),
                ]));
        var sut = new NovelStatisticsDashboardViewModel(
            new RecordingDashboardService(Snapshot()),
            CreateSettings().Object,
            sidecars.Object,
            new FixedTimeProvider());
        await sut.ActivateAsync(
            books,
            Shelves(),
            TestContext.Current.CancellationToken);

        var detail = await sut.LoadBookStatisticsAsync(
            "a",
            TestContext.Current.CancellationToken);

        detail.Should().NotBeNull();
        detail!.TotalCharactersText.Should().Be("200");
        detail.TotalDurationText.Should().Be("1m");
        detail.AverageSpeedText.Should().Be("— / h");
        detail.Days.Should().OnlyContain(day => day.SpeedText == "— / h");
    }

    [Fact]
    public async Task BookDetail_DeleteDay_UsesActiveReaderCoordinatorAndPersistsTombstone()
    {
        var books = Books().ToArray();
        books[0].ExtractedPath = "D:\\Books\\a";
        IReadOnlyList<NovelReadingStatistic> current =
        [
            new NovelReadingStatistic(
                "A", "2026-07-11", 1_200, 600, 0, 0, 7_200, 7_200, 10),
        ];
        var sidecars = new Mock<INovelStatisticsSidecarService>();
        sidecars.Setup(service => service.LoadWithStatusAsync(
                books[0].ExtractedPath!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NovelStatisticsSidecarLoadResult(
                NovelStatisticsSidecarLoadStatus.Loaded,
                current));
        sidecars.Setup(service => service.SaveAsync(
                books[0].ExtractedPath!,
                It.IsAny<IReadOnlyList<NovelReadingStatistic>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<NovelReadingStatistic>, CancellationToken>(
                (_, statistics, _) => current = statistics)
            .Returns(Task.CompletedTask);
        var coordinator = new Mock<INovelStatisticsMutationCoordinator>();
        coordinator.Setup(service => service.ExecuteAsync(
                "a",
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<CancellationToken, Task>, CancellationToken>(
                (_, mutation, ct) => mutation(ct));
        var sut = new NovelStatisticsDashboardViewModel(
            new RecordingDashboardService(Snapshot()),
            CreateSettings().Object,
            sidecars.Object,
            new FixedTimeProvider(),
            new NovelStatisticsBookCoverCache(),
            coordinator.Object);
        await sut.ActivateAsync(books, Shelves(), TestContext.Current.CancellationToken);

        var detail = await sut.DeleteBookStatisticsDayAsync(
            "a",
            "2026-07-11",
            TestContext.Current.CancellationToken);

        current.Should().ContainSingle();
        current[0].CharactersRead.Should().Be(0);
        current[0].ReadingTime.Should().Be(0);
        current[0].LastStatisticModified.Should().BeGreaterThan(10);
        detail.Should().NotBeNull();
        detail!.HasNoDays.Should().BeTrue();
        coordinator.Verify(service => service.ExecuteAsync(
            "a",
            It.IsAny<Func<CancellationToken, Task>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RangeScrollbar_DefaultsNewestAndMovesEveryProjection()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;

        sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum);
        sut.SelectedDateRange.Should().Be(
            new NovelStatisticsDateRange(Today, Today));
        sut.RangeText.Should().Contain("1,200");

        sut.SelectedRangeOffsetValue = sut.RangeScrollMaximum - 1;

        sut.SelectedDateRange.Should().Be(
            new NovelStatisticsDateRange(Today.AddDays(-1), Today.AddDays(-1)));
        sut.RangeText.Should().Contain("600");
        sut.TrendPoints.Should().ContainSingle();
        sut.BookRankingRows.Should().ContainSingle(row => row.Id == "b");
    }

    [Fact]
    public async Task RangeScrollbar_FractionalInputNotifiesRoundedValue()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;
        var notifications = new List<string?>();
        sut.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        sut.SelectedRangeOffsetValue = sut.RangeScrollMaximum - 0.2;

        sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum);
        notifications.Should().Contain(nameof(sut.SelectedRangeOffsetValue));
    }

    [Fact]
    public async Task CalendarSelection_MovesScrollbarToContainingPeriodAndUpdatesDetail()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;

        sut.SelectedCalendarDay = sut.CalendarDays.Single(day => day.Characters == 600);

        sut.SelectedDateRange.Start.Should().Be(Today.AddDays(-1));
        sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum - 1);
        sut.CalendarDetail.Characters.Should().Be(600);
        sut.CalendarDetail.ActiveBookCount.Should().Be(1);
    }

    [Fact]
    public async Task RangeModeChange_SelectsNewestPeriodAndKeepsCalendarSelectionInsideIt()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;
        sut.SelectedRangeOffsetValue = sut.RangeScrollMaximum - 20;

        sut.SelectedRangeMode = NovelStatisticsRangeMode.Week;

        sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum);
        sut.SelectedCalendarDay.Should().NotBeNull();
        sut.SelectedCalendarDay!.Date.Should().BeOnOrAfter(
            sut.SelectedDateRange.Start);
        sut.SelectedCalendarDay.Date.Should().BeOnOrBefore(
            sut.SelectedDateRange.End);
    }

    [Fact]
    public async Task TargetChange_SnapsPersistsAndRecalculates()
    {
        var sut = CreateSut(out _, out var settings);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.DailyCharacterTargetValue = 751;

        sut.DailyCharacterTarget.Should().Be(1_000);
        sut.Today!.TargetPercent.Should().Be(120);
        settings.Verify(service => service.Set(
            It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
            It.Is<NovelStatisticsSettings>(value => value.DailyCharacterTarget == 1_000)));
        settings.Verify(service => service.SaveAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DashboardToday_UsesTheConfiguredEarlyMorningResetBoundary()
    {
        var service = new RecordingDashboardService(Snapshot());
        var settings = CreateSettings();
        settings.Object.Current.StatisticsSettings.ResetTimeMinutes = 4 * 60;
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "View model +08",
            TimeSpan.FromHours(8),
            "View model +08",
            "View model +08");
        var clock = new ConfigurableTimeProvider(
            new DateTimeOffset(2026, 7, 11, 19, 30, 0, TimeSpan.Zero),
            timeZone);
        var sut = new NovelStatisticsDashboardViewModel(
            service,
            settings.Object,
            clock);

        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.Today!.Date.Should().Be(Today);
        sut.SelectedDateRange.End.Should().Be(Today);
    }

    [Fact]
    public async Task RefreshedSnapshot_ReplacesVisibleProjectionWhileActive()
    {
        var sut = CreateSut(out var service, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        var replacement = Snapshot() with
        {
            Days =
            [
                new NovelStatisticsDayAggregate(
                    Today,
                    3_000,
                    900,
                    [new NovelStatisticsBookContribution("a", "A", null, 3_000, 900, true)]),
            ],
        };

        service.Publish(replacement);

        sut.Today!.Characters.Should().Be(3_000);
    }

    [Fact]
    public async Task Deactivate_IgnoresLaterSnapshotRefresh()
    {
        var sut = CreateSut(out var service, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        sut.Deactivate();

        service.Publish(Snapshot() with
        {
            Days =
            [
                new NovelStatisticsDayAggregate(
                    Today,
                    9_000,
                    900,
                    [new NovelStatisticsBookContribution("a", "A", null, 9_000, 900, true)]),
            ],
        });

        sut.Today!.Characters.Should().Be(1_200);
        sut.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeactivateBeforeInitialLoad_IgnoresStaleCompletion()
    {
        var service = new ControlledDashboardService();
        var settings = CreateSettings();
        var sut = new NovelStatisticsDashboardViewModel(
            service,
            settings.Object,
            new FixedTimeProvider());

        var activation = sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        sut.Deactivate();
        service.CompleteNext(Snapshot());
        await activation;

        sut.HasData.Should().BeFalse();
        sut.IsLoading.Should().BeFalse();
        sut.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task NewerActivation_WinsWhenOlderLoadCompletesLater()
    {
        var service = new ControlledDashboardService();
        var settings = CreateSettings();
        var sut = new NovelStatisticsDashboardViewModel(
            service,
            settings.Object,
            new FixedTimeProvider());
        var replacement = Snapshot() with
        {
            Days =
            [
                new NovelStatisticsDayAggregate(
                    Today,
                    4_200,
                    600,
                    [new NovelStatisticsBookContribution("a", "A", null, 4_200, 600, true)]),
            ],
        };

        var first = sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        var second = sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
        service.CompleteAt(1, replacement);
        await second;
        service.CompleteAt(0, Snapshot());
        await first;

        sut.Today!.Characters.Should().Be(4_200);
        service.ActiveSubscriptions.Should().Be(1);
        sut.Deactivate();
        service.ActiveSubscriptions.Should().Be(0);
    }

    private static NovelStatisticsDashboardViewModel CreateSut(
        out RecordingDashboardService service,
        out Mock<ISettingsService> settings)
    {
        service = new RecordingDashboardService(Snapshot());
        settings = CreateSettings();
        return new NovelStatisticsDashboardViewModel(
            service,
            settings.Object,
            new FixedTimeProvider());
    }

    private static Mock<ISettingsService> CreateSettings()
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(value => value.Current).Returns(new AppSettings
        {
            StatisticsSettings = new NovelStatisticsSettings
            {
                EnableStatistics = true,
                DailyTargetType = StatisticsDailyTargetType.Characters,
                DailyCharacterTarget = 500,
                DailyDurationTargetMinutes = 30,
                WeeklyTargetDays = 4,
            },
        });
        settings.Setup(value => value.SaveAsync()).Returns(Task.CompletedTask);
        return settings;
    }

    private static IReadOnlyList<NovelBook> Books() =>
    [
        new NovelBook { Id = "a", Title = "A" },
        new NovelBook { Id = "b", Title = "B" },
    ];

    private static NovelShelfState Shelves() =>
        new([new NovelShelf("Favorites", ["a"])], ["b"]);

    private static NovelStatisticsDashboardSnapshot Snapshot() =>
        new(
            Today.AddYears(-1).AddDays(1),
            Today,
            [
                new NovelStatisticsDayAggregate(
                    Today.AddDays(-1),
                    600,
                    300,
                    [new NovelStatisticsBookContribution("b", "B", null, 600, 300, true)]),
                new NovelStatisticsDayAggregate(
                    Today,
                    1_200,
                    600,
                    [new NovelStatisticsBookContribution("a", "A", null, 1_200, 600, true)]),
            ],
            [
                new NovelStatisticsBookRecord("a", "A", null, 2_000),
                new NovelStatisticsBookRecord("b", "B", null, 1_500),
            ],
            []);

    private sealed class RecordingDashboardService(
        NovelStatisticsDashboardSnapshot snapshot) : INovelStatisticsDashboardService
    {
        public event EventHandler<NovelStatisticsDashboardSnapshot>? SnapshotRefreshed;

        public Task<NovelStatisticsDashboardSnapshot> LoadSnapshotAsync(
            IReadOnlyList<NovelBook> books,
            CancellationToken ct = default) => Task.FromResult(snapshot);

        public void Publish(NovelStatisticsDashboardSnapshot value) =>
            SnapshotRefreshed?.Invoke(this, value);
    }

    private sealed class ControlledDashboardService : INovelStatisticsDashboardService
    {
        private readonly List<TaskCompletionSource<NovelStatisticsDashboardSnapshot>> _loads = [];
        private EventHandler<NovelStatisticsDashboardSnapshot>? _snapshotRefreshed;

        public event EventHandler<NovelStatisticsDashboardSnapshot>? SnapshotRefreshed
        {
            add => _snapshotRefreshed += value;
            remove => _snapshotRefreshed -= value;
        }

        public int ActiveSubscriptions =>
            _snapshotRefreshed?.GetInvocationList().Length ?? 0;

        public Task<NovelStatisticsDashboardSnapshot> LoadSnapshotAsync(
            IReadOnlyList<NovelBook> books,
            CancellationToken ct = default)
        {
            var source = new TaskCompletionSource<NovelStatisticsDashboardSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _loads.Add(source);
            return source.Task.WaitAsync(ct);
        }

        public void CompleteNext(NovelStatisticsDashboardSnapshot snapshot) =>
            _loads.First(source => !source.Task.IsCompleted).SetResult(snapshot);

        public void CompleteAt(int index, NovelStatisticsDashboardSnapshot snapshot) =>
            _loads[index].SetResult(snapshot);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class ConfigurableTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone;
    }
}
