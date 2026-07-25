using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoLookupPlaybackCoordinatorTests
{
    [Fact]
    public void PlayingVideo_AutoPausesAndResumesAfterPopupDismissal()
    {
        var coordinator = new VideoLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(isPlaying: true).Should().BeTrue();
        coordinator.TryResumeAfterDismiss(isPlaying: false).Should().BeTrue();
    }

    [Fact]
    public void AlreadyPausedVideo_IsNotResumedAfterPopupDismissal()
    {
        var coordinator = new VideoLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(isPlaying: false).Should().BeFalse();
        coordinator.TryResumeAfterDismiss(isPlaying: false).Should().BeFalse();
    }

    [Fact]
    public void ReplacementLookup_PreservesPendingResume()
    {
        var coordinator = new VideoLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(isPlaying: true).Should().BeTrue();
        coordinator.TryPauseForLookup(isPlaying: false).Should().BeFalse();

        coordinator.TryResumeAfterDismiss(isPlaying: false).Should().BeTrue();
    }

    [Fact]
    public void ExplicitPlaybackControl_CancelsPendingResume()
    {
        var coordinator = new VideoLookupPlaybackCoordinator();
        coordinator.TryPauseForLookup(isPlaying: true).Should().BeTrue();

        coordinator.CancelAutoResume();

        coordinator.TryResumeAfterDismiss(isPlaying: false).Should().BeFalse();
    }
}
