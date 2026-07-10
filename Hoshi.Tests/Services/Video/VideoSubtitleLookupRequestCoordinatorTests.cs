using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleLookupRequestCoordinatorTests
{
    [Fact]
    public void BeginRequest_CancelsThePreviousRequestAndAdvancesVersion()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        var second = coordinator.BeginRequest();

        first.CancellationToken.IsCancellationRequested.Should().BeTrue();
        second.Version.Should().Be(first.Version + 1);
        coordinator.IsCurrent(first).Should().BeFalse();
        coordinator.IsCurrent(second).Should().BeTrue();
    }

    [Fact]
    public void CancelCurrent_InvalidatesTheCurrentRequest()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var request = coordinator.BeginRequest();

        coordinator.CancelCurrent();

        request.CancellationToken.IsCancellationRequested.Should().BeTrue();
        coordinator.IsCurrent(request).Should().BeFalse();
    }
}
