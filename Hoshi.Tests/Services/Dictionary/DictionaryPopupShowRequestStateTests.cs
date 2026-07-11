using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupShowRequestStateTests
{
    private sealed record TestShowRequest(
        string Id,
        CancellationToken CancellationToken,
        DictionaryPopupShowRequestState State);

    [Fact]
    public void QueuedDrop_IsReportedExactlyOnce()
    {
        var state = new DictionaryPopupShowRequestState();

        state.TryDropBeforeGeneration().Should().BeTrue();
        state.TryDropBeforeGeneration().Should().BeFalse();
        state.TryStartGeneration().Should().BeFalse();
    }

    [Fact]
    public void StartedGeneration_DoesNotReportQueuedDrop()
    {
        var state = new DictionaryPopupShowRequestState();

        state.TryStartGeneration().Should().BeTrue();

        state.TryDropBeforeGeneration().Should().BeFalse();
        state.TryStartGeneration().Should().BeFalse();
    }

    [Fact]
    public void AcceptedA_QueuedB_ReplacedByC_DropsBExactlyOnce()
    {
        var acceptedA = new DictionaryPopupShowRequestState();
        acceptedA.TryStartGeneration().Should().BeTrue();
        var queuedB = new TestShowRequest(
            "B",
            CancellationToken.None,
            new DictionaryPopupShowRequestState());
        var queuedC = new TestShowRequest(
            "C",
            CancellationToken.None,
            new DictionaryPopupShowRequestState());
        var queue = new DictionaryPopupLatestRequestQueue<TestShowRequest>();
        queue.Replace(queuedB);

        var displaced = queue.Replace(queuedC);

        displaced.Should().BeSameAs(queuedB);
        displaced!.State.TryDropBeforeGeneration().Should().BeTrue();
        displaced.State.TryDropBeforeGeneration().Should().BeFalse();
        acceptedA.TryDropBeforeGeneration().Should().BeFalse();
    }

    [Fact]
    public void CanceledQueuedB_AfterAcceptedACompletes_DropsBExactlyOnce()
    {
        using var cts = new CancellationTokenSource();
        var queuedB = new TestShowRequest(
            "B",
            cts.Token,
            new DictionaryPopupShowRequestState());
        var queue = new DictionaryPopupLatestRequestQueue<TestShowRequest>();
        queue.Replace(queuedB);
        cts.Cancel();

        queue.TryTake(out var taken).Should().BeTrue();

        taken.Should().BeSameAs(queuedB);
        taken!.CancellationToken.IsCancellationRequested.Should().BeTrue();
        taken.State.TryDropBeforeGeneration().Should().BeTrue();
        taken.State.TryDropBeforeGeneration().Should().BeFalse();
    }

    [Fact]
    public void QueueClear_DropsQueuedRequestOnceAndLeavesAcceptedTransactionOutOfBand()
    {
        var acceptedA = new DictionaryPopupShowRequestState();
        acceptedA.TryStartGeneration().Should().BeTrue();
        var queuedB = new TestShowRequest(
            "B",
            CancellationToken.None,
            new DictionaryPopupShowRequestState());
        var queue = new DictionaryPopupLatestRequestQueue<TestShowRequest>();
        queue.Replace(queuedB);

        var cleared = queue.Clear();

        cleared.Should().BeSameAs(queuedB);
        cleared!.State.TryDropBeforeGeneration().Should().BeTrue();
        queue.Clear().Should().BeNull();
        cleared.State.TryDropBeforeGeneration().Should().BeFalse();
        acceptedA.TryDropBeforeGeneration().Should().BeFalse();
    }

    [Fact]
    public void ActiveCallerCancellationDuringInjection_EmitsAbortTerminalOnce()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        transaction.TryBeginPending(21, "active", out _).Should().BeTrue();
        var request = new DictionaryPopupShowRequestState();
        request.TryStartGeneration().Should().BeTrue();

        transaction.TryCancelPending(21, "active", out var aborted).Should().BeTrue();

        aborted.Should().Be(new DictionaryPopupContentCommit(21, "active"));
        transaction.TryCancelPending(21, "active", out _).Should().BeFalse();
        request.TryDropBeforeGeneration().Should().BeFalse();
    }

    [Fact]
    public void QueuedGenerationStartedException_UsesAbortTerminalInsteadOfQueuedDrop()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        transaction.TryBeginPending(22, "queued-started", out _).Should().BeTrue();
        var request = new DictionaryPopupShowRequestState();
        request.TryStartGeneration().Should().BeTrue();

        transaction.TryCancelPending(22, "queued-started", out var aborted).Should().BeTrue();

        aborted.Should().Be(new DictionaryPopupContentCommit(22, "queued-started"));
        request.TryDropBeforeGeneration().Should().BeFalse();
    }

    [Fact]
    public void SynchronousAbortBeforeCancelReturn_DoesNotTurnCancellationIntoAccepted()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        var rootState = new DictionaryPopupRootStateCoordinator<string, string, string>();
        transaction.TryBeginPending(23, "reentrant", out _).Should().BeTrue();
        rootState.TryStage(23, "reentrant", "context", "anchor", "layout")
            .Should().BeTrue();

        var contentCancelled = transaction.TryCancelPending(
            23,
            "reentrant",
            out var aborted);
        var abortEventClearedLayout = rootState.TryAbort(
            aborted.Generation,
            aborted.TraceId);
        var secondLayoutCancellation = rootState.TryAbort(23, "reentrant");

        contentCancelled.Should().BeTrue();
        abortEventClearedLayout.Should().BeTrue();
        secondLayoutCancellation.Should().BeFalse();
        rootState.TryGetPending(out _).Should().BeFalse();
    }

    [Fact]
    public void AcceptedCancellationRejected_EmitsNoAbortOrQueuedDrop()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        transaction.TryBeginPending(24, "accepted", out _).Should().BeTrue();
        var request = new DictionaryPopupShowRequestState();
        request.TryStartGeneration().Should().BeTrue();
        transaction.TryAcceptCommit(24).Should().BeTrue();

        transaction.TryCancelPending(24, "accepted", out _).Should().BeFalse();
        request.TryDropBeforeGeneration().Should().BeFalse();
        transaction.TryCompleteCommit(24, out var committed).Should().BeTrue();

        committed.Should().Be(new DictionaryPopupContentCommit(24, "accepted"));
    }

    [Fact]
    public void DequeuedBPending_CSupersedesWithAbortBeforeCStage_AndBLateCompletionIsHarmless()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        var rootState = new DictionaryPopupRootStateCoordinator<string, string, string>();
        var requestB = new DictionaryPopupShowRequestState();
        var requestC = new DictionaryPopupShowRequestState();
        var ownershipOrder = new List<string>();
        transaction.TryBeginPending(39, "A", out _).Should().BeTrue();
        transaction.TryAcceptCommit(39).Should().BeTrue();
        transaction.TryCompleteCommit(39, out _).Should().BeTrue();
        rootState.TryStage(39, "A", "A-context", "A-anchor", "A-layout")
            .Should().BeTrue();
        rootState.TryCommit(39, "A", out _).Should().BeTrue();
        transaction.TryBeginPending(40, "B", out var preserveA).Should().BeTrue();
        requestB.TryStartGeneration().Should().BeTrue();
        rootState.TryStage(40, "B", "B-context", "B-anchor", "B-layout")
            .Should().BeTrue();

        transaction.TryBeginPending(41, "C", out _).Should().BeFalse();
        transaction.TryCancelPending(40, "B", out var abortedB).Should().BeTrue();
        ownershipOrder.Add($"abort:{abortedB.TraceId}");
        rootState.TryAbort(abortedB.Generation, abortedB.TraceId).Should().BeTrue();
        transaction.HasCommittedContent.Should().BeTrue();
        transaction.CommittedGeneration.Should().Be(39);
        transaction.TryBeginPending(41, "C", out var preserveAForC).Should().BeTrue();
        requestC.TryStartGeneration().Should().BeTrue();
        ownershipOrder.Add("stage:C");
        rootState.TryStage(41, "C", "C-context", "C-anchor", "C-layout")
            .Should().BeTrue();

        transaction.TryCancelPending(40, "B", out _).Should().BeFalse();
        rootState.TryAbort(40, "B").Should().BeFalse();
        rootState.TryCommit(41, "C", out var committedRoot).Should().BeTrue();
        transaction.TryAcceptCommit(41).Should().BeTrue();
        transaction.TryCompleteCommit(41, out var committedC).Should().BeTrue();

        requestB.TryDropBeforeGeneration().Should().BeFalse();
        preserveA.Should().BeTrue();
        preserveAForC.Should().BeTrue();
        ownershipOrder.Should().Equal("abort:B", "stage:C");
        committedRoot.Layout.Should().Be("C-layout");
        committedC.Should().Be(new DictionaryPopupContentCommit(41, "C"));
    }
}
