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

        tracker.TryComplete(first, 1, 0.25).Should().BeFalse();
        tracker.TryComplete(second, 1, 0.75).Should().BeFalse();
        tracker.TryComplete(second, 2, double.NaN).Should().BeFalse();
        tracker.TryComplete(second, 2, 0.5).Should().BeTrue();
        tracker.HasPending.Should().BeFalse();
    }

    [Fact]
    public void TryComplete_AllowsPageAlignedBridgeProgress()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(0);

        tracker.TryComplete(generation, 0, 0.25).Should().BeTrue();
    }

    [Fact]
    public void Cancel_InvalidatesPendingDestination()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(1);

        tracker.Cancel();

        tracker.TryComplete(generation, 1, 0.5).Should().BeFalse();
    }
}
