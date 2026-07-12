using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderNavigationTransactionCoordinatorTests
{
    [Fact]
    public async Task Commit_SettlesToResolvedDestinationAndKeepsGateUntilAcknowledged()
    {
        var source = Source();
        var destination = ReaderNavigationDestination.AtChapterEnd(0);
        var sut = new ReaderNavigationTransactionCoordinator();

        sut.BlocksPositionMutation.Should().BeFalse();
        sut.ActiveRenderRequest.Should().BeNull();

        var render = sut.TryBegin(source, destination);

        render.Should().NotBeNull();
        render!.Source.Should().Be(source);
        render.Destination.Should().Be(destination);
        sut.ActiveRenderRequest.Should().BeSameAs(render);
        sut.BlocksPositionMutation.Should().BeTrue();

        var resolved = Resolved();
        var lease = sut.TryBeginCommit(render.Generation, resolved);

        lease.Should().NotBeNull();
        lease!.Source.Should().Be(source);
        lease.ResolvedDestination.Should().Be(resolved);
        sut.TryCancelRendering(render.Generation).Should().BeNull();

        var settled = sut.CompleteCommit(lease, committed: true);

        settled.Should().NotBeNull();
        settled!.Position.Should().Be(lease.ResolvedDestination);
        settled.ShouldRevealDestination.Should().BeTrue();
        (await sut.WaitForSettlementAsync()).Should().Be(settled);
        sut.BlocksPositionMutation.Should().BeTrue();
        sut.AcknowledgeTerminalRender(settled.Generation).Should().BeTrue();
        sut.BlocksPositionMutation.Should().BeFalse();
        sut.ActiveRenderRequest.Should().BeNull();
    }

    [Fact]
    public void TryBeginCommit_StaleGenerationAndWrongChapterDoNotMutateRenderingTransaction()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));
        var settlement = sut.WaitForSettlementAsync();

        sut.TryBeginCommit(render!.Generation + 1, Resolved()).Should().BeNull();
        sut.TryBeginCommit(render.Generation, Resolved() with { ChapterIndex = 2 }).Should().BeNull();

        sut.ActiveRenderRequest.Should().BeSameAs(render);
        sut.BlocksPositionMutation.Should().BeTrue();
        settlement.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void TryBeginCommit_InvalidResolvedPositionDoesNotMutateRenderingTransaction()
    {
        var source = Source();
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(source, ReaderNavigationDestination.AtChapterEnd(0));
        var settlement = sut.WaitForSettlementAsync();
        var resolved = Resolved();
        var invalidPositions = new (string Reason, ReaderNavigationPositionSnapshot Position)[]
        {
            ("book identity differs", resolved with { BookId = "book-2" }),
            ("progress is NaN", resolved with { Progress = double.NaN }),
            ("progress is positive infinity", resolved with { Progress = double.PositiveInfinity }),
            ("progress is negative infinity", resolved with { Progress = double.NegativeInfinity }),
            ("progress is below zero", resolved with { Progress = -0.01 }),
            ("progress is above one", resolved with { Progress = 1.01 }),
            ("character count is negative", resolved with { CharacterCount = -1 }),
            ("total character count is negative", resolved with { TotalCharacterCount = -1 }),
            ("character count exceeds total", resolved with { CharacterCount = 201 }),
            ("revision equals source", resolved with { Revision = source.Revision }),
            ("revision predates source", resolved with { Revision = source.Revision - 1 }),
        };

        foreach (var invalid in invalidPositions)
        {
            sut.TryBeginCommit(render!.Generation, invalid.Position)
                .Should().BeNull(because: invalid.Reason);
        }

        sut.ActiveRenderRequest.Should().BeSameAs(render);
        sut.BlocksPositionMutation.Should().BeTrue();
        settlement.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteCommit_DuplicateCompletionReturnsNullWithoutChangingSettlement()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));
        var lease = sut.TryBeginCommit(render!.Generation, Resolved());

        var first = sut.CompleteCommit(lease!, committed: true);
        var duplicate = sut.CompleteCommit(lease!, committed: false);

        first.Should().NotBeNull();
        duplicate.Should().BeNull();
        (await sut.WaitForSettlementAsync()).Should().Be(first);
        sut.BlocksPositionMutation.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteCommit_StaleLeaseReturnsNullWithoutMutatingCurrentCommit()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var firstRender = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));
        var staleLease = sut.TryBeginCommit(firstRender!.Generation, Resolved());
        var firstSettlement = sut.CompleteCommit(staleLease!, committed: true);
        sut.AcknowledgeTerminalRender(firstSettlement!.Generation).Should().BeTrue();

        var secondSource = firstSettlement.Position;
        var secondRender = sut.TryBegin(
            secondSource,
            ReaderNavigationDestination.AtChapterStart(1));
        var currentLease = sut.TryBeginCommit(
            secondRender!.Generation,
            new ReaderNavigationPositionSnapshot("book-1", 1, 0, 0, 200, 9));
        var currentSettlement = sut.WaitForSettlementAsync();

        sut.CompleteCommit(staleLease!, committed: false).Should().BeNull();
        currentSettlement.IsCompleted.Should().BeFalse();
        sut.BlocksPositionMutation.Should().BeTrue();

        var settled = sut.CompleteCommit(currentLease!, committed: false);
        (await currentSettlement).Should().Be(settled);
    }

    [Fact]
    public async Task TryCancelRendering_SettlesToImmutableSource()
    {
        var source = Source();
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(source, ReaderNavigationDestination.AtChapterEnd(0));

        var settled = sut.TryCancelRendering(render!.Generation);

        settled.Should().Be(new ReaderNavigationSettlement(
            render.Generation,
            source,
            ShouldRevealDestination: false));
        (await sut.WaitForSettlementAsync()).Should().Be(settled);
        sut.BlocksPositionMutation.Should().BeTrue();
    }

    [Fact]
    public void TryCancelRendering_DuringCommitIsRejectedAndFailedCommitSettlesToSource()
    {
        var source = Source();
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(source, ReaderNavigationDestination.AtChapterEnd(0));
        var lease = sut.TryBeginCommit(render!.Generation, Resolved());
        var settlement = sut.WaitForSettlementAsync();

        sut.TryCancelRendering(render.Generation).Should().BeNull();

        settlement.IsCompleted.Should().BeFalse();
        sut.BlocksPositionMutation.Should().BeTrue();
        sut.CompleteCommit(lease!, committed: false).Should().Be(
            new ReaderNavigationSettlement(
                render.Generation,
                source,
                ShouldRevealDestination: false));
    }

    [Fact]
    public async Task HandleBridgeErrorAsync_DuringRenderingSettlesToSource()
    {
        var source = Source();
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(source, ReaderNavigationDestination.AtChapterEnd(0));

        var settled = await sut.HandleBridgeErrorAsync();

        settled.Should().Be(new ReaderNavigationSettlement(
            render!.Generation,
            source,
            ShouldRevealDestination: false));
        sut.BlocksPositionMutation.Should().BeTrue();
    }

    [Fact]
    public async Task HandleBridgeErrorAsync_DuringCommitReturnsExistingSettlementTask()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));
        var lease = sut.TryBeginCommit(render!.Generation, Resolved());
        var existingSettlement = sut.WaitForSettlementAsync();

        var bridgeErrorSettlement = sut.HandleBridgeErrorAsync();

        bridgeErrorSettlement.Should().BeSameAs(existingSettlement);
        bridgeErrorSettlement.IsCompleted.Should().BeFalse();

        var settled = sut.CompleteCommit(lease!, committed: true);
        (await bridgeErrorSettlement).Should().Be(settled);
        (await existingSettlement).Should().Be(settled);
    }

    [Fact]
    public void TryBegin_IsRejectedUntilTerminalRenderIsAcknowledged()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var first = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));
        var settled = sut.TryCancelRendering(first!.Generation);

        sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterStart(2)).Should().BeNull();

        sut.AcknowledgeTerminalRender(settled!.Generation).Should().BeTrue();
        var second = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterStart(2));
        second.Should().NotBeNull();
        second!.Generation.Should().BeGreaterThan(first.Generation);
    }

    [Fact]
    public void AcknowledgeTerminalRender_ReleasesGateOnceAndRejectsWrongGeneration()
    {
        var sut = new ReaderNavigationTransactionCoordinator();
        var render = sut.TryBegin(Source(), ReaderNavigationDestination.AtChapterEnd(0));

        sut.AcknowledgeTerminalRender(render!.Generation).Should().BeFalse();
        var settled = sut.TryCancelRendering(render.Generation);
        sut.AcknowledgeTerminalRender(settled!.Generation + 1).Should().BeFalse();
        sut.BlocksPositionMutation.Should().BeTrue();

        sut.AcknowledgeTerminalRender(settled.Generation).Should().BeTrue();
        sut.BlocksPositionMutation.Should().BeFalse();
        sut.AcknowledgeTerminalRender(settled.Generation).Should().BeFalse();
    }

    [Fact]
    public async Task NoActiveTransaction_ReturnsNullSettlementAndRejectsTerminalOperations()
    {
        var sut = new ReaderNavigationTransactionCoordinator();

        (await sut.WaitForSettlementAsync()).Should().BeNull();
        (await sut.HandleBridgeErrorAsync()).Should().BeNull();
        sut.TryCancelRendering(1).Should().BeNull();
        sut.AcknowledgeTerminalRender(1).Should().BeFalse();
    }

    [Fact]
    public void DestinationFactories_CreateStartEndAndClampedProgressTargets()
    {
        ReaderNavigationDestination.AtChapterStart(2).Should().Be(
            new ReaderNavigationDestination(2, ReaderChapterRestoreTarget.Start, null));
        ReaderNavigationDestination.AtChapterEnd(3).Should().Be(
            new ReaderNavigationDestination(3, ReaderChapterRestoreTarget.End, null));
        ReaderNavigationDestination.AtProgress(4, -0.5).Should().Be(
            new ReaderNavigationDestination(4, null, 0));
        ReaderNavigationDestination.AtProgress(5, 1.5).Should().Be(
            new ReaderNavigationDestination(5, null, 1));
        ReaderNavigationDestination.AtProgress(6, 0.4).Should().Be(
            new ReaderNavigationDestination(6, null, 0.4));
    }

    private static ReaderNavigationPositionSnapshot Source() =>
        new("book-1", 1, 0, 100, 200, 7);

    private static ReaderNavigationPositionSnapshot Resolved() =>
        new("book-1", 0, 0.82, 82, 200, 8);
}
