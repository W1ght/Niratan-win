using FluentAssertions;
using Hoshi.Services.Dictionary;
using Hoshi.Views.Dictionary;

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

    [Fact]
    public void CapturedCommittedGeneration_BecomesStaleWhenReplacementCommits()
    {
        var state = new DictionaryPopupRootStateCoordinator<string, string, string>();
        state.TryStage(1, "A", "A-context", "A-anchor", "A-layout")
            .Should().BeTrue();
        state.TryCommit(1, "A", out var capturedA).Should().BeTrue();
        state.TryStage(2, "B", "B-context", "B-anchor", "B-layout")
            .Should().BeTrue();

        state.IsCommitted(capturedA.Generation, capturedA.TraceId)
            .Should().BeTrue();
        state.TryCommit(2, "B", out _).Should().BeTrue();

        state.IsCommitted(capturedA.Generation, capturedA.TraceId)
            .Should().BeFalse();
        state.IsCommitted(2, "B").Should().BeTrue();
    }

    [Fact]
    public void Resize_UpdatesCommittedAndPendingLayoutsExactly_WithoutDisplayingPending()
    {
        var anchorA = new DictionaryPopupAnchorRect(120, 100, 20, 24);
        var anchorB = new DictionaryPopupAnchorRect(900, 600, 20, 24);
        var wideA = DictionaryPopupLayoutCalculator.Resolve(
            anchorA, 1200, 800, 560, 420, 360, isVertical: false);
        var wideB = DictionaryPopupLayoutCalculator.Resolve(
            anchorB, 1200, 800, 560, 420, 360, isVertical: false);
        var narrowA = DictionaryPopupLayoutCalculator.Resolve(
            anchorA, 640, 480, 560, 360, 360, isVertical: false);
        var narrowB = DictionaryPopupLayoutCalculator.Resolve(
            anchorB, 640, 480, 560, 360, 360, isVertical: false);
        var state = new DictionaryPopupRootStateCoordinator<
            string,
            DictionaryPopupAnchorRect,
            DictionaryPopupLayoutResult>();
        state.TryStage(1, "A", "A-context", anchorA, wideA).Should().BeTrue();
        state.TryCommit(1, "A", out _).Should().BeTrue();
        state.TryStage(2, "B", "B-context", anchorB, wideB).Should().BeTrue();

        state.TryUpdateCommittedLayout(1, "A", narrowA, out var resizedA)
            .Should().BeTrue();
        state.TryUpdatePendingLayout(2, "B", narrowB, out var resizedB)
            .Should().BeTrue();
        state.TryGetCommitted(out var visibleDuringB).Should().BeTrue();
        state.TryGetPending(out var pendingB).Should().BeTrue();

        visibleDuringB.Should().Be(resizedA);
        visibleDuringB.Generation.Should().Be(1);
        pendingB.Should().Be(resizedB);
        pendingB.Layout.Should().Be(narrowB);
        state.TryUpdatePendingLayout(1, "A", wideA, out _).Should().BeFalse();
        state.TryCommit(2, "B", out var committedB).Should().BeTrue();
        committedB.Layout.Should().Be(narrowB);
    }

    [Fact]
    public void PendingResizeThenAbort_LeavesResizedCommittedLayout()
    {
        var state = new DictionaryPopupRootStateCoordinator<string, string, string>();
        state.TryStage(1, "A", "A-context", "A-anchor", "A-wide").Should().BeTrue();
        state.TryCommit(1, "A", out _).Should().BeTrue();
        state.TryStage(2, "B", "B-context", "B-anchor", "B-wide").Should().BeTrue();
        state.TryUpdateCommittedLayout(1, "A", "A-narrow", out _).Should().BeTrue();
        state.TryUpdatePendingLayout(2, "B", "B-narrow", out _).Should().BeTrue();

        state.TryAbort(2, "B").Should().BeTrue();
        state.TryGetCommitted(out var committedA).Should().BeTrue();

        committedA.Generation.Should().Be(1);
        committedA.Layout.Should().Be("A-narrow");
    }
}
