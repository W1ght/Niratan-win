using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSurfaceVolumeScrollTests
{
    [Fact]
    public void Adjustment_NormalizesWheelAndPreciseDeltasLikeNiratan()
    {
        VideoSurfaceVolumeScroll.GetAdjustment(0, 120, hasPreciseScrollingDeltas: false)
            .Should().Be(5);
        VideoSurfaceVolumeScroll.GetAdjustment(0, -120, hasPreciseScrollingDeltas: false)
            .Should().Be(-5);

        VideoSurfaceVolumeScroll.GetAdjustment(0, 6, hasPreciseScrollingDeltas: true)
            .Should().Be(3);
        VideoSurfaceVolumeScroll.GetAdjustment(0, 20, hasPreciseScrollingDeltas: true)
            .Should().Be(5);
        VideoSurfaceVolumeScroll.GetAdjustment(0, -20, hasPreciseScrollingDeltas: true)
            .Should().Be(-5);

        VideoSurfaceVolumeScroll.GetAdjustment(0, 0.1, hasPreciseScrollingDeltas: true)
            .Should().BeNull();
        VideoSurfaceVolumeScroll.GetAdjustment(6, 4, hasPreciseScrollingDeltas: true)
            .Should().BeNull();
        VideoSurfaceVolumeScroll.GetAdjustment(0, double.NaN, hasPreciseScrollingDeltas: true)
            .Should().BeNull();
    }

    [Fact]
    public void TryGetAdjustment_IgnoresInactivePopupOutsideAndExcludedRegions()
    {
        var excluded = new[]
        {
            new VideoSurfaceVolumeScroll.ExcludedRect(800, 0, 300, 720),
            new VideoSurfaceVolumeScroll.ExcludedRect(0, 616, 1100, 104),
        };

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 200,
                pointerY: 200,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: true,
                hasActivePopup: false,
                excludedRects: excluded)
            .Should().Be(5);

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 900,
                pointerY: 200,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: true,
                hasActivePopup: false,
                excludedRects: excluded)
            .Should().BeNull();

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 200,
                pointerY: 650,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: true,
                hasActivePopup: false,
                excludedRects: excluded)
            .Should().BeNull();

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 200,
                pointerY: 200,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: false,
                hasActivePopup: false,
                excludedRects: excluded)
            .Should().BeNull();

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 200,
                pointerY: 200,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: true,
                hasActivePopup: true,
                excludedRects: excluded)
            .Should().BeNull();

        VideoSurfaceVolumeScroll.TryGetAdjustment(
                0,
                120,
                hasPreciseScrollingDeltas: false,
                pointerX: 1200,
                pointerY: 200,
                surfaceWidth: 1100,
                surfaceHeight: 720,
                isEnabled: true,
                hasActivePopup: false,
                excludedRects: excluded)
            .Should().BeNull();
    }
}
