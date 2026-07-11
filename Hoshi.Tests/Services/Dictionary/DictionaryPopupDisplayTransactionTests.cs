using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupDisplayTransactionTests
{
    [Fact]
    public void Replacement_PreservesCommittedContentUntilCurrentGenerationCommits()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "first").Should().BeFalse();
        state.TryAcceptCommit(1).Should().BeTrue();
        state.TryCompleteCommit(1, out _).Should().BeTrue();

        state.BeginPending(2, "second").Should().BeTrue();
        state.TryAcceptCommit(1).Should().BeFalse();
        state.TryAcceptCommit(2).Should().BeTrue();
        state.TryCompleteCommit(2, out var commit).Should().BeTrue();

        commit.Should().Be(new DictionaryPopupContentCommit(2, "second"));
        state.HasCommittedContent.Should().BeTrue();
    }

    [Fact]
    public void CancelPending_PreservesCommit_AndDismissClearsIt()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "shown");
        state.TryAcceptCommit(1);
        state.TryCompleteCommit(1, out _);
        state.BeginPending(2, "cancelled");

        state.TryCancelPending(2, "cancelled", out _).Should().BeTrue();
        state.HasCommittedContent.Should().BeTrue();

        state.BeginPending(3, "newer");
        state.TryCancelPending(2, "cancelled", out _).Should().BeFalse();
        state.PendingGeneration.Should().Be(3);

        state.Dismiss();
        state.HasCommittedContent.Should().BeFalse();
        state.PendingGeneration.Should().BeNull();
        state.CommittedGeneration.Should().BeNull();
    }

    [Fact]
    public void CancelPending_RequiresExactGenerationAndTrace()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(7, "current");

        state.TryCancelPending(6, "current", out _).Should().BeFalse();
        state.TryCancelPending(7, null, out _).Should().BeFalse();
        state.TryCancelPending(7, "stale", out _).Should().BeFalse();
        state.PendingGeneration.Should().Be(7);

        state.TryCancelPending(7, "current", out _).Should().BeTrue();
        state.PendingGeneration.Should().BeNull();
    }

    [Fact]
    public void CommitInFlight_RejectsReplacementAndCancellationUntilCompletion()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(11, "accepted");
        state.TryAcceptCommit(11).Should().BeTrue();

        state.CommitInFlightGeneration.Should().Be(11);
        state.TryCancelPending(11, "accepted", out _).Should().BeFalse();
        state.BeginPending(12, "newer");
        state.PendingGeneration.Should().BeNull();
        state.TryAcceptCommit(12).Should().BeFalse();
        state.TryCompleteCommit(12, out _).Should().BeFalse();

        state.TryCompleteCommit(11, out var commit).Should().BeTrue();
        commit.Should().Be(new DictionaryPopupContentCommit(11, "accepted"));
        state.CommittedGeneration.Should().Be(11);
    }

    [Fact]
    public void TryAbortCommit_RequiresExactInFlightGeneration_AndPreservesCommit()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "committed");
        state.TryAcceptCommit(1);
        state.TryCompleteCommit(1, out _);
        state.BeginPending(2, "accepted");
        state.TryAcceptCommit(2);

        state.TryAbortCommit(1).Should().BeFalse();
        state.CommitInFlightGeneration.Should().Be(2);
        state.TryAbortCommit(2).Should().BeTrue();

        state.CommitInFlightGeneration.Should().BeNull();
        state.CommittedGeneration.Should().Be(1);
        state.HasCommittedContent.Should().BeTrue();
    }

    [Fact]
    public void PendingCancellation_ReturnsExactAbortTerminalOnlyOnce()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(12, "queued-B");

        state.TryCancelPending(12, "queued-B", out var aborted).Should().BeTrue();
        state.TryCancelPending(12, "queued-B", out _).Should().BeFalse();

        aborted.Should().Be(new DictionaryPopupContentCommit(12, "queued-B"));
    }

    [Fact]
    public void AcceptedCancellation_IsRejectedAndCommitOwnsTheSingleTerminal()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(13, "accepted-A");
        state.TryAcceptCommit(13).Should().BeTrue();

        state.TryCancelPending(13, "accepted-A", out _).Should().BeFalse();
        state.TryCompleteCommit(13, out var committed).Should().BeTrue();
        state.TryCompleteCommit(13, out _).Should().BeFalse();

        committed.Should().Be(new DictionaryPopupContentCommit(13, "accepted-A"));
    }
}
