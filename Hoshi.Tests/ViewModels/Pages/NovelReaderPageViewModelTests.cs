using System.Collections.Concurrent;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Profiles;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Profiles;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;
using Hoshi.Tests.TestUtils;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public sealed class NovelReaderPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_WhenOpenSyncImports_ReloadsBookBeforeRestore()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new List<string>();
        var local = new NovelBook
        {
            Id = "book-1",
            Title = "Local",
            Progress = 0.10,
            CurrentChapterIndex = 0,
        };
        var imported = new NovelBook
        {
            Id = "book-1",
            Title = "Imported",
            Progress = 0.65,
            CurrentChapterIndex = 2,
        };
        var library = new Mock<INovelLibraryService>();
        var loadCount = 0;
        library.Setup(service => service.GetNovelBookAsync(
                "book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                loadCount++;
                events.Add(loadCount == 1 ? "load-local" : "load-imported");
                return Result<NovelBook?>.Success(loadCount == 1 ? local : imported);
            });
        library.Setup(service => service.MarkOpenedAsync(
                "book-1", It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("mark-opened"))
            .ReturnsAsync(Result.Success());
        var autoSync = CreateAutoSyncCoordinator(imported: true);
        autoSync.Setup(service => service.ImportOnOpenAsync(
                local, It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("import-open"))
            .ReturnsAsync(true);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var sut = new NovelReaderPageViewModel(
            library.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new ReaderHighlightService(),
            new NovelBookSidecarService(),
            new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService(
                ContentLanguageProfile.Japanese,
                _ => events.Add("activate-profile")),
            settings.Object,
            autoSync.Object);

        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        sut.CurrentBook.Should().BeSameAs(imported);
        sut.ReaderTitle.Should().Be("Imported");
        library.Verify(service => service.GetNovelBookAsync(
            "book-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
        autoSync.Verify(service => service.ImportOnOpenAsync(
            local, It.IsAny<CancellationToken>()), Times.Once);
        events.Should().Equal(
            "load-local",
            "activate-profile",
            "import-open",
            "load-imported",
            "mark-opened");
    }

    [Fact]
    public async Task LoadHighlightsAsync_LoadsBookSidecarAndSerializesOnlyCurrentChapterHighlights()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var highlightService = new ReaderHighlightService();
        var priorChapter = HighlightAt(character: 9, offset: 9);
        var currentChapter = HighlightAt(character: 10, offset: 0);
        await highlightService.SaveAsync(temp.Path, [priorChapter, currentChapter], ct);

        var novelService = new Mock<INovelLibraryService>();
        novelService
            .Setup(s => s.GetNovelBookAsync("book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "Book One",
                FilePath = System.IO.Path.Combine(temp.Path, "book.epub"),
                ExtractedPath = temp.Path,
            }));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());

        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            highlightService,
            new NovelBookSidecarService(),
            new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService(ContentLanguageProfile.Japanese),
            settings.Object,
            CreateAutoSyncCoordinator().Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([10, 20]);
        sut.SetChapter(1, count: 2);

        await sut.LoadHighlightsAsync(ct);
        var json = sut.GetCurrentChapterHighlightsJson();

        sut.Highlights.Should().HaveCount(2);
        json.Should().NotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(json!);
        var item = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetString().Should().Be(currentChapter.Id.ToString("D"));
        item.GetProperty("character").GetInt32().Should().Be(10);
        item.GetProperty("offset").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DeleteHighlightAsync_RemovesHighlightFromSidecarAndListRows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var highlightService = new ReaderHighlightService();
        var first = HighlightAt(character: 5, offset: 5);
        var second = HighlightAt(character: 15, offset: 5);
        await highlightService.SaveAsync(temp.Path, [second, first], ct);
        var sut = CreateInitializedSut(temp.Path, highlightService);
        sut.SetChapterCharacterCounts([10, 20]);
        await sut.LoadHighlightsAsync(ct);

        var rows = sut.GetHighlightListItems(["One", "Two"]);
        rows.Select(row => row.ChapterLabel).Should().Equal("One", "Two");
        rows.Select(row => row.Highlight.Id).Should().Equal(first.Id, second.Id);
        rows[1].JumpTarget.Should().Be(new ReaderHighlightJumpTarget(1, 0.25));

        var deleted = await sut.DeleteHighlightAsync(first.Id, ct);

        deleted.Should().BeTrue();
        sut.Highlights.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        var loaded = await highlightService.LoadAsync(temp.Path, ct);
        loaded.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        sut.GetHighlightListItems(["One", "Two"])
            .Should()
            .ContainSingle()
            .Which.ChapterLabel.Should().Be("Two");
    }

    [Theory]
    [InlineData(ReaderHighlightColor.Yellow, 239, 209, 56)]
    [InlineData(ReaderHighlightColor.Green, 152, 220, 129)]
    [InlineData(ReaderHighlightColor.Blue, 149, 185, 255)]
    [InlineData(ReaderHighlightColor.Pink, 255, 155, 180)]
    [InlineData(ReaderHighlightColor.Purple, 197, 175, 251)]
    public void HighlightListItem_ExposesMacAlignedSwatchColor(
        ReaderHighlightColor highlightColor,
        byte red,
        byte green,
        byte blue)
    {
        var highlight = HighlightAt(
            character: 42,
            offset: 2,
            color: highlightColor);
        var item = new ReaderHighlightListItem(
            highlight,
            new ReaderHighlightJumpTarget(0, 0.42),
            "Chapter 1");

        item.SwatchColor.Should().Be(Windows.UI.Color.FromArgb(255, red, green, blue));
    }

    [Fact]
    public async Task SaveProgressNowAsync_DelegatesCanonicalBookmarkWriteOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecarService = new NovelBookSidecarService();
        var novelService = new Mock<INovelLibraryService>();
        novelService
            .Setup(s => s.GetNovelBookAsync("book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "Book One",
                FilePath = System.IO.Path.Combine(temp.Path, "book.epub"),
                ExtractedPath = temp.Path,
            }));
        novelService
            .Setup(s => s.SaveProgressAsync(
                "book-1",
                1,
                0.5,
                125,
                150,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var events = new List<string>();
        novelService.Setup(s => s.SaveProgressAsync(
                "book-1", 1, 0.5, 125, 150, It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("save-bookmark"))
            .ReturnsAsync(Result.Success());
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => events.Add("schedule-export"));
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new ReaderHighlightService(),
            sidecarService,
            new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService(ContentLanguageProfile.Japanese),
            settings.Object,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100, 50]);
        sut.SetChapter(1, count: 2);
        sut.UpdateProgress(0.5);

        await sut.SaveProgressNowAsync(ct: ct);

        (await sidecarService.LoadBookmarkAsync(temp.Path, ct)).Should().BeNull();
        novelService.Verify(s => s.SaveProgressAsync(
            "book-1",
            1,
            0.5,
            125,
            150,
            It.IsAny<CancellationToken>()), Times.Once);
        novelService.VerifyAll();
        events.Should().Equal("save-bookmark", "schedule-export");
    }

    [Fact]
    public async Task SaveProgressNowAsync_WhenBookmarkWriteFails_DoesNotScheduleOrBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1", 0, 0, 0, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("write failed", "Bookmark"));
        var notifications = new Mock<INotificationService>();
        var messenger = new FakeMessenger();
        var autoSync = CreateAutoSyncCoordinator();
        var sut = CreateSut(
            novelService.Object,
            notifications.Object,
            messenger,
            new FakeReaderStatisticsSession(),
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        await sut.SaveProgressNowAsync(flushStatistics: false, ct: ct);

        notifications.Verify(service => service.ShowError("write failed", "Bookmark"), Times.Once);
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Never);
        messenger.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveProgressDebounced_WhenBookmarkWriteSucceeds_BroadcastsOnCallingContext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new List<string>();
        var broadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1", 0, 0, 0, 0, It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("save-bookmark"))
            .ReturnsAsync(Result.Success());
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => events.Add("schedule-export"));
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        SynchronizationContext? broadcastContext = null;
        messenger.Register<NovelLibraryChangedMessage>(recipient, (_, _) =>
        {
            events.Add("broadcast-library-change");
            broadcastContext = SynchronizationContext.Current;
            broadcast.TrySetResult();
        });
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            new FakeReaderStatisticsSession(),
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var callingContext = new InlineRecordingSynchronizationContext();
        RunWithSynchronizationContext(callingContext, sut.SaveProgressDebounced);
        await broadcast.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        events.Should().Equal(
            "save-bookmark",
            "schedule-export",
            "broadcast-library-change");
        broadcastContext.Should().BeSameAs(callingContext);
        callingContext.PostCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SaveProgressDebounced_WhenBookmarkWriteFails_NotifiesOnCallingContext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1", 0, 0, 0, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("write failed", "Bookmark"));
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new Mock<INotificationService>();
        SynchronizationContext? notificationContext = null;
        notifications.Setup(service => service.ShowError("write failed", "Bookmark"))
            .Callback(() =>
            {
                notificationContext = SynchronizationContext.Current;
                notified.TrySetResult();
            });
        var messenger = new FakeMessenger();
        var autoSync = CreateAutoSyncCoordinator();
        var sut = CreateSut(
            novelService.Object,
            notifications.Object,
            messenger,
            new FakeReaderStatisticsSession(),
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var callingContext = new InlineRecordingSynchronizationContext();
        RunWithSynchronizationContext(callingContext, sut.SaveProgressDebounced);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        notificationContext.Should().BeSameAs(callingContext);
        messenger.SentMessages.Should().BeEmpty();
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Never);
    }

    [Fact]
    public async Task SaveBookInfoSidecarAsync_WritesBookInfoFromCurrentChapterCharacterCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecarService = new NovelBookSidecarService();
        var sut = CreateInitializedSut(temp.Path, new ReaderHighlightService(), sidecarService);
        var containerDirectory = System.IO.Path.Combine(temp.Path, "OEBPS");
        sut.SetChapterCharacterCounts([100, 50]);

        await sut.SaveBookInfoSidecarAsync(
            [
                new EpubChapter
                {
                    Href = System.IO.Path.Combine(containerDirectory, "chapter1.xhtml"),
                    SpineIndex = 0,
                },
                new EpubChapter
                {
                    Href = System.IO.Path.Combine(containerDirectory, "chapter2.xhtml"),
                    SpineIndex = 1,
                },
            ],
            containerDirectory,
            ct);

        var bookInfo = await sidecarService.LoadBookInfoAsync(temp.Path, ct);
        bookInfo.Should().NotBeNull();
        bookInfo!.CharacterCount.Should().Be(150);
        bookInfo.ChapterInfo["chapter2.xhtml"]
            .Should()
            .Be(new NovelBookInfoChapter(SpineIndex: 1, CurrentTotal: 100, ChapterCount: 50));
    }

    [Fact]
    public async Task StatisticsSessionState_ProjectsSessionTodayAndAllTimeText()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession);
        sut.SetChapterCharacterCounts([100, 100]);
        sut.SetChapter(0, count: 2);
        sut.UpdateProgress(0.25);
        await sut.LoadStatisticsAsync(ct);

        statisticsSession.Publish(new ReaderStatisticsSessionState(
            IsTracking: true,
            IsPaused: false,
            Statistic(60, 120, 1_800),
            Statistic(70, 180, 1_400),
            Statistic(600, 1_200, 1_800),
            []));

        sut.SessionStatistics.CharactersRead.Should().Be(60);
        sut.SessionStatistics.ReadingTime.Should().Be(120);
        sut.SessionStatistics.LastReadingSpeed.Should().Be(1_800);
        sut.TodaysStatistics.CharactersRead.Should().Be(70);
        sut.AllTimeStatistics.CharactersRead.Should().Be(600);
        sut.IsStatisticsTracking.Should().BeTrue();
        sut.IsStatisticsPaused.Should().BeFalse();
        sut.StatisticsSessionCharactersText.Should().Be("60");
        sut.StatisticsSessionSpeedText.Should().Be("1,800 / h");
        sut.StatisticsSessionTimeText.Should().Be("2m 0s");
        sut.StatisticsSessionChromeTimeText.Should().Be("0:02");
        sut.StatisticsTodayTimeText.Should().Be("3m 0s");
        sut.StatisticsAllTimeTimeText.Should().Be("20m 0s");
    }

    [Theory]
    [InlineData("ja", false, "11", "1,805 / h")]
    [InlineData("en", true, "3", "361 / h")]
    public async Task StatisticsPanel_UsesActiveContentLanguageUnits(
        string languageId,
        bool expectedEnglish,
        string expectedCount,
        string expectedSpeed)
    {
        using var temp = new TempBookDirectory();
        var statistics = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            language: ContentLanguageProfile.FromId(languageId),
            statisticsSession: statistics);
        await sut.LoadStatisticsAsync(TestContext.Current.CancellationToken);

        statistics.Publish(new ReaderStatisticsSessionState(
            true,
            false,
            Statistic(11, 60, 1_805),
            Statistic(11, 60, 1_805),
            Statistic(11, 60, 1_805),
            []));

        sut.IsEnglishStatisticsContent.Should().Be(expectedEnglish);
        sut.StatisticsSessionCharactersText.Should().Be(expectedCount);
        sut.StatisticsSessionSpeedText.Should().Be(expectedSpeed);
    }

    [Fact]
    public async Task StatisticsPanel_RemainingTimeUsesRawCharactersAndRawSpeed()
    {
        using var temp = new TempBookDirectory();
        var statistics = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            language: ContentLanguageProfile.English,
            statisticsSession: statistics);
        sut.SetChapterCharacterCounts([11]);
        sut.SetChapter(0, count: 1);
        sut.UpdateProgress(0.1);
        await sut.LoadStatisticsAsync(TestContext.Current.CancellationToken);

        statistics.Publish(new ReaderStatisticsSessionState(
            true,
            false,
            Statistic(1, 60, 11),
            Statistic(1, 60, 11),
            Statistic(1, 60, 11),
            []));

        sut.StatisticsBookRemainingTimeText.Should().Be("54m 32s");
        sut.StatisticsChapterRemainingTimeText.Should().Be("54m 32s");
    }

    [Fact]
    public async Task LifecycleCheckpoints_SaveThenCheckpointFlushAndCancelCloseExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = new Mock<INovelLibraryService>();
        novelService
            .Setup(s => s.GetNovelBookAsync("book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "Book One",
                FilePath = System.IO.Path.Combine(temp.Path, "book.epub"),
                ExtractedPath = temp.Path,
            }));
        var events = new List<string>();
        novelService.Setup(s => s.SaveProgressAsync(
                "book-1", 0, 0.5, 50, 100, It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("save-bookmark"))
            .ReturnsAsync(Result.Success());
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var statisticsSession = new FakeReaderStatisticsSession();
        statisticsSession.CheckpointRecorded = reason => events.Add($"checkpoint-{reason}");
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => events.Add("schedule-export"));
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("flush-export"))
            .Returns(Task.CompletedTask);
        autoSync.Setup(service => service.Cancel())
            .Callback(() => events.Add("cancel"));
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new ReaderHighlightService(),
            new NovelBookSidecarService(),
            statisticsSession,
            new NoOpProfileRuntimeService(ContentLanguageProfile.Japanese),
            settings.Object,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.5);

        await sut.CheckpointAppBackgroundingAsync(ct);
        autoSync.Verify(service => service.Cancel(), Times.Never);
        await Task.WhenAll(
            sut.PrepareForReaderLifecycleCloseAsync(ct),
            sut.PrepareForReaderLifecycleCloseAsync(ct));

        novelService.Verify(s => s.SaveProgressAsync(
            "book-1", 0, 0.5, 50, 100, It.IsAny<CancellationToken>()), Times.Exactly(2));
        statisticsSession.Checkpoints.Should().Equal(
            (50, ReaderStatisticsCheckpointReason.Background),
            (50, ReaderStatisticsCheckpointReason.Close));
        events.Should().Equal(
            "save-bookmark",
            "checkpoint-Background",
            "schedule-export",
            "flush-export",
            "save-bookmark",
            "checkpoint-Close",
            "schedule-export",
            "flush-export",
            "cancel");
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Exactly(2));
        autoSync.Verify(service => service.FlushAsync(
            It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleBoundary_WaitsForInFlightDebounceAndSuppressesItsStaleEffects(
        bool close)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new List<string>();
        var debounceSaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDebounceSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, currentCharacterCount, totalCharacterCount, saveToken);
                if (Interlocked.Increment(ref saveCount) == 1)
                {
                    events.Add("debounce-save-start");
                    debounceSaveStarted.TrySetResult();
                    await releaseDebounceSave.Task;
                    events.Add("debounce-save-end");
                    return Result.Success();
                }

                events.Add("final-save");
                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession
        {
            CheckpointRecorded = reason => events.Add($"checkpoint-{reason}"),
        };
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => events.Add("schedule-export"));
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("flush-export"))
            .Returns(Task.CompletedTask);
        autoSync.Setup(service => service.Cancel())
            .Callback(() => events.Add("cancel"));
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.5);

        sut.SaveProgressDebounced();
        await debounceSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var boundary = close
            ? sut.PrepareForReaderLifecycleCloseAsync(ct)
            : sut.CheckpointAppBackgroundingAsync(ct);

        boundary.IsCompleted.Should().BeFalse();
        Volatile.Read(ref saveCount).Should().Be(1);
        releaseDebounceSave.TrySetResult();
        await boundary.WaitAsync(TimeSpan.FromSeconds(2), ct);

        var reason = close
            ? ReaderStatisticsCheckpointReason.Close
            : ReaderStatisticsCheckpointReason.Background;
        statistics.Checkpoints.Should().Equal((50, reason));
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().ContainSingle();
        events.Should().Equal(close
            ? [
                "debounce-save-start",
                "debounce-save-end",
                "final-save",
                "checkpoint-Close",
                "schedule-export",
                "flush-export",
                "cancel",
            ]
            : [
                "debounce-save-start",
                "debounce-save-end",
                "final-save",
                "checkpoint-Background",
                "schedule-export",
                "flush-export",
            ]);
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Once);
        autoSync.Verify(service => service.Cancel(), close ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task LifecycleClose_RejectsDebounceWhileWaitingForPriorDebounce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var firstSaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, currentCharacterCount, totalCharacterCount, saveToken);
                var call = Interlocked.Increment(ref saveCount);
                if (call == 1)
                {
                    firstSaveStarted.TrySetResult();
                    await releaseFirstSave.Task;
                }
                else if (call > 2)
                {
                    unexpectedSave.TrySetResult();
                }

                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession();
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        sut.SaveProgressDebounced();
        await firstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var close = sut.PrepareForReaderLifecycleCloseAsync(ct);
        close.IsCompleted.Should().BeFalse();

        sut.SaveProgressDebounced();
        releaseFirstSave.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await Task.Delay(650, ct);

        Volatile.Read(ref saveCount).Should().Be(2);
        unexpectedSave.Task.IsCompleted.Should().BeFalse();
        statistics.Checkpoints.Should().Equal((0, ReaderStatisticsCheckpointReason.Close));
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().ContainSingle();
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Once);
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleBoundary_WaitsForInFlightImmediateBeforeFinalSave(bool close)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var immediateStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImmediate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, currentCharacterCount, totalCharacterCount, saveToken);
                if (Interlocked.Increment(ref saveCount) == 1)
                {
                    events.Enqueue("immediate-start");
                    immediateStarted.TrySetResult();
                    await releaseImmediate.Task;
                    events.Enqueue("immediate-end");
                }
                else
                {
                    events.Enqueue("final-save");
                }

                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession
        {
            CheckpointRecorded = reason => events.Enqueue($"checkpoint-{reason}"),
        };
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => events.Enqueue("schedule-export"));
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue("flush-export"))
            .Returns(Task.CompletedTask);
        autoSync.Setup(service => service.Cancel())
            .Callback(() => events.Enqueue("cancel"));
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var immediate = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await immediateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var boundary = close
            ? sut.PrepareForReaderLifecycleCloseAsync(ct)
            : sut.CheckpointAppBackgroundingAsync(ct);

        boundary.IsCompleted.Should().BeFalse();
        Volatile.Read(ref saveCount).Should().Be(1);
        releaseImmediate.TrySetResult();
        await Task.WhenAll(immediate, boundary).WaitAsync(TimeSpan.FromSeconds(2), ct);

        var reason = close ? "Close" : "Background";
        events.Should().Equal(close
            ? [
                "immediate-start",
                "immediate-end",
                "final-save",
                $"checkpoint-{reason}",
                "schedule-export",
                "flush-export",
                "cancel",
            ]
            : [
                "immediate-start",
                "immediate-end",
                "final-save",
                $"checkpoint-{reason}",
                "schedule-export",
                "flush-export",
            ]);
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().HaveCount(2);
    }

    [Fact]
    public async Task LifecycleClose_RejectsImmediateAndDebouncedWritersAfterBarrier()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref saveCount))
            .ReturnsAsync(Result.Success());
        var flushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                flushStarted.TrySetResult();
                return releaseFlush.Task;
            });
        var statistics = new FakeReaderStatisticsSession();
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var close = sut.PrepareForReaderLifecycleCloseAsync(ct);
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        await sut.SaveProgressNowAsync(ct: ct);
        sut.SaveProgressDebounced();
        releaseFlush.TrySetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(2), ct);

        await sut.SaveProgressNowAsync(ct: ct);
        sut.SaveProgressDebounced();
        await Task.Delay(650, ct);

        Volatile.Read(ref saveCount).Should().Be(1);
        statistics.Checkpoints.Should().Equal((0, ReaderStatisticsCheckpointReason.Close));
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().ContainSingle();
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Once);
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Fact]
    public async Task LifecycleBackground_RestoresAdmissionForLaterSaveAndClose()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref saveCount))
            .ReturnsAsync(Result.Success());
        var backgroundFlushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackgroundFlush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var flushCount = 0;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref flushCount) == 1)
                {
                    backgroundFlushStarted.TrySetResult();
                    return releaseBackgroundFlush.Task;
                }

                return Task.CompletedTask;
            });
        var statistics = new FakeReaderStatisticsSession();
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var background = sut.CheckpointAppBackgroundingAsync(ct);
        await backgroundFlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await sut.SaveProgressNowAsync(flushStatistics: false, ct: ct);
        releaseBackgroundFlush.TrySetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(2), ct);

        await sut.SaveProgressNowAsync(flushStatistics: false, ct: ct);
        await sut.PrepareForReaderLifecycleCloseAsync(ct);

        Volatile.Read(ref saveCount).Should().Be(3);
        statistics.Checkpoints.Should().Equal(
            (0, ReaderStatisticsCheckpointReason.Background),
            (0, ReaderStatisticsCheckpointReason.Close));
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().HaveCount(3);
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Exactly(3));
        autoSync.Verify(service => service.FlushAsync(
            It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Fact]
    public async Task SaveProgressNowAsync_ConcurrentWritersRunInAdmissionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, currentCharacterCount, totalCharacterCount, saveToken);
                var label = progress.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                events.Enqueue($"start-{label}");
                if (progress == 0.1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                events.Enqueue($"end-{label}");
                return Result.Success();
            });
        var messenger = new FakeMessenger();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            new FakeReaderStatisticsSession(),
            CreateAutoSyncCoordinator().Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        sut.UpdateProgress(0.1);
        var first = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.UpdateProgress(0.2);
        var second = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        sut.UpdateProgress(0.3);
        var third = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);

        events.Should().Equal("start-0.1");
        second.IsCompleted.Should().BeFalse();
        third.IsCompleted.Should().BeFalse();

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2), ct);

        events.Should().Equal(
            "start-0.1",
            "end-0.1",
            "start-0.2",
            "end-0.2",
            "start-0.3",
            "end-0.3");
        messenger.SentMessages.OfType<NovelLibraryChangedMessage>().Should().HaveCount(3);
    }

    [Fact]
    public async Task ManualNavigation_QueuedBehindWriter_CheckpointsAdmittedProgressSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var savedPositions = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, totalCharacterCount, saveToken);
                savedPositions.Enqueue(currentCharacterCount);
                if (Interlocked.Increment(ref saveCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            CreateAutoSyncCoordinator().Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);

        sut.UpdateProgress(0.1);
        var first = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var manual = sut.HandleManualPageNavigationAsync(
            new ReaderPageNavigationEvent(
                ReaderPageNavigationResult.Scrolled,
                ReaderPageNavigationDirection.Forward,
                0.4),
            ct);
        sut.UpdateProgress(0.8);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, manual).WaitAsync(TimeSpan.FromSeconds(2), ct);

        savedPositions.Should().Equal(10, 40);
        statistics.Checkpoints.Should().Equal(
            (40, ReaderStatisticsCheckpointReason.ReadingMovement));
    }

    [Fact]
    public async Task SaveProgressNowAsync_DefaultFlush_CheckpointsAdmittedProgressSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var savedPositions = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, totalCharacterCount, saveToken);
                savedPositions.Enqueue(currentCharacterCount);
                if (Interlocked.Increment(ref saveCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            CreateAutoSyncCoordinator().Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);

        sut.UpdateProgress(0.1);
        var first = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.UpdateProgress(0.5);
        var second = sut.SaveProgressNowAsync(ct: ct);
        sut.UpdateProgress(0.9);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2), ct);

        savedPositions.Should().Equal(10, 50);
        statistics.Checkpoints.Should().Equal(
            (50, ReaderStatisticsCheckpointReason.ReadingMovement));
    }

    [Fact]
    public async Task SaveProgressDebounced_CheckpointsAdmittedProgressSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var savedPositions = new ConcurrentQueue<int>();
        var scheduled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback((
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, totalCharacterCount, saveToken);
                savedPositions.Enqueue(currentCharacterCount);
            })
            .ReturnsAsync(Result.Success());
        var statistics = new FakeReaderStatisticsSession();
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.ScheduleExport(It.IsAny<NovelBook>()))
            .Callback(() => scheduled.TrySetResult());
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);

        sut.UpdateProgress(0.3);
        sut.SaveProgressDebounced();
        sut.UpdateProgress(0.8);
        await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        savedPositions.Should().Equal(30);
        statistics.Checkpoints.Should().Equal(
            (30, ReaderStatisticsCheckpointReason.ReadingMovement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleFinalFlush_UsesAdmittedProgressSnapshot(bool close)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalBookmarkPosition = 0;
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, progress, totalCharacterCount, saveToken);
                Volatile.Write(ref finalBookmarkPosition, currentCharacterCount);
                if (Interlocked.Increment(ref saveCount) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                return Result.Success();
            });
        var statistics = new FakeReaderStatisticsSession();
        (int Bookmark, int Statistics)? flushSnapshot = null;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() => flushSnapshot = (
                Volatile.Read(ref finalBookmarkPosition),
                statistics.Checkpoints.Last().Position))
            .Returns(Task.CompletedTask);
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);

        sut.UpdateProgress(0.1);
        var first = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.UpdateProgress(0.4);
        var lifecycle = close
            ? sut.PrepareForReaderLifecycleCloseAsync(ct)
            : sut.CheckpointAppBackgroundingAsync(ct);
        sut.UpdateProgress(0.9);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, lifecycle).WaitAsync(TimeSpan.FromSeconds(2), ct);

        Volatile.Read(ref finalBookmarkPosition).Should().Be(40);
        statistics.Checkpoints.Should().Equal((
            40,
            close
                ? ReaderStatisticsCheckpointReason.Close
                : ReaderStatisticsCheckpointReason.Background));
        flushSnapshot.Should().Be((40, 40));
    }

    [Fact]
    public async Task SaveProgressNowAsync_ReentrantMessengerWriterKeepsTailOwnership()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var reentrantStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReentrant = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                string bookId,
                int chapterIndex,
                double progress,
                int currentCharacterCount,
                int totalCharacterCount,
                CancellationToken saveToken) =>
            {
                _ = (bookId, chapterIndex, currentCharacterCount, totalCharacterCount, saveToken);
                var label = progress.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                events.Enqueue($"start-{label}");
                if (progress == 0.2)
                {
                    reentrantStarted.TrySetResult();
                    await releaseReentrant.Task;
                }
                events.Enqueue($"end-{label}");
                return Result.Success();
            });
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        NovelReaderPageViewModel? sut = null;
        Task? reentrant = null;
        var didReenter = false;
        messenger.Register<NovelLibraryChangedMessage>(recipient, (_, _) =>
        {
            if (didReenter)
                return;

            didReenter = true;
            sut!.UpdateProgress(0.2);
            reentrant = sut.SaveProgressNowAsync(
                flushStatistics: false,
                scheduleAutoSync: false,
                ct: ct);
        });
        sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            new FakeReaderStatisticsSession(),
            CreateAutoSyncCoordinator().Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        sut.UpdateProgress(0.1);
        await sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);
        await reentrantStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        sut.UpdateProgress(0.3);
        var third = sut.SaveProgressNowAsync(
            flushStatistics: false,
            scheduleAutoSync: false,
            ct: ct);

        events.Should().Equal(
            "start-0.1",
            "end-0.1",
            "start-0.2");
        third.IsCompleted.Should().BeFalse();

        releaseReentrant.TrySetResult();
        await Task.WhenAll(reentrant!, third).WaitAsync(TimeSpan.FromSeconds(2), ct);

        events.Should().Equal(
            "start-0.1",
            "end-0.1",
            "start-0.2",
            "end-0.2",
            "start-0.3",
            "end-0.3");
    }

    [Fact]
    public async Task LifecycleClose_CallerCancellationDoesNotCancelSharedInFlightClose()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var flushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns((NovelBook book, CancellationToken flushToken) =>
            {
                _ = book;
                flushStarted.TrySetResult();
                return releaseFlush.Task.WaitAsync(flushToken);
            });
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new FakeReaderStatisticsSession(),
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        using var callerCancellation = new CancellationTokenSource();

        var cancelledWait = sut.PrepareForReaderLifecycleCloseAsync(callerCancellation.Token);
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var sharedWait = sut.PrepareForReaderLifecycleCloseAsync(ct);
        callerCancellation.Cancel();

        await cancelledWait.Invoking(task => task)
            .Should().ThrowAsync<OperationCanceledException>();
        sharedWait.IsCompleted.Should().BeFalse();
        autoSync.Verify(service => service.Cancel(), Times.Never);

        releaseFlush.TrySetResult();
        await sharedWait.WaitAsync(TimeSpan.FromSeconds(2), ct);

        novelService.Verify(service => service.SaveProgressAsync(
            "book-1",
            It.IsAny<int>(),
            It.IsAny<double>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        autoSync.Verify(service => service.FlushAsync(
            It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()), Times.Once);
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleClose_WhenFlushFails_CanRetryAndComplete(bool cancelFlush)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var flushCount = 0;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref flushCount) > 1)
                    return Task.CompletedTask;
                return cancelFlush
                    ? Task.FromCanceled(new CancellationToken(canceled: true))
                    : Task.FromException(new InvalidOperationException("flush failed"));
            });
        var statistics = new FakeReaderStatisticsSession();
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var first = sut.PrepareForReaderLifecycleCloseAsync(ct);
        if (cancelFlush)
        {
            await first.Invoking(task => task)
                .Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await first.Invoking(task => task)
                .Should().ThrowAsync<InvalidOperationException>();
        }
        autoSync.Verify(service => service.Cancel(), Times.Never);

        await sut.PrepareForReaderLifecycleCloseAsync(ct);
        await sut.PrepareForReaderLifecycleCloseAsync(ct);

        Volatile.Read(ref flushCount).Should().Be(2);
        statistics.Checkpoints.Should().Equal(
            (0, ReaderStatisticsCheckpointReason.Close),
            (0, ReaderStatisticsCheckpointReason.Close));
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Exactly(2));
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Fact]
    public async Task StatisticsCommands_DelegateCurrentRawPositionAndReason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, count: 1);
        sut.UpdateProgress(0.25);
        await sut.LoadStatisticsAsync(ct);

        sut.StartStatisticsTracking();
        sut.UpdateProgress(0.5);
        await sut.CheckpointReadingAsync(
            ReaderStatisticsCheckpointReason.AdjacentChapter,
            ct);
        sut.ResetStatisticsBaseline();
        await sut.StopStatisticsTrackingAsync(ct: ct);

        statisticsSession.LoadRequest.Should().Be((temp.Path, "Book One", 25));
        statisticsSession.StartPositions.Should().Equal(25);
        statisticsSession.Checkpoints.Should().ContainSingle()
            .Which.Should().Be((50, ReaderStatisticsCheckpointReason.AdjacentChapter));
        statisticsSession.ResetPositions.Should().Equal(50);
        statisticsSession.StopPositions.Should().Equal(50);
    }

    [Fact]
    public async Task ManualSameChapterPageTurn_UpdatesBookmarkAndCheckpointsOnce()
    {
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession,
            statisticsSettings: new NovelStatisticsSettings
            {
                EnableStatistics = true,
                AutostartMode = StatisticsAutostartMode.PageTurn,
            });
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.20);

        var outcome = await sut.HandleManualPageNavigationAsync(
            new(
                ReaderPageNavigationResult.Scrolled,
                ReaderPageNavigationDirection.Forward,
                0.30),
            TestContext.Current.CancellationToken);

        outcome.Should().Be(ReaderPageNavigationOutcome.SameChapterMovement);
        sut.CurrentCharacterCount.Should().Be(30);
        sut.IsStatisticsTracking.Should().BeTrue();
        statisticsSession.Checkpoints.Should().Equal(
            (30, ReaderStatisticsCheckpointReason.ReadingMovement));
    }

    [Fact]
    public async Task ManualAdjacentChapterLimit_CheckpointsDepartureAndRequestsTargetChapter()
    {
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession,
            statisticsSettings: new NovelStatisticsSettings
            {
                EnableStatistics = true,
                AutostartMode = StatisticsAutostartMode.PageTurn,
            });
        sut.SetChapterCharacterCounts([100, 200]);
        sut.SetChapter(0, 2);
        sut.UpdateProgress(0.90);

        var outcome = await sut.HandleManualPageNavigationAsync(
            new(
                ReaderPageNavigationResult.Limit,
                ReaderPageNavigationDirection.Forward,
                1.0),
            TestContext.Current.CancellationToken);

        outcome.Should().Be(ReaderPageNavigationOutcome.AdjacentChapter(1));
        sut.CurrentCharacterCount.Should().Be(100);
        sut.IsStatisticsTracking.Should().BeTrue();
        statisticsSession.Checkpoints.Should().Equal(
            (100, ReaderStatisticsCheckpointReason.AdjacentChapter));
    }

    [Fact]
    public async Task ManualFinalBookLimit_AutostartsPageTurnStatisticsWithoutCheckpoint()
    {
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession,
            statisticsSettings: new NovelStatisticsSettings
            {
                EnableStatistics = true,
                AutostartMode = StatisticsAutostartMode.PageTurn,
            });
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.90);

        var outcome = await sut.HandleManualPageNavigationAsync(
            new(
                ReaderPageNavigationResult.Limit,
                ReaderPageNavigationDirection.Forward,
                1.0),
            TestContext.Current.CancellationToken);

        outcome.Should().Be(ReaderPageNavigationOutcome.NoMovement);
        sut.CurrentCharacterCount.Should().Be(90);
        sut.IsStatisticsTracking.Should().BeTrue();
        statisticsSession.Checkpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task StatisticsToggleCommand_StartsAndStopsWhenStatisticsAreEnabled()
    {
        using var temp = new TempBookDirectory();
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = CreateInitializedSut(
            temp.Path,
            new ReaderHighlightService(),
            statisticsSession: statisticsSession,
            statisticsSettings: new NovelStatisticsSettings
            {
                EnableStatistics = true,
            });
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.40);

        await sut.ToggleStatisticsTrackingCommand.ExecuteAsync(null);
        await sut.ToggleStatisticsTrackingCommand.ExecuteAsync(null);

        statisticsSession.StartPositions.Should().Equal(40);
        statisticsSession.StopPositions.Should().Equal(40);
        sut.IsStatisticsTracking.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualNavigationCheckpoint_CompletesBeforeLifecycleFinalFlush(
        bool adjacentChapter,
        bool close)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var manualCheckpointStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseManualCheckpoint = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manualCheckpointCompleted = false;
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue(
                Interlocked.Increment(ref saveCount) == 1
                    ? "manual-save"
                    : "final-save"))
            .ReturnsAsync(Result.Success());
        var manualReason = adjacentChapter
            ? ReaderStatisticsCheckpointReason.AdjacentChapter
            : ReaderStatisticsCheckpointReason.ReadingMovement;
        var statistics = new FakeReaderStatisticsSession
        {
            CheckpointAsyncHandler = async (position, reason, checkpointToken) =>
            {
                _ = (position, checkpointToken);
                if (reason == manualReason)
                {
                    events.Enqueue("manual-checkpoint-start");
                    manualCheckpointStarted.TrySetResult();
                    await releaseManualCheckpoint.Task;
                    manualCheckpointCompleted = true;
                    events.Enqueue("manual-checkpoint-end");
                }
                else
                {
                    events.Enqueue($"final-checkpoint-{reason}");
                }
            },
        };
        var flushSawManualCheckpoint = false;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                flushSawManualCheckpoint = manualCheckpointCompleted;
                events.Enqueue("flush-export");
            })
            .Returns(Task.CompletedTask);
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100, 100]);
        sut.SetChapter(0, 2);
        sut.UpdateProgress(adjacentChapter ? 0.9 : 0.2);
        var readerEvent = adjacentChapter
            ? new ReaderPageNavigationEvent(
                ReaderPageNavigationResult.Limit,
                ReaderPageNavigationDirection.Forward,
                1.0)
            : new ReaderPageNavigationEvent(
                ReaderPageNavigationResult.Scrolled,
                ReaderPageNavigationDirection.Forward,
                0.3);

        var manual = sut.HandleManualPageNavigationAsync(readerEvent, ct);
        await manualCheckpointStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var lifecycle = close
            ? sut.PrepareForReaderLifecycleCloseAsync(ct)
            : sut.CheckpointAppBackgroundingAsync(ct);

        lifecycle.IsCompleted.Should().BeFalse();
        Volatile.Read(ref saveCount).Should().Be(1);
        releaseManualCheckpoint.TrySetResult();
        await Task.WhenAll(manual, lifecycle).WaitAsync(TimeSpan.FromSeconds(2), ct);

        flushSawManualCheckpoint.Should().BeTrue();
        statistics.Checkpoints.Select(item => item.Reason).Should().Equal(
            manualReason,
            close
                ? ReaderStatisticsCheckpointReason.Close
                : ReaderStatisticsCheckpointReason.Background);
        events.Should().ContainInOrder(
            "manual-save",
            "manual-checkpoint-start",
            "manual-checkpoint-end",
            "final-save",
            close ? "final-checkpoint-Close" : "final-checkpoint-Background",
            "flush-export");
    }

    [Fact]
    public async Task ManualNavigation_WhenBookmarkWriteFails_StillCheckpointsWithoutPublishing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("write failed", "Bookmark"));
        var notifications = new Mock<INotificationService>();
        var statistics = new FakeReaderStatisticsSession();
        var messenger = new FakeMessenger();
        var autoSync = CreateAutoSyncCoordinator();
        var sut = CreateSut(
            novelService.Object,
            notifications.Object,
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.2);

        await sut.HandleManualPageNavigationAsync(
            new ReaderPageNavigationEvent(
                ReaderPageNavigationResult.Scrolled,
                ReaderPageNavigationDirection.Forward,
                0.3),
            ct);

        statistics.Checkpoints.Should().Equal(
            (30, ReaderStatisticsCheckpointReason.ReadingMovement));
        notifications.Verify(service => service.ShowError("write failed", "Bookmark"), Times.Once);
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Never);
        messenger.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task LifecycleClose_TakesOverInFlightBackgroundWithoutReopeningAdmission()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue(
                Interlocked.Increment(ref saveCount) == 1
                    ? "background-save"
                    : "close-save"))
            .ReturnsAsync(Result.Success());
        var backgroundFlushStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackgroundFlush = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var flushCount = 0;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Returns(async (NovelBook book, CancellationToken flushToken) =>
            {
                _ = (book, flushToken);
                if (Interlocked.Increment(ref flushCount) == 1)
                {
                    events.Enqueue("background-flush-start");
                    backgroundFlushStarted.TrySetResult();
                    await releaseBackgroundFlush.Task;
                    events.Enqueue("background-flush-end");
                }
                else
                {
                    events.Enqueue("close-flush");
                }
            });
        var statistics = new FakeReaderStatisticsSession
        {
            CheckpointRecorded = reason => events.Enqueue($"checkpoint-{reason}"),
        };
        var sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        var background = sut.CheckpointAppBackgroundingAsync(ct);
        await backgroundFlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var close = sut.PrepareForReaderLifecycleCloseAsync(ct);
        close.IsCompleted.Should().BeFalse();
        await sut.SaveProgressNowAsync(flushStatistics: false, ct: ct);

        releaseBackgroundFlush.TrySetResult();
        await Task.WhenAll(background, close).WaitAsync(TimeSpan.FromSeconds(2), ct);
        await sut.SaveProgressNowAsync(flushStatistics: false, ct: ct);

        Volatile.Read(ref saveCount).Should().Be(2);
        events.Should().ContainInOrder(
            "background-save",
            "checkpoint-Background",
            "background-flush-start",
            "background-flush-end",
            "close-save",
            "checkpoint-Close",
            "close-flush");
        autoSync.Verify(service => service.Cancel(), Times.Once);
    }

    [Fact]
    public async Task ProgrammaticCompletion_ResetBaselineCompletesBeforeLifecycleFinalFlush()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var events = new ConcurrentQueue<string>();
        var baselineCompleted = false;
        var saveCount = 0;
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => events.Enqueue(
                Interlocked.Increment(ref saveCount) == 1
                    ? "programmatic-save"
                    : "final-save"))
            .ReturnsAsync(Result.Success());
        var statistics = new FakeReaderStatisticsSession
        {
            ResetBaselineRecorded = _ =>
            {
                baselineCompleted = true;
                events.Enqueue("baseline-reset");
            },
            CheckpointRecorded = reason => events.Enqueue($"checkpoint-{reason}"),
        };
        var flushSawBaseline = false;
        var autoSync = CreateAutoSyncCoordinator();
        autoSync.Setup(service => service.FlushAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                flushSawBaseline = baselineCompleted;
                events.Enqueue("flush-export");
            })
            .Returns(Task.CompletedTask);
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        NovelReaderPageViewModel? sut = null;
        Task? close = null;
        var closeRequested = false;
        messenger.Register<NovelLibraryChangedMessage>(recipient, (_, _) =>
        {
            if (closeRequested)
                return;

            closeRequested = true;
            close = sut!.PrepareForReaderLifecycleCloseAsync(ct);
        });
        sut = CreateSut(
            novelService.Object,
            Mock.Of<INotificationService>(),
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.4);

        await sut.SaveProgressAndResetStatisticsBaselineAsync(ct);
        await close!.WaitAsync(TimeSpan.FromSeconds(2), ct);

        flushSawBaseline.Should().BeTrue();
        statistics.ResetPositions.Should().Equal(40);
        events.Should().ContainInOrder(
            "programmatic-save",
            "baseline-reset",
            "final-save",
            "checkpoint-Close",
            "flush-export");
    }

    [Fact]
    public async Task ProgrammaticCompletion_WhenBookmarkWriteFails_StillResetsWithoutPublishing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var novelService = CreateNovelService(temp.Path);
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("write failed", "Bookmark"));
        var notifications = new Mock<INotificationService>();
        var statistics = new FakeReaderStatisticsSession();
        var messenger = new FakeMessenger();
        var autoSync = CreateAutoSyncCoordinator();
        var sut = CreateSut(
            novelService.Object,
            notifications.Object,
            messenger,
            statistics,
            autoSync.Object);
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

        await sut.SaveProgressAndResetStatisticsBaselineAsync(ct);

        statistics.ResetPositions.Should().Equal(0);
        notifications.Verify(service => service.ShowError("write failed", "Bookmark"), Times.Once);
        autoSync.Verify(service => service.ScheduleExport(It.IsAny<NovelBook>()), Times.Never);
        messenger.SentMessages.Should().BeEmpty();
    }

    private static NovelReaderPageViewModel CreateInitializedSut(
        string bookRootPath,
        IReaderHighlightService highlightService,
        INovelBookSidecarService? novelBookSidecarService = null,
        IReaderStatisticsSession? statisticsSession = null,
        NovelStatisticsSettings? statisticsSettings = null,
        ContentLanguageProfile? language = null)
    {
        var novelService = new Mock<INovelLibraryService>();
        novelService
            .Setup(s => s.GetNovelBookAsync("book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "Book One",
                FilePath = System.IO.Path.Combine(bookRootPath, "book.epub"),
                ExtractedPath = bookRootPath,
            }));
        novelService
            .Setup(service => service.MarkOpenedAsync(
                "book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        novelService.Setup(service => service.SaveProgressAsync(
                "book-1",
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var appSettings = new AppSettings
        {
            StatisticsSettings = statisticsSettings ?? new NovelStatisticsSettings(),
        };
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(appSettings);
        var profile = new NoOpProfileRuntimeService(
            language ?? ContentLanguageProfile.Japanese);
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            highlightService,
            novelBookSidecarService ?? new NovelBookSidecarService(),
            statisticsSession ?? new FakeReaderStatisticsSession(),
            profile,
            settings.Object,
            CreateAutoSyncCoordinator().Object);
        sut.InitializeAsync(new NovelReaderNavigationArgs("book-1")).GetAwaiter().GetResult();
        return sut;
    }

    private static Mock<INovelLibraryService> CreateNovelService(string bookRootPath)
    {
        var novelService = new Mock<INovelLibraryService>();
        novelService.Setup(service => service.GetNovelBookAsync(
                "book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "Book One",
                FilePath = System.IO.Path.Combine(bookRootPath, "book.epub"),
                ExtractedPath = bookRootPath,
            }));
        novelService.Setup(service => service.MarkOpenedAsync(
                "book-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        return novelService;
    }

    private static NovelReaderPageViewModel CreateSut(
        INovelLibraryService novelService,
        INotificationService notificationService,
        IMessenger messenger,
        IReaderStatisticsSession statisticsSession,
        IReaderAutoSyncCoordinator autoSync)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        return new NovelReaderPageViewModel(
            novelService,
            notificationService,
            messenger,
            new ReaderHighlightService(),
            new NovelBookSidecarService(),
            statisticsSession,
            new NoOpProfileRuntimeService(ContentLanguageProfile.Japanese),
            settings.Object,
            autoSync);
    }

    private static Mock<IReaderAutoSyncCoordinator> CreateAutoSyncCoordinator(bool imported = false)
    {
        var autoSync = new Mock<IReaderAutoSyncCoordinator>();
        autoSync.Setup(service => service.ImportOnOpenAsync(
                It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);
        return autoSync;
    }

    private static NovelReadingStatistic Statistic(
        int characters,
        double readingTime,
        int speed) =>
        new("Book One", "2026-07-11", characters, readingTime, speed, speed, speed, speed, 1);

    private sealed class FakeReaderStatisticsSession : IReaderStatisticsSession
    {
        private static readonly NovelReadingStatistic Empty = ReaderStatisticsMath.Empty(
            "Book One",
            new DateOnly(2026, 7, 11));

        public ReaderStatisticsSessionState State { get; private set; } = new(
            false,
            false,
            Empty,
            Empty,
            Empty,
            []);

        public (string Root, string Title, int Position)? LoadRequest { get; private set; }
        public List<int> StartPositions { get; } = [];
        public List<(int Position, ReaderStatisticsCheckpointReason Reason)> Checkpoints { get; } = [];
        public List<int> ResetPositions { get; } = [];
        public List<int> StopPositions { get; } = [];
        public Action<ReaderStatisticsCheckpointReason>? CheckpointRecorded { get; set; }
        public Action<ReaderStatisticsPosition>? ResetBaselineRecorded { get; set; }
        public Func<
            ReaderStatisticsPosition,
            ReaderStatisticsCheckpointReason,
            CancellationToken,
            Task>? CheckpointAsyncHandler
        { get; set; }

        public event EventHandler<ReaderStatisticsSessionState>? StateChanged;

        public Task LoadAsync(
            string bookRoot,
            string title,
            ReaderStatisticsPosition position,
            CancellationToken ct = default)
        {
            LoadRequest = (bookRoot, title, position.RawCharacterCount);
            return Task.CompletedTask;
        }

        public void Start(ReaderStatisticsPosition position)
        {
            StartPositions.Add(position.RawCharacterCount);
            Publish(State with
            {
                IsTracking = true,
                IsPaused = false,
            });
        }

        public void Tick(ReaderStatisticsPosition position)
        {
        }

        public async Task CheckpointAsync(
            ReaderStatisticsPosition position,
            ReaderStatisticsCheckpointReason reason,
            CancellationToken ct = default)
        {
            if (CheckpointAsyncHandler != null)
                await CheckpointAsyncHandler(position, reason, ct);
            Checkpoints.Add((position.RawCharacterCount, reason));
            CheckpointRecorded?.Invoke(reason);
        }

        public Task PauseAsync(
            ReaderStatisticsPosition position,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(
            ReaderStatisticsPosition position,
            CancellationToken ct = default)
        {
            StopPositions.Add(position.RawCharacterCount);
            Publish(State with
            {
                IsTracking = false,
                IsPaused = false,
            });
            return Task.CompletedTask;
        }

        public void ResetBaseline(ReaderStatisticsPosition position)
        {
            ResetPositions.Add(position.RawCharacterCount);
            ResetBaselineRecorded?.Invoke(position);
        }

        public void Publish(ReaderStatisticsSessionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private static ReaderHighlight HighlightAt(
        int character,
        int offset,
        ReaderHighlightColor color = ReaderHighlightColor.Yellow) =>
        new(
            Guid.NewGuid(),
            Character: character,
            Offset: offset,
            Text: character.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Color: color,
            CreatedAt: new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero));

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class NoOpProfileRuntimeService : IProfileRuntimeService
    {
        private readonly Action<NovelBook>? _onActivateForBook;

        public NoOpProfileRuntimeService(
            ContentLanguageProfile language,
            Action<NovelBook>? onActivateForBook = null)
        {
            _onActivateForBook = onActivateForBook;
            ActiveResolution = new ProfileResolution(
                new HoshiProfile(
                    ProfileConstants.DefaultJapaneseProfileId,
                    "Japanese EPUB",
                    language.Id),
                language,
                ProfileContext.Global());
        }

        public ProfileResolution ActiveResolution { get; }

        public string ActiveProfileId => ActiveResolution.Profile.Id;

        public ContentLanguageProfile ActiveLanguage => ActiveResolution.Language;

        public event EventHandler<ProfileResolution>? ProfileChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ActivateGlobalAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ActivateProfileAsync(
            string profileId,
            bool setGlobalActive = true,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ActivateForBookAsync(NovelBook book, CancellationToken ct = default)
        {
            _onActivateForBook?.Invoke(book);
            return Task.CompletedTask;
        }

        public Task ActivateForVideoAsync(VideoItem video, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveActiveSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InlineRecordingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCount++;
            RunWithSynchronizationContext(this, () => callback(state));
        }
    }

    private static void RunWithSynchronizationContext(
        SynchronizationContext context,
        Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
