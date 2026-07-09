using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public sealed class VideoBottomChromeAutoHideStateTests
{
    [Fact]
    public void DefaultHideDelay_IsTwoSeconds()
    {
        VideoBottomChromeAutoHideState.DefaultHideDelay.Should().Be(TimeSpan.FromSeconds(2));
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
