using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupRootStateCoordinatorTests
{
    [Fact]
    public void PendingReplacement_NestedAndResizeSnapshotsRemainCommittedUntilExactAbort()
    {
        var state = new DictionaryPopupRootStateCoordinator<string, string, string>();
        state.TryStage(1, "A", "A-context", "A-anchor", "A-layout")
            .Should().BeTrue();
        state.TryCommit(1, "A", out _).Should().BeTrue();

        state.TryStage(2, "B", "B-context", "B-anchor", "B-layout")
            .Should().BeTrue();
        state.TryGetPendingGeneration("B", out var pendingB).Should().BeTrue();
        state.TryGetCommitted(out var whileBPending).Should().BeTrue();
        state.TryAbort(2, "B").Should().BeTrue();
        state.TryGetCommitted(out var afterBAbort).Should().BeTrue();

        whileBPending.Context.Should().Be("A-context");
        whileBPending.Anchor.Should().Be("A-anchor");
        pendingB.Should().Be(2);
        afterBAbort.Should().Be(whileBPending);
    }

    [Fact]
    public void MatchingCommit_PromotesContextAnchorAndLayoutTogether()
    {
        var state = new DictionaryPopupRootStateCoordinator<string, string, string>();
        state.TryStage(1, "A", "A-context", "A-anchor", "A-layout")
            .Should().BeTrue();
        state.TryCommit(1, "A", out _).Should().BeTrue();
        state.TryStage(2, "B", "B-context", "B-anchor", "B-layout")
            .Should().BeTrue();

        state.TryCommit(2, "B", out var committed).Should().BeTrue();

        committed.Context.Should().Be("B-context");
        committed.Anchor.Should().Be("B-anchor");
        committed.Layout.Should().Be("B-layout");
        state.TryGetCommitted(out var snapshot).Should().BeTrue();
        snapshot.Should().Be(committed);
    }

    [Fact]
    public void StaleAbort_CannotClearNewerPendingOrCommittedState()
    {
        var state = new DictionaryPopupRootStateCoordinator<string, string, string>();
        state.TryStage(1, "A", "A-context", "A-anchor", "A-layout")
            .Should().BeTrue();
        state.TryCommit(1, "A", out _).Should().BeTrue();
        state.TryStage(2, "B", "B-context", "B-anchor", "B-layout")
            .Should().BeTrue();
        state.TryStage(3, "C", "C-context", "C-anchor", "C-layout")
            .Should().BeFalse();
        state.TryAbort(2, "B").Should().BeTrue();
        state.TryStage(3, "C", "C-context", "C-anchor", "C-layout")
            .Should().BeTrue();

        state.TryAbort(2, "B").Should().BeFalse();
        state.TryCommit(3, "C", out var committedC).Should().BeTrue();
        state.TryAbort(2, "B").Should().BeFalse();
        state.TryGetCommitted(out var current).Should().BeTrue();

        current.Should().Be(committedC);
        current.Context.Should().Be("C-context");

        state.Clear();
        state.TryGetCommitted(out _).Should().BeFalse();
    }
}
