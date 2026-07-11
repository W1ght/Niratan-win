using System.Text.Json;
using FluentAssertions;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Profiles;
using Hoshi.Services.Novels;
using Hoshi.Services.Profiles;
using Hoshi.Services.UI;
using Hoshi.Tests.TestUtils;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public sealed class NovelReaderPageViewModelTests
{
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

        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            highlightService,
            new NovelBookSidecarService(),
            new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService());
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"));
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
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new ReaderHighlightService(),
            sidecarService,
            new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService());
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"));
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

    [Fact]
    public async Task LifecycleCheckpoints_SaveBackgroundAndCloseExactlyOnce()
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
        novelService
            .Setup(s => s.SaveProgressAsync(
                "book-1", 0, 0.5, 50, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var statisticsSession = new FakeReaderStatisticsSession();
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            new ReaderHighlightService(),
            new NovelBookSidecarService(),
            statisticsSession,
            new NoOpProfileRuntimeService());
        await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"));
        sut.SetChapterCharacterCounts([100]);
        sut.SetChapter(0, 1);
        sut.UpdateProgress(0.5);

        await sut.CheckpointAppBackgroundingAsync(ct);
        await Task.WhenAll(
            sut.PrepareForReaderLifecycleCloseAsync(ct),
            sut.PrepareForReaderLifecycleCloseAsync(ct));

        novelService.Verify(s => s.SaveProgressAsync(
            "book-1", 0, 0.5, 50, 100, It.IsAny<CancellationToken>()), Times.Exactly(2));
        statisticsSession.Checkpoints.Should().Equal(
            (50, ReaderStatisticsCheckpointReason.Background),
            (50, ReaderStatisticsCheckpointReason.Close));
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

    private static NovelReaderPageViewModel CreateInitializedSut(
        string bookRootPath,
        IReaderHighlightService highlightService,
        INovelBookSidecarService? novelBookSidecarService = null,
        IReaderStatisticsSession? statisticsSession = null)
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
        var sut = new NovelReaderPageViewModel(
            novelService.Object,
            Mock.Of<INotificationService>(),
            new FakeMessenger(),
            highlightService,
            novelBookSidecarService ?? new NovelBookSidecarService(),
            statisticsSession ?? new FakeReaderStatisticsSession(),
            new NoOpProfileRuntimeService());
        sut.InitializeAsync(new NovelReaderNavigationArgs("book-1")).GetAwaiter().GetResult();
        return sut;
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

        public void Start(ReaderStatisticsPosition position) =>
            StartPositions.Add(position.RawCharacterCount);

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
            StopPositions.Add(position.RawCharacterCount);
            return Task.CompletedTask;
        }

        public void ResetBaseline(ReaderStatisticsPosition position) =>
            ResetPositions.Add(position.RawCharacterCount);

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
        public ProfileResolution ActiveResolution { get; } = new(
            new HoshiProfile(ProfileConstants.DefaultJapaneseProfileId, "Japanese EPUB", ContentLanguageProfile.Japanese.Id),
            ContentLanguageProfile.Japanese,
            ProfileContext.Global());

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

        public Task ActivateForBookAsync(NovelBook book, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ActivateForVideoAsync(VideoItem video, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveActiveSettingsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
