using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupCommitCoordinatorTests
{
    [Fact]
    public async Task FalseResult_RejectsWithoutReconciliation_AndRecoversQueue()
    {
        var queryCalls = 0;
        var discardCalls = 0;
        var recovered = 0;
        DictionaryPopupCommitResolution? observed = null;

        await DictionaryPopupCommitCoordinator.ObserveAsync(
            3,
            () => Task.FromResult(false),
            () => { queryCalls++; return Task.FromResult<long?>(3); },
            () => { discardCalls++; return Task.CompletedTask; },
            resolution => { observed = resolution; recovered++; },
            TimeSpan.FromSeconds(1));

        observed.Should().Be(DictionaryPopupCommitResolution.Rejected);
        queryCalls.Should().Be(0);
        discardCalls.Should().Be(0);
        recovered.Should().Be(1);
    }

    [Fact]
    public async Task Exception_ReconcilesMatchingCommittedGeneration()
    {
        DictionaryPopupCommitResolution? observed = null;

        await DictionaryPopupCommitCoordinator.ObserveAsync(
            7,
            () => Task.FromException<bool>(new InvalidOperationException("script failed")),
            () => Task.FromResult<long?>(7),
            () => throw new InvalidOperationException("discard must not run"),
            resolution => observed = resolution,
            TimeSpan.FromSeconds(1));

        observed.Should().Be(DictionaryPopupCommitResolution.ReconciledCommitted);
    }

    [Fact]
    public async Task Timeout_WithDifferentCommittedGeneration_DiscardsAndAborts()
    {
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = 0;
        DictionaryPopupCommitResolution? observed = null;

        await DictionaryPopupCommitCoordinator.ObserveAsync(
            9,
            () => never.Task,
            () => Task.FromResult<long?>(4),
            () => { discarded++; return Task.CompletedTask; },
            resolution => observed = resolution,
            TimeSpan.FromMilliseconds(20));

        observed.Should().Be(DictionaryPopupCommitResolution.Aborted);
        discarded.Should().Be(1);
    }

    [Fact]
    public async Task Exception_WithUnavailableRenderer_ReleasesQueueAsRendererUnavailable()
    {
        DictionaryPopupCommitResolution? observed = null;
        var discarded = 0;

        await DictionaryPopupCommitCoordinator.ObserveAsync(
            12,
            () => Task.FromException<bool>(new InvalidOperationException("commit failed")),
            () => Task.FromException<long?>(new InvalidOperationException("renderer gone")),
            () => { discarded++; return Task.CompletedTask; },
            resolution => observed = resolution,
            TimeSpan.FromSeconds(1));

        observed.Should().Be(DictionaryPopupCommitResolution.RendererUnavailable);
        discarded.Should().Be(1);
    }

    [Fact]
    public async Task SuccessfulResult_AndContentReady_CompleteIdempotently_AndRecoverOnceEachSignal()
    {
        var transaction = new DictionaryPopupDisplayTransaction();
        transaction.BeginPending(5, "ready");
        transaction.TryAcceptCommit(5);
        var completions = 0;
        var recoveries = 0;

        void Resolve(DictionaryPopupCommitResolution resolution)
        {
            if (resolution is DictionaryPopupCommitResolution.Committed
                or DictionaryPopupCommitResolution.ReconciledCommitted)
            {
                if (transaction.TryCompleteCommit(5, out _))
                    completions++;
            }
            recoveries++;
        }

        transaction.TryCompleteCommit(5, out _).Should().BeTrue(); // contentReady wins
        completions++;
        await DictionaryPopupCommitCoordinator.ObserveAsync(
            5,
            () => Task.FromResult(true),
            () => Task.FromResult<long?>(5),
            () => Task.CompletedTask,
            Resolve,
            TimeSpan.FromSeconds(1));

        completions.Should().Be(1);
        recoveries.Should().Be(1);
        transaction.CommittedGeneration.Should().Be(5);
    }
}
