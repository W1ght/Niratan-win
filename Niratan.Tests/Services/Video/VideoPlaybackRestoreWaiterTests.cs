using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoPlaybackRestoreWaiterTests
{
    [Fact]
    public async Task WaitForRestoreTargetAsync_WaitsForLoadedDurationBeforeRestoring()
    {
        var durations = new Queue<TimeSpan>(
        [
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(120),
        ]);
        var calls = 0;
        var state = new VideoPlaybackState(
            PositionSeconds: 60,
            DurationSeconds: 120,
            SubtitleSelection: VideoSubtitleSelection.None());

        var target = await VideoPlaybackRestoreWaiter.WaitForRestoreTargetAsync(
            state,
            _ =>
            {
                calls++;
                return Task.FromResult(durations.Dequeue());
            },
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        target.Should().NotBeNull();
        target!.Position.Should().Be(TimeSpan.FromSeconds(60));
        target.Duration.Should().Be(TimeSpan.FromSeconds(120));
        calls.Should().Be(3);
    }

    [Fact]
    public async Task WaitForRestoreTargetAsync_UsesLoadedDurationToSkipNearEndProgress()
    {
        var durations = new Queue<TimeSpan>(
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(120),
        ]);
        var state = new VideoPlaybackState(
            PositionSeconds: 119,
            DurationSeconds: 300,
            SubtitleSelection: VideoSubtitleSelection.None());

        var target = await VideoPlaybackRestoreWaiter.WaitForRestoreTargetAsync(
            state,
            _ => Task.FromResult(durations.Dequeue()),
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        target.Should().BeNull();
    }

    [Fact]
    public async Task WaitForRestoreTargetAsync_FallsBackToStoredDurationWhenEngineDurationIsNotReady()
    {
        var state = new VideoPlaybackState(
            PositionSeconds: 60,
            DurationSeconds: 120,
            SubtitleSelection: VideoSubtitleSelection.None());

        var target = await VideoPlaybackRestoreWaiter.WaitForRestoreTargetAsync(
            state,
            _ => Task.FromResult(TimeSpan.Zero),
            (_, _) => Task.CompletedTask,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        target.Should().NotBeNull();
        target!.Position.Should().Be(TimeSpan.FromSeconds(60));
        target.Duration.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task WaitForRestoreTargetAsync_DoesNotPollDurationWhenPositionIsEmpty()
    {
        var calls = 0;
        var state = new VideoPlaybackState(
            PositionSeconds: 0,
            DurationSeconds: 120,
            SubtitleSelection: VideoSubtitleSelection.None());

        var target = await VideoPlaybackRestoreWaiter.WaitForRestoreTargetAsync(
            state,
            _ =>
            {
                calls++;
                return Task.FromResult(TimeSpan.FromSeconds(120));
            },
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken);

        target.Should().BeNull();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task WaitForSeekPositionAsync_ReturnsTrueWhenPlaybackReachesRestoreTarget()
    {
        var positions = new Queue<TimeSpan>(
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(58.5),
        ]);

        var restored = await VideoPlaybackRestoreWaiter.WaitForSeekPositionAsync(
            TimeSpan.FromSeconds(60),
            _ => Task.FromResult(positions.Dequeue()),
            (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        restored.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForSeekPositionAsync_ReturnsFalseWhenPlaybackStaysNearStart()
    {
        var restored = await VideoPlaybackRestoreWaiter.WaitForSeekPositionAsync(
            TimeSpan.FromSeconds(60),
            _ => Task.FromResult(TimeSpan.FromSeconds(2)),
            (_, _) => Task.CompletedTask,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        restored.Should().BeFalse();
    }
}
