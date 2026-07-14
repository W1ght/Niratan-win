using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Views.Pages;

namespace Niratan.Tests.Views.Pages;

public sealed class NovelReaderRenderStateTests
{
    private const string SourceUri = "https://niratan-novel-book.local/source.xhtml";
    private const string DestinationUri = "https://niratan-novel-book.local/destination.xhtml";

    [Fact]
    public void SettingsReload_DuringHiddenDestination_IsDeferredWithoutReplacingActualAttempt()
    {
        var state = new NovelReaderRenderState();
        state.BeginNavigation(CreateRequest(), DestinationUri, waitsForFragment: false);

        var started = state.TryBeginOrdinary(SourceUri, chapterIndex: 1, progress: 0.25);

        started.Should().BeFalse();
        state.HasDeferredOrdinaryReload.Should().BeTrue();
        state.CurrentAttempt.Should().Be(new NovelReaderRenderAttempt(
            DestinationUri,
            2,
            0.75,
            null,
            7,
            NovelReaderRenderAttemptKind.Destination,
            1));
        state.TryGetDomAttempt(DestinationUri, out _).Should().BeTrue();
        state.TryGetDomAttempt(SourceUri, out _).Should().BeFalse();
    }

    [Fact]
    public void SourceSettlement_CreatesExplicitImmutableRecoveryAttempt()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source,
            ShouldRevealDestination: false);

        state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();

        state.CurrentAttempt.Should().Be(new NovelReaderRenderAttempt(
            SourceUri,
            request.Source.ChapterIndex,
            request.Source.Progress,
            null,
            request.Generation,
            NovelReaderRenderAttemptKind.Recovery,
            2));
        state.TryGetDomAttempt(SourceUri, out var recovery).Should().BeTrue();
        recovery.ChapterIndex.Should().Be(request.Source.ChapterIndex);
        state.TryGetDomAttempt(DestinationUri, out _).Should().BeFalse();
    }

    [Fact]
    public void ReservedDestination_CannotStartAfterLifecycleSettlesToSource()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);

        state.CanStartDestinationRender(request.Generation).Should().BeTrue();

        state.TryApplySettlement(
            new ReaderNavigationSettlement(
                request.Generation,
                request.Source,
                ShouldRevealDestination: false),
            SourceUri).Should().BeTrue();

        state.CanStartDestinationRender(request.Generation).Should().BeFalse();
        state.CanStartDestinationRender(request.Generation + 1).Should().BeFalse();
        state.CurrentAttempt!.Kind.Should().Be(NovelReaderRenderAttemptKind.Recovery);
    }

    [Fact]
    public void SameChapterRecovery_RejectsDelayedDestinationReadyByRenderAttemptId()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest() with
        {
            Destination = ReaderNavigationDestination.AtProgress(1, 0.75),
        };
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var destinationAttemptId = state.CurrentAttempt!.RenderAttemptId;
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source,
            ShouldRevealDestination: false);

        state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();
        var recoveryAttemptId = state.CurrentAttempt!.RenderAttemptId;

        recoveryAttemptId.Should().BeGreaterThan(destinationAttemptId);
        state.AcceptChapterReady(
                request.Source.ChapterIndex,
                request.Generation,
                destinationAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(
                request.Source.ChapterIndex,
                request.Generation,
                recoveryAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);
    }

    [Fact]
    public void OrdinaryReady_AcceptsOnlyNullGenerationAndActualChapter()
    {
        var state = new NovelReaderRenderState();
        state.TryBeginOrdinary(SourceUri, chapterIndex: 1, progress: 0.25).Should().BeTrue();
        var renderAttemptId = state.CurrentAttempt!.RenderAttemptId;

        state.AcceptChapterReady(chapterIndex: 1, navigationGeneration: 7, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: null, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(chapterIndex: 1, navigationGeneration: null, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Ordinary);
    }

    [Fact]
    public void FragmentDestination_SeparatesInitialReadyFromTerminalReady()
    {
        var state = new NovelReaderRenderState();
        state.BeginNavigation(CreateRequest(), DestinationUri, waitsForFragment: true);
        var renderAttemptId = state.CurrentAttempt!.RenderAttemptId;

        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: null, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.HiddenInitial);
        state.HiddenChapterReady.Should().BeFalse();
        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: 7, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);
        state.HiddenChapterReady.Should().BeTrue();
    }

    [Fact]
    public async Task TerminalFailure_ReleasesGenerationAndCompletesWaiterExactlyOnce()
    {
        var state = new NovelReaderRenderState();
        state.BeginNavigation(CreateRequest(), DestinationUri, waitsForFragment: false);
        var terminal = state.WaitForTerminalAsync(7);

        var first = state.TryPrepareFailure();
        var second = state.TryPrepareFailure();

        first.Should().Be(new NovelReaderRenderRelease(7));
        second.Should().BeNull();
        terminal.IsCompleted.Should().BeFalse();
        state.CompleteFailure(first!.Value).Should().BeTrue();
        await terminal.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        state.HasActiveNavigation.Should().BeFalse();
        state.CurrentAttempt.Should().BeNull();
    }

    [Fact]
    public async Task RecoveryFailure_IsTerminalAndCannotStartAnotherRecoveryLoop()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        state.TryApplySettlement(
            new ReaderNavigationSettlement(7, request.Source, ShouldRevealDestination: false),
            SourceUri).Should().BeTrue();
        var terminal = state.WaitForTerminalAsync(7);

        var release = state.TryPrepareFailure();
        release.Should().Be(new NovelReaderRenderRelease(7));
        state.TryPrepareFailure().Should().BeNull();
        terminal.IsCompleted.Should().BeFalse();
        state.CompleteFailure(release!.Value).Should().BeTrue();
        await terminal.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        state.PendingSettlement.Should().BeNull();
    }

    [Fact]
    public void ForcedCommittedSettlement_CreatesFreshDestinationRecoveryAttempt()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var failedAttemptId = state.CurrentAttempt!.RenderAttemptId;
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source with
            {
                ChapterIndex = request.Destination.ChapterIndex,
                Progress = request.Destination.ExactProgress!.Value,
            },
            ShouldRevealDestination: true);

        state.TryApplySettlement(
            settlement,
            DestinationUri,
            forceTerminalReload: true).Should().BeTrue();

        state.CurrentAttempt.Should().Match<NovelReaderRenderAttempt>(attempt =>
            attempt.Kind == NovelReaderRenderAttemptKind.Recovery
            && attempt.ChapterIndex == settlement.Position.ChapterIndex
            && attempt.NavigationGeneration == request.Generation
            && attempt.RenderAttemptId > failedAttemptId);
    }

    [Fact]
    public void PendingCommittedSettlement_CanStartExactlyOneFreshRecoveryAttempt()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var failedAttemptId = state.CurrentAttempt!.RenderAttemptId;
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source with
            {
                ChapterIndex = request.Destination.ChapterIndex,
                Progress = request.Destination.ExactProgress!.Value,
            },
            ShouldRevealDestination: true);
        state.TryApplySettlement(settlement, string.Empty).Should().BeTrue();

        state.TryBeginPendingSettlementRecovery(DestinationUri).Should().BeTrue();

        state.CurrentAttempt!.Kind.Should().Be(NovelReaderRenderAttemptKind.Recovery);
        state.CurrentAttempt.RenderAttemptId.Should().BeGreaterThan(failedAttemptId);
        state.TryBeginPendingSettlementRecovery(DestinationUri).Should().BeFalse();
    }

    [Fact]
    public void InvalidRecoveryUri_DoesNotConsumeSettlementOrReplaceAttempt()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var destinationAttempt = state.CurrentAttempt;
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source,
            ShouldRevealDestination: false);

        var action = () => state.TryApplySettlement(settlement, string.Empty);

        action.Should().Throw<ArgumentException>();
        state.PendingSettlement.Should().BeNull();
        state.CurrentAttempt.Should().BeSameAs(destinationAttempt);
    }

    [Fact]
    public async Task AlreadyOwnedSourceRecovery_LifecycleWaiterStaysGatedUntilMatchingReadyCompletes()
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var settlement = new ReaderNavigationSettlement(
            request.Generation,
            request.Source,
            ShouldRevealDestination: false);
        state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();
        var recoveryAttempt = state.CurrentAttempt!;
        var lifecycleWait = state.WaitForTerminalAsync(request.Generation);

        state.OwnsPendingSettlement(request.Generation).Should().BeTrue();
        state.OwnsPendingSettlement(request.Generation + 1).Should().BeFalse();
        lifecycleWait.IsCompleted.Should().BeFalse();

        state.AcceptChapterReady(
                recoveryAttempt.ChapterIndex,
                request.Generation,
                recoveryAttempt.RenderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);
        state.TryPrepareCompletion(out var release).Should().BeTrue();
        lifecycleWait.IsCompleted.Should().BeFalse(
            "the native acknowledgement must happen before terminal release");

        state.CompleteSuccess(release).Should().BeTrue();
        await lifecycleWait.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        state.HasActiveNavigation.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DestinationCompletion_WaitsForBothSettlementAndMatchingReady(bool readyFirst)
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var terminal = state.WaitForTerminalAsync(7);
        var renderAttemptId = state.CurrentAttempt!.RenderAttemptId;
        var settlement = new ReaderNavigationSettlement(
            7,
            request.Source with { ChapterIndex = 2, Progress = 0.8, Revision = 11 },
            ShouldRevealDestination: true);

        if (readyFirst)
            state.AcceptChapterReady(2, 7, renderAttemptId).Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);
        else
            state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();

        state.TryPrepareCompletion(out _).Should().BeFalse();

        if (readyFirst)
            state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();
        else
            state.AcceptChapterReady(2, 7, renderAttemptId).Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);

        state.TryPrepareCompletion(out var release).Should().BeTrue();
        release.Should().Be(new NovelReaderRenderRelease(7));
        terminal.IsCompleted.Should().BeFalse();
        state.CompleteSuccess(release).Should().BeTrue();
        await terminal.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        state.AcceptChapterReady(2, 7, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(2, null, renderAttemptId)
            .Should().Be(NovelReaderChapterReadyDisposition.Ordinary);
    }

    private static ReaderNavigationRenderRequest CreateRequest() =>
        new(
            Generation: 7,
            Source: new ReaderNavigationPositionSnapshot(
                BookId: "book",
                ChapterIndex: 1,
                Progress: 0.25,
                CharacterCount: 25,
                TotalCharacterCount: 100,
                Revision: 10),
            Destination: ReaderNavigationDestination.AtProgress(2, 0.75));
}
