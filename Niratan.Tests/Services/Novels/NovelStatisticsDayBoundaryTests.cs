using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelStatisticsDayBoundaryTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.CreateCustomTimeZone(
        "Test +08",
        TimeSpan.FromHours(8),
        "Test +08",
        "Test +08");

    [Fact]
    public void ReportingDate_UsesPreviousDayBeforeResetAndCurrentDayAtReset()
    {
        var beforeReset = new DateTimeOffset(2026, 8, 10, 3, 59, 0, TimeSpan.FromHours(8));
        var atReset = new DateTimeOffset(2026, 8, 10, 4, 0, 0, TimeSpan.FromHours(8));

        NovelStatisticsDayBoundary.ReportingDate(
                beforeReset,
                4 * 60,
                Shanghai)
            .Should().Be(new DateOnly(2026, 8, 9));
        NovelStatisticsDayBoundary.ReportingDate(
                atReset,
                4 * 60,
                Shanghai)
            .Should().Be(new DateOnly(2026, 8, 10));
        NovelStatisticsDayBoundary.ReportingDate(
                beforeReset,
                0,
                Shanghai)
            .Should().Be(new DateOnly(2026, 8, 10));
    }

    [Theory]
    [InlineData(-30, 0)]
    [InlineData(0, 0)]
    [InlineData(275, 275)]
    [InlineData(2000, 1439)]
    public void NormalizeResetMinutes_ClampsToOneLocalDay(int value, int expected) =>
        NovelStatisticsDayBoundary.NormalizeResetMinutes(value).Should().Be(expected);

    [Fact]
    public void ReportingDate_RemainsLocalAcrossDaylightSavingTransition()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var utc = new DateTimeOffset(2026, 3, 8, 10, 30, 0, TimeSpan.Zero);

        NovelStatisticsDayBoundary.ReportingDate(utc, 4 * 60, pacific)
            .Should().Be(new DateOnly(2026, 3, 7));
    }
}
