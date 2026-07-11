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
}
