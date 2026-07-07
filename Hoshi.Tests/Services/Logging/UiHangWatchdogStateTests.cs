using FluentAssertions;
using Hoshi.Services.Logging;

namespace Hoshi.Tests.Services.Logging;

public class UiHangWatchdogStateTests
{
    [Fact]
    public void ShouldReportHang_WaitsForFirstUiTickBeforeReporting()
    {
        var state = new UiHangWatchdogState(initialTimestampMs: 1000);

        state.ShouldReportHang(nowMs: 60_000, thresholdMs: 4_000)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldReportHang_ReportsOnlyAfterUiTickExceedsThreshold()
    {
        var state = new UiHangWatchdogState(initialTimestampMs: 1000);
        state.RecordUiTick(timestampMs: 2_000);

        state.ShouldReportHang(nowMs: 5_500, thresholdMs: 4_000)
            .Should()
            .BeFalse();
        state.ShouldReportHang(nowMs: 6_500, thresholdMs: 4_000)
            .Should()
            .BeTrue();
        state.ElapsedSinceLastUiTickMs(nowMs: 6_500)
            .Should()
            .Be(4_500);
    }

    [Fact]
    public void RecordUiTick_ResetsHangElapsedTime()
    {
        var state = new UiHangWatchdogState(initialTimestampMs: 1000);
        state.RecordUiTick(timestampMs: 2_000);
        state.ShouldReportHang(nowMs: 7_000, thresholdMs: 4_000)
            .Should()
            .BeTrue();

        state.RecordUiTick(timestampMs: 7_100);

        state.ShouldReportHang(nowMs: 7_200, thresholdMs: 4_000)
            .Should()
            .BeFalse();
        state.ElapsedSinceLastUiTickMs(nowMs: 7_200)
            .Should()
            .Be(100);
    }
}
