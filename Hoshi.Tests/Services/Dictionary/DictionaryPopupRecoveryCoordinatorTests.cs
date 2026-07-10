using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupRecoveryCoordinatorTests
{
    [Fact]
    public void Recovery_DoesNotReleaseLatestQueueUntilFreshEpochIsReady()
    {
        var recovery = new DictionaryPopupRecoveryCoordinator();
        var transaction = new DictionaryPopupDisplayTransaction();
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        transaction.BeginPending(8, "accepted");
        transaction.TryAcceptCommit(8).Should().BeTrue();
        queue.Replace("first");
        queue.Replace("latest");

        recovery.TryStartAttempt(8, 21, out var firstAttempt).Should().BeTrue();
        recovery.TryComplete(firstAttempt, 21).Should().BeFalse();
        transaction.CommitInFlightGeneration.Should().Be(8);

        recovery.FailAttempt(firstAttempt);
        recovery.TryStartAttempt(8, 21, out var retry).Should().BeTrue();
        DictionaryPopupDocumentEpoch.Matches(22, 21).Should().BeFalse(
            "a late command from the old document must be rejected after navigation");
        recovery.TryComplete(retry, 22).Should().BeTrue();
        transaction.TryAbortCommit(8).Should().BeTrue();

        queue.TryTake(_ => true, out var request).Should().BeTrue();
        request.Should().Be("latest");
        transaction.BeginPending(9, "latest");
        transaction.PendingGeneration.Should().Be(9,
            "the latest request starts only after the fresh epoch releases old ownership");
    }

    [Fact]
    public void FailedAttempt_RetainsOwnershipForRetry_AndExactCancelRejectsStaleTicket()
    {
        var recovery = new DictionaryPopupRecoveryCoordinator();
        recovery.TryStartAttempt(4, 10, out var first).Should().BeTrue();
        recovery.FailAttempt(first);

        recovery.IsRecovering(4, 10).Should().BeTrue();
        recovery.TryStartAttempt(4, 10, out var retry).Should().BeTrue();
        recovery.Cancel(first).Should().BeFalse();
        recovery.Cancel(retry).Should().BeTrue();
        recovery.IsRecovering(4, 10).Should().BeFalse();
    }
}
