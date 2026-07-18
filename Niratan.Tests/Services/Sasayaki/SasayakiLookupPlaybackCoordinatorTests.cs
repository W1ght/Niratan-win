using FluentAssertions;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiLookupPlaybackCoordinatorTests
{
    [Fact]
    public void PlayingAudio_AutoPausesAndResumesAfterPopupDismissal()
    {
        var coordinator = new SasayakiLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: true)
            .Should().BeTrue();
        coordinator.TryResumeAfterDismiss(isPlaying: false)
            .Should().BeTrue();
    }

    [Fact]
    public void AlreadyPausedAudio_IsNotResumedAfterPopupDismissal()
    {
        var coordinator = new SasayakiLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: false)
            .Should().BeFalse();
        coordinator.TryResumeAfterDismiss(isPlaying: false)
            .Should().BeFalse();
    }

    [Fact]
    public void ReplacementLookup_PreservesPendingResume()
    {
        var coordinator = new SasayakiLookupPlaybackCoordinator();

        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: true)
            .Should().BeTrue();
        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: false)
            .Should().BeFalse();

        coordinator.TryResumeAfterDismiss(isPlaying: false)
            .Should().BeTrue();
    }

    [Fact]
    public void ExplicitPlaybackControl_CancelsPendingResume()
    {
        var coordinator = new SasayakiLookupPlaybackCoordinator();
        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: true)
            .Should().BeTrue();

        coordinator.CancelAutoResume();

        coordinator.TryResumeAfterDismiss(isPlaying: false)
            .Should().BeFalse();
    }

    [Fact]
    public void PlaybackAlreadyResumed_DoesNotResumeTwiceAndClearsPendingState()
    {
        var coordinator = new SasayakiLookupPlaybackCoordinator();
        coordinator.TryPauseForLookup(autoPauseEnabled: true, isPlaying: true)
            .Should().BeTrue();

        coordinator.TryResumeAfterDismiss(isPlaying: true)
            .Should().BeFalse();
        coordinator.TryResumeAfterDismiss(isPlaying: false)
            .Should().BeFalse();
    }
}
