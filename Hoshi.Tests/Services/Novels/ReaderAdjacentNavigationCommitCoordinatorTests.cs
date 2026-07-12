using FluentAssertions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderAdjacentNavigationCommitCoordinatorTests
{
    [Fact]
    public async Task CommitAsync_FailedPersistenceAbortsAndRecoversWithoutPublishingDestination()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(1);
        var events = new List<string>();
        var sut = new ReaderAdjacentNavigationCommitCoordinator(tracker);

        var result = await sut.CommitAsync(
            generation,
            1,
            0.75,
            _ => Task.FromResult(false),
            () =>
            {
                events.Add("publish");
                return Task.CompletedTask;
            },
            () =>
            {
                events.Add("recover");
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        events.Should().Equal("recover");
        tracker.HasPending.Should().BeFalse();
        tracker.CanAcceptReaderInput.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_ThrowOrCancellationAbortsAndRecovers(bool cancel)
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(1);
        var recovered = false;
        var sut = new ReaderAdjacentNavigationCommitCoordinator(tracker);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        if (cancel)
            cts.Cancel();

        var act = () => sut.CommitAsync(
            generation,
            1,
            0.75,
            _ => cancel
                ? Task.FromCanceled<bool>(cts.Token)
                : Task.FromException<bool>(new IOException("commit failed")),
            () => Task.CompletedTask,
            () =>
            {
                recovered = true;
                return Task.CompletedTask;
            },
            cts.Token);

        if (cancel)
            await act.Should().ThrowAsync<OperationCanceledException>();
        else
            await act.Should().ThrowAsync<IOException>();
        recovered.Should().BeTrue();
        tracker.HasPending.Should().BeFalse();
        tracker.CanAcceptReaderInput.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_SuccessKeepsGateThroughVisibleCommitThenCompletes()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var generation = tracker.Begin(1);
        var gateDuringPublish = true;
        var sut = new ReaderAdjacentNavigationCommitCoordinator(tracker);

        var result = await sut.CommitAsync(
            generation,
            1,
            0.75,
            _ => Task.FromResult(true),
            () =>
            {
                gateDuringPublish = !tracker.CanAcceptReaderInput;
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        gateDuringPublish.Should().BeTrue();
        tracker.CanAcceptReaderInput.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_StaleOrDuplicateGenerationDoesNotPrepareOrPersist()
    {
        var tracker = new ReaderProgrammaticNavigationTracker();
        var stale = tracker.Begin(0);
        var current = tracker.Begin(1);
        var prepared = 0;
        var persisted = 0;
        var sut = new ReaderAdjacentNavigationCommitCoordinator(tracker);

        var staleResult = await sut.CommitAsync(
            stale,
            0,
            0.25,
            _ =>
            {
                persisted++;
                return Task.FromResult(true);
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            TestContext.Current.CancellationToken,
            prepareAsync: () =>
            {
                prepared++;
                return Task.CompletedTask;
            });
        var committed = await sut.CommitAsync(
            current,
            1,
            0.75,
            _ =>
            {
                persisted++;
                return Task.FromResult(true);
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            TestContext.Current.CancellationToken,
            prepareAsync: () =>
            {
                prepared++;
                return Task.CompletedTask;
            });
        var duplicate = await sut.CommitAsync(
            current,
            1,
            0.9,
            _ =>
            {
                persisted++;
                return Task.FromResult(true);
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            TestContext.Current.CancellationToken,
            prepareAsync: () =>
            {
                prepared++;
                return Task.CompletedTask;
            });

        staleResult.Should().BeFalse();
        committed.Should().BeTrue();
        duplicate.Should().BeFalse();
        prepared.Should().Be(1);
        persisted.Should().Be(1);
    }
}
