using FluentAssertions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderProgrammaticNavigationTrackerTests
{
    [Fact]
    public void TryComplete_RequiresCurrentGenerationChapterAndFiniteResolvedProgress()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var first = tracker.Begin(chapterIndex: 1);
        var second = tracker.Begin(chapterIndex: 2);

        tracker.TryBeginCompletion(first, 1, 0.25).Should().BeFalse();
        tracker.TryBeginCompletion(second, 1, 0.75).Should().BeFalse();
        tracker.TryBeginCompletion(second, 2, double.NaN).Should().BeFalse();
        tracker.TryBeginCompletion(second, 2, 0.5).Should().BeTrue();
        tracker.HasPending.Should().BeTrue();
        tracker.CanAcceptReaderInput.Should().BeFalse();
        tracker.TryBeginCompletion(second, 2, 0.5).Should().BeFalse();
        tracker.CompleteCommit(second, 2).Should().BeTrue();
        tracker.HasPending.Should().BeFalse();
        tracker.CanAcceptReaderInput.Should().BeTrue();
    }

    [Fact]
    public void TryComplete_AllowsPageAlignedBridgeProgress()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(0);

        tracker.TryBeginCompletion(generation, 0, 0.25).Should().BeTrue();
        tracker.CompleteCommit(generation, 0).Should().BeTrue();
    }

    [Fact]
    public void Cancel_InvalidatesPendingDestination()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(1);

        tracker.Cancel();

        tracker.TryBeginCompletion(generation, 1, 0.5).Should().BeFalse();
    }
}
