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

        state.CancelPending(2, "cancelled").Should().BeTrue();
        state.HasCommittedContent.Should().BeTrue();

        state.BeginPending(3, "newer");
        state.CancelPending(2, "cancelled").Should().BeFalse();
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

        state.CancelPending(6, "current").Should().BeFalse();
        state.CancelPending(7, null).Should().BeFalse();
        state.CancelPending(7, "stale").Should().BeFalse();
        state.PendingGeneration.Should().Be(7);

        state.CancelPending(7, "current").Should().BeTrue();
        state.PendingGeneration.Should().BeNull();
    }

    [Fact]
    public void CommitInFlight_RejectsReplacementAndCancellationUntilCompletion()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(11, "accepted");
        state.TryAcceptCommit(11).Should().BeTrue();

        state.CommitInFlightGeneration.Should().Be(11);
        state.CancelPending(11, "accepted").Should().BeFalse();
        state.BeginPending(12, "newer");
        state.PendingGeneration.Should().BeNull();
        state.TryAcceptCommit(12).Should().BeFalse();
        state.TryCompleteCommit(12, out _).Should().BeFalse();

        state.TryCompleteCommit(11, out var commit).Should().BeTrue();
        commit.Should().Be(new DictionaryPopupContentCommit(11, "accepted"));
        state.CommittedGeneration.Should().Be(11);
    }
}
