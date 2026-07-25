using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoBottomChromeAutoHideStateTests
{
    [Fact]
    public void DefaultHideDelay_IsOneSecond()
    {
        VideoBottomChromeAutoHideState.DefaultHideDelay.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PointerActivityShowsChromeUntilIdleOrPointerLeave()
    {
        var state = new VideoBottomChromeAutoHideState();

        state.IsVisible.Should().BeTrue();

        state.HideForInactivity();
        state.IsVisible.Should().BeFalse();

        state.ShowForPointerActivity();
        state.IsVisible.Should().BeTrue();

        state.HideForPointerLeave();
        state.IsVisible.Should().BeFalse();
    }
}
