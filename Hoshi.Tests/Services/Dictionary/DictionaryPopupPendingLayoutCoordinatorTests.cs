using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupPendingLayoutCoordinatorTests
{
    [Fact]
    public void PendingCancellation_ClearsFirstLayoutOwnership()
    {
        var state = new DictionaryPopupPendingLayoutCoordinator<string>();
        state.Stage(1, "first", "first-layout");

        state.TryCancel(1, "first", contentCancellationSucceeded: true)
            .Should().BeTrue();

        state.HasPending.Should().BeFalse();
        state.TryComplete(1, "first", out _).Should().BeFalse();
    }

    [Fact]
    public void CommitInFlightCancellation_RejectedForFirstCommit_PreservesLayoutOwnership()
    {
        var state = new DictionaryPopupPendingLayoutCoordinator<string>();
        state.Stage(1, "first", "first-layout");

        state.TryCancel(1, "first", contentCancellationSucceeded: false)
            .Should().BeFalse();
        state.TryComplete(1, "first", out var layout).Should().BeTrue();

        layout.Should().Be("first-layout");
        state.HasPending.Should().BeFalse();
    }

    [Fact]
    public void CommitInFlightCancellation_RejectedForReplacement_CommitsReplacementLayout()
    {
        var state = new DictionaryPopupPendingLayoutCoordinator<string>();
        state.Stage(1, "first", "first-layout");
        state.TryComplete(1, "first", out _).Should().BeTrue();
        state.Stage(2, "replacement", "replacement-layout");

        state.TryCancel(2, "replacement", contentCancellationSucceeded: false)
            .Should().BeFalse();
        state.TryComplete(2, "replacement", out var layout).Should().BeTrue();

        layout.Should().Be("replacement-layout");
    }

    [Fact]
    public void AcceptedCommitAbort_ClearsMatchingLayoutOwnership()
    {
        var state = new DictionaryPopupPendingLayoutCoordinator<string>();
        state.Stage(7, "recovering", "failed-layout");

        state.TryAbort(7, "recovering").Should().BeTrue();

        state.HasPending.Should().BeFalse();
        state.TryComplete(7, "recovering", out _).Should().BeFalse();
    }
}
