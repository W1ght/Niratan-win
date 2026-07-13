using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Views.Pages;

namespace Hoshi.Tests.Views.Pages;

public sealed class NovelReaderRenderStateTests
{
    private const string SourceUri = "https://hoshi-novel-book.local/source.xhtml";
    private const string DestinationUri = "https://hoshi-novel-book.local/destination.xhtml";

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
            NovelReaderRenderAttemptKind.Destination));
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
            NovelReaderRenderAttemptKind.Recovery));
        state.TryGetDomAttempt(SourceUri, out var recovery).Should().BeTrue();
        recovery.ChapterIndex.Should().Be(request.Source.ChapterIndex);
        state.TryGetDomAttempt(DestinationUri, out _).Should().BeFalse();
    }

    [Fact]
    public void OrdinaryReady_AcceptsOnlyNullGenerationAndActualChapter()
    {
        var state = new NovelReaderRenderState();
        state.TryBeginOrdinary(SourceUri, chapterIndex: 1, progress: 0.25).Should().BeTrue();

        state.AcceptChapterReady(chapterIndex: 1, navigationGeneration: 7)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: null)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(chapterIndex: 1, navigationGeneration: null)
            .Should().Be(NovelReaderChapterReadyDisposition.Ordinary);
    }

    [Fact]
    public void FragmentDestination_SeparatesInitialReadyFromTerminalReady()
    {
        var state = new NovelReaderRenderState();
        state.BeginNavigation(CreateRequest(), DestinationUri, waitsForFragment: true);

        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: null)
            .Should().Be(NovelReaderChapterReadyDisposition.HiddenInitial);
        state.HiddenChapterReady.Should().BeFalse();
        state.AcceptChapterReady(chapterIndex: 2, navigationGeneration: 7)
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DestinationCompletion_WaitsForBothSettlementAndMatchingReady(bool readyFirst)
    {
        var state = new NovelReaderRenderState();
        var request = CreateRequest();
        state.BeginNavigation(request, DestinationUri, waitsForFragment: false);
        var terminal = state.WaitForTerminalAsync(7);
        var settlement = new ReaderNavigationSettlement(
            7,
            request.Source with { ChapterIndex = 2, Progress = 0.8, Revision = 11 },
            ShouldRevealDestination: true);

        if (readyFirst)
            state.AcceptChapterReady(2, 7).Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);
        else
            state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();

        state.TryPrepareCompletion(out _).Should().BeFalse();

        if (readyFirst)
            state.TryApplySettlement(settlement, SourceUri).Should().BeTrue();
        else
            state.AcceptChapterReady(2, 7).Should().Be(NovelReaderChapterReadyDisposition.HiddenTerminal);

        state.TryPrepareCompletion(out var release).Should().BeTrue();
        release.Should().Be(new NovelReaderRenderRelease(7));
        terminal.IsCompleted.Should().BeFalse();
        state.CompleteSuccess(release).Should().BeTrue();
        await terminal.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        state.AcceptChapterReady(2, 7)
            .Should().Be(NovelReaderChapterReadyDisposition.Rejected);
        state.AcceptChapterReady(2, null)
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
