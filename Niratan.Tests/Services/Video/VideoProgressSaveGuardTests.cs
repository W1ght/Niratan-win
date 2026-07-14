using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoProgressSaveGuardTests
{
    [Fact]
    public void ShouldSaveProgress_AllowsSaveWithoutRestoreFloor()
    {
        VideoProgressSaveGuard.ShouldSaveProgress(
                TimeSpan.FromSeconds(2),
                protectedRestoreFloor: null)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void ShouldSaveProgress_SuppressesAutomaticSaveBelowFailedRestoreTarget()
    {
        var floor = VideoProgressSaveGuard.CreateProtectedRestoreFloor(TimeSpan.FromSeconds(60));

        VideoProgressSaveGuard.ShouldSaveProgress(
                TimeSpan.FromSeconds(2),
                floor)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldSaveProgress_AllowsSaveAfterPlaybackPassesFailedRestoreFloor()
    {
        var floor = VideoProgressSaveGuard.CreateProtectedRestoreFloor(TimeSpan.FromSeconds(60));

        VideoProgressSaveGuard.ShouldSaveProgress(
                TimeSpan.FromSeconds(59),
                floor)
            .Should()
            .BeTrue();
    }
}
