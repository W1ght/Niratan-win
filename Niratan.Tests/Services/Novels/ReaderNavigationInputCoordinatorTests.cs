using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.DTO;
using Niratan.Models.Novel;
using Niratan.Models.Profiles;
using Niratan.Models.Settings;
using Niratan.Services.Novels;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.Services.Sync;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;
using Moq;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderNavigationInputCoordinatorTests
{
    [Fact]
    public void HistoryMutation_CommitsOnlyWithOwnedDestinationSettlement()
    {
        var ct = TestContext.Current.CancellationToken;
        var generation = 0L;
        var current = Snapshot(chapter: 0, progress: 0.2, revision: 1);
        var input = CreateInput(() => current, () => ++generation);

        var navigation = input.TryNavigate(
            ReaderNavigationInputKind.SearchResult,
            chapterIndex: 1,
            progress: 0.4,
            ct: ct);

        navigation.Should().NotBeNull();
        input.BackTarget.Should().BeNull("reservation must not publish history");
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            navigation!.RenderRequest.Generation,
            navigation.RenderRequest.Source,
            ShouldRevealDestination: false)).Should().BeTrue();
        input.BackTarget.Should().BeNull("source recovery rolls the pending mutation back");

        navigation = input.TryNavigate(
            ReaderNavigationInputKind.SearchResult,
            chapterIndex: 1,
            progress: 0.4,
            ct: ct);
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            navigation!.RenderRequest.Generation,
            Snapshot(chapter: 1, progress: 0.4, revision: 2),
            ShouldRevealDestination: true)).Should().BeTrue();

        input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        input.ForwardTarget.Should().BeNull();
    }

    [Fact]
    public void BackAndForwardHistory_SourceRecoveryLeavesBothStacksUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var generation = 0L;
        var current = Snapshot(chapter: 0, progress: 0.2, revision: 1);
        var input = CreateInput(() => current, () => ++generation);
        var navigation = input.TryNavigate(
            ReaderNavigationInputKind.TableOfContents,
            chapterIndex: 1,
            progress: 0.4,
            ct: ct);
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            navigation!.RenderRequest.Generation,
            Snapshot(chapter: 1, progress: 0.4, revision: 2),
            ShouldRevealDestination: true));
        current = Snapshot(chapter: 1, progress: 0.4, revision: 2);

        var back = input.TryGoBack(ct);
        input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        input.ForwardTarget.Should().BeNull();
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            back!.RenderRequest.Generation,
            back.RenderRequest.Source,
            ShouldRevealDestination: false)).Should().BeTrue();
        input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        input.ForwardTarget.Should().BeNull();

        back = input.TryGoBack(ct);
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            back!.RenderRequest.Generation,
            Snapshot(chapter: 0, progress: 0.2, revision: 3),
            ShouldRevealDestination: true)).Should().BeTrue();
        current = Snapshot(chapter: 0, progress: 0.2, revision: 3);
        input.BackTarget.Should().BeNull();
        input.ForwardTarget.Should().Be(new ReaderNavigationPosition(1, 0.4));

        var forward = input.TryGoForward(ct);
        input.BackTarget.Should().BeNull();
        input.ForwardTarget.Should().Be(new ReaderNavigationPosition(1, 0.4));
        input.ApplyNavigationSettlement(new ReaderNavigationSettlement(
            forward!.RenderRequest.Generation,
            forward.RenderRequest.Source,
            ShouldRevealDestination: false)).Should().BeTrue();
        input.BackTarget.Should().BeNull();
        input.ForwardTarget.Should().Be(new ReaderNavigationPosition(1, 0.4));
    }

    [Fact]
    public async Task HistoryReservation_WithBlockedWriterRejectsConcurrentCommandWithoutMutatingHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await Harness.CreateAsync(ct);
        await harness.NavigateAndCommitAsync(
            ReaderNavigationInputKind.SearchResult,
            chapterIndex: 1,
            progress: 0.4,
            ct);
        harness.ViewModel.UpdateProgress(0.5);
        harness.BlockSaveCall(2);
        var priorWriter = harness.ViewModel.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await harness.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        var back = harness.Input.TryGoBack(ct);
        var concurrentForward = harness.Input.TryGoForward(ct);

        back.Should().NotBeNull();
        back!.DepartureCheckpoint.IsCompleted.Should().BeFalse();
        concurrentForward.Should().BeNull();
        harness.Input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        harness.Input.ForwardTarget.Should().BeNull();
        harness.ViewModel.CurrentChapterIndex.Should().Be(1);
        harness.ViewModel.Progress.Should().Be(0.5);

        harness.ReleaseSave.TrySetResult();
        await Task.WhenAll(priorWriter, back.DepartureCheckpoint)
            .WaitAsync(TimeSpan.FromSeconds(2), ct);

        harness.Statistics.Checkpoints.Should().Contain(
            (150, ReaderStatisticsCheckpointReason.ProgrammaticDeparture));
        var settlement = await harness.ViewModel.HandleNavigationBridgeErrorAsync();
        settlement.Should().NotBeNull();
        harness.Input.ApplyNavigationSettlement(settlement!).Should().BeTrue();
        harness.Input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        harness.Input.ForwardTarget.Should().BeNull();
        harness.ViewModel.AcknowledgeNavigationRendered(settlement!.Generation)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(HistoryRecoveryCause.SaveFailure)]
    [InlineData(HistoryRecoveryCause.BridgeError)]
    [InlineData(HistoryRecoveryCause.LifecycleCancel)]
    public async Task HistoryBack_SourceSettlementCausePreservesBackAndForwardStacks(
        HistoryRecoveryCause cause)
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await Harness.CreateAsync(ct);
        await harness.NavigateAndCommitAsync(
            ReaderNavigationInputKind.SearchResult,
            chapterIndex: 1,
            progress: 0.4,
            ct);
        var back = harness.Input.TryGoBack(ct);
        back.Should().NotBeNull();
        await back!.DepartureCheckpoint.WaitAsync(TimeSpan.FromSeconds(2), ct);

        ReaderNavigationSettlement settlement;
        if (cause == HistoryRecoveryCause.SaveFailure)
        {
            harness.FailSaveCall(2);
            var resolution = await harness.ViewModel.ResolveNavigationAsync(
                back.RenderRequest.Generation,
                back.RenderRequest.Destination.ChapterIndex,
                back.RenderRequest.Destination.ExactProgress!.Value,
                ct);
            resolution.Disposition.Should().Be(ReaderNavigationResolutionDisposition.Settled);
            settlement = resolution.Settlement!;
        }
        else
        {
            settlement = (await (cause == HistoryRecoveryCause.BridgeError
                ? harness.ViewModel.HandleNavigationBridgeErrorAsync()
                : harness.ViewModel.SettleNavigationForLifecycleAsync()))!;
        }

        settlement.ShouldRevealDestination.Should().BeFalse();
        harness.Input.ApplyNavigationSettlement(settlement).Should().BeTrue();
        harness.Input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        harness.Input.ForwardTarget.Should().BeNull();
        harness.ViewModel.AcknowledgeNavigationRendered(settlement.Generation)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ActiveTransaction_BlocksEveryPagePositionInputAndSasayakiMutationButKeepsCueUiLive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await Harness.CreateAsync(ct);
        await harness.NavigateAndCommitAsync(
            ReaderNavigationInputKind.SearchResult,
            chapterIndex: 1,
            progress: 0.4,
            ct);
        var savesBeforeBlockedInputs = harness.Saves.Count;
        var active = harness.ViewModel.TryBeginNavigation(
            0,
            restoreTarget: null,
            exactProgress: 0.8);
        active.Should().NotBeNull();

        var internalLink = harness.Input.TryNavigate(
            ReaderNavigationInputKind.InternalLink,
            0,
            0.1,
            fragment: "target",
            ct: ct);
        var search = harness.Input.TryNavigate(
            ReaderNavigationInputKind.SearchResult,
            0,
            0.2,
            ct: ct);
        var toc = harness.Input.TryNavigate(
            ReaderNavigationInputKind.TableOfContents,
            0,
            0,
            ct: ct);
        var history = harness.Input.TryGoBack(ct);
        var paginateCalls = 0;
        var pageTurnAccepted = await harness.Input.TryExecutePageTurnAsync(
            "forward",
            _ =>
            {
                paginateCalls++;
                return Task.CompletedTask;
            });

        var playbackUpdates = 0;
        var cueUpdates = 0;
        var highlights = 0;
        var highlightAllowedAutoScroll = true;
        var chapterLoads = 0;
        var clears = 0;
        playbackUpdates++;
        cueUpdates++;
        harness.Input.DispatchLiveSasayakiCue(
            sameChapter: true,
            autoScrollEnabled: true,
            allowAutoScroll =>
            {
                highlights++;
                highlightAllowedAutoScroll = allowAutoScroll;
            },
            () => chapterLoads++,
            () => clears++);
        harness.Input.DispatchLiveSasayakiCue(
            sameChapter: false,
            autoScrollEnabled: true,
            _ => highlights++,
            () => chapterLoads++,
            () => clears++);
        var positionMutationAccepted = harness.Input.TryApplyPositionMutation(() =>
        {
            harness.ViewModel.SetChapter(0, 2);
            harness.ViewModel.UpdateProgress(0.9);
            harness.ViewModel.SaveProgressDebounced();
        });

        internalLink.Should().BeNull();
        search.Should().BeNull();
        toc.Should().BeNull();
        history.Should().BeNull();
        pageTurnAccepted.Should().BeFalse();
        paginateCalls.Should().Be(0);
        harness.Input.BackTarget.Should().Be(new ReaderNavigationPosition(0, 0.2));
        harness.Input.ForwardTarget.Should().BeNull();
        playbackUpdates.Should().Be(1);
        cueUpdates.Should().Be(1);
        highlights.Should().Be(1);
        highlightAllowedAutoScroll.Should().BeFalse();
        chapterLoads.Should().Be(0);
        clears.Should().Be(1);
        positionMutationAccepted.Should().BeFalse();
        harness.ViewModel.CurrentChapterIndex.Should().Be(1);
        harness.ViewModel.Progress.Should().Be(0.4);
        harness.Saves.Count.Should().Be(savesBeforeBlockedInputs);

        var settlement = await harness.ViewModel.HandleNavigationBridgeErrorAsync();
        settlement.Should().NotBeNull();
        harness.ViewModel.AcknowledgeNavigationRendered(settlement!.Generation)
            .Should().BeTrue();
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempBookDirectory _temp = new();
        private int _saveCall;
        private int _blockedSaveCall = -1;
        private int _failedSaveCall = -1;

        private Harness()
        {
            var novelService = new Mock<INovelLibraryService>();
            novelService.Setup(service => service.GetNovelBookAsync(
                    "book-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
                {
                    Id = "book-1",
                    Title = "Book One",
                    FilePath = Path.Combine(_temp.Path, "book.epub"),
                    ExtractedPath = _temp.Path,
                }));
            novelService.Setup(service => service.MarkOpenedAsync(
                    "book-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());
            novelService.Setup(service => service.SaveProgressAsync(
                    "book-1",
                    It.IsAny<int>(),
                    It.IsAny<double>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (
                    string _,
                    int chapter,
                    double progress,
                    int position,
                    int _,
                    CancellationToken _) =>
                {
                    Saves.Enqueue((chapter, progress, position));
                    var call = Interlocked.Increment(ref _saveCall);
                    if (call == Volatile.Read(ref _blockedSaveCall))
                    {
                        SaveStarted.TrySetResult();
                        await ReleaseSave.Task;
                    }

                    if (call == Volatile.Read(ref _failedSaveCall))
                        return Result.Failure("write failed", "Bookmark");

                    return Result.Success();
                });
            var settings = new Mock<ISettingsService>();
            settings.SetupGet(service => service.Current).Returns(new AppSettings());
            var autoSync = new Mock<IReaderAutoSyncCoordinator>();
            autoSync.Setup(service => service.ImportOnOpenAsync(
                    It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var profileRuntime = new Mock<IProfileRuntimeService>();
            profileRuntime.SetupGet(service => service.ActiveLanguage)
                .Returns(ContentLanguageProfile.Japanese);
            profileRuntime.Setup(service => service.ActivateForBookAsync(
                    It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Statistics = new FakeReaderStatisticsSession();
            ViewModel = new NovelReaderPageViewModel(
                novelService.Object,
                Mock.Of<INotificationService>(),
                Mock.Of<IMessenger>(),
                new ReaderHighlightService(),
                new NovelBookSidecarService(),
                Statistics,
                profileRuntime.Object,
                settings.Object,
                autoSync.Object,
                new ReaderNavigationTransactionCoordinator());
            Input = new ReaderNavigationInputCoordinator(
                () => ViewModel.CanAcceptReaderPositionMutation,
                ViewModel.TryReserveProgrammaticNavigation);
        }

        public NovelReaderPageViewModel ViewModel { get; }
        public ReaderNavigationInputCoordinator Input { get; }
        public FakeReaderStatisticsSession Statistics { get; }
        public ConcurrentQueue<(int Chapter, double Progress, int Position)> Saves { get; } = [];
        public TaskCompletionSource SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Harness> CreateAsync(CancellationToken ct)
        {
            var harness = new Harness();
            await harness.ViewModel.InitializeAsync(
                new NovelReaderNavigationArgs("book-1"),
                ct);
            harness.ViewModel.SetChapterCharacterCounts([100, 100]);
            harness.ViewModel.SetChapter(0, 2);
            harness.ViewModel.UpdateProgress(0.2);
            return harness;
        }

        public void BlockSaveCall(int call) =>
            Volatile.Write(ref _blockedSaveCall, call);

        public void FailSaveCall(int call) =>
            Volatile.Write(ref _failedSaveCall, call);

        public async Task NavigateAndCommitAsync(
            ReaderNavigationInputKind kind,
            int chapterIndex,
            double progress,
            CancellationToken ct)
        {
            var input = Input.TryNavigate(
                kind,
                chapterIndex,
                progress,
                ct: ct);
            input.Should().NotBeNull();
            await input!.DepartureCheckpoint.WaitAsync(TimeSpan.FromSeconds(2), ct);
            var resolution = await ViewModel.ResolveNavigationAsync(
                input.RenderRequest.Generation,
                chapterIndex,
                progress,
                ct);
            resolution.Disposition.Should().Be(ReaderNavigationResolutionDisposition.Settled);
            Input.ApplyNavigationSettlement(resolution.Settlement!).Should().BeTrue();
            ViewModel.AcknowledgeNavigationRendered(input.RenderRequest.Generation)
                .Should().BeTrue();
        }

        public void Dispose()
        {
            ReleaseSave.TrySetResult();
            _temp.Dispose();
        }
    }

    private static ReaderNavigationInputCoordinator CreateInput(
        Func<ReaderNavigationPositionSnapshot> current,
        Func<long> nextGeneration) =>
        new(
            () => true,
            (chapter, restore, progress, _) =>
            {
                var source = current();
                var destination = new ReaderNavigationDestination(
                    chapter,
                    restore,
                    progress);
                return new ReaderProgrammaticNavigationReservation(
                    new ReaderNavigationRenderRequest(
                        nextGeneration(),
                        source,
                        destination),
                    Task.CompletedTask);
            });

    private static ReaderNavigationPositionSnapshot Snapshot(
        int chapter,
        double progress,
        long revision) =>
        new("book-1", chapter, progress, (chapter * 100) + (int)(progress * 100), 200, revision);

    public enum HistoryRecoveryCause
    {
        SaveFailure,
        BridgeError,
        LifecycleCancel,
    }

    private sealed class FakeReaderStatisticsSession : IReaderStatisticsSession
    {
        private static readonly NovelReadingStatistic Empty = ReaderStatisticsMath.Empty(
            "Book One",
            new DateOnly(2026, 7, 13));

        public ReaderStatisticsSessionState State { get; private set; } = new(
            false,
            false,
            Empty,
            Empty,
            Empty,
            []);
        public List<(int Position, ReaderStatisticsCheckpointReason Reason)> Checkpoints { get; } = [];
        public event EventHandler<ReaderStatisticsSessionState>? StateChanged;

        public Task LoadAsync(
            string bookRoot,
            string title,
            ReaderStatisticsPosition position,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Start(ReaderStatisticsPosition position)
        {
            State = State with { IsTracking = true, IsPaused = false };
            StateChanged?.Invoke(this, State);
        }

        public void Tick(ReaderStatisticsPosition position)
        {
        }

        public Task CheckpointAsync(
            ReaderStatisticsPosition position,
            ReaderStatisticsCheckpointReason reason,
            CancellationToken ct = default)
        {
            Checkpoints.Add((position.RawCharacterCount, reason));
            return Task.CompletedTask;
        }

        public Task PauseAsync(
            ReaderStatisticsPosition position,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(
            ReaderStatisticsPosition position,
            CancellationToken ct = default)
        {
            State = State with { IsTracking = false, IsPaused = false };
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void ResetBaseline(ReaderStatisticsPosition position)
        {
        }
    }

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
