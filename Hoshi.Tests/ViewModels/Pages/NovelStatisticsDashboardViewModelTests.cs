using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Settings;
using Hoshi.ViewModels.Pages;
using Moq;

namespace Hoshi.Tests.ViewModels.Pages;

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
    public async Task CalendarSelection_UpdatesAnchorAndSelectedDetail()
    {
        var sut = CreateSut(out _, out _);
        await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);

        sut.SelectedCalendarDay = sut.CalendarDays.Single(day => day.Characters == 1_200);

        DateOnly.FromDateTime(sut.AnchorDate!.Value.LocalDateTime)
            .Should().Be(sut.SelectedCalendarDay.Date);
        sut.CalendarDetail.Characters.Should().Be(1_200);
        sut.CalendarDetail.ActiveBookCount.Should().Be(1);
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
}
