using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class TtuSyncServiceTests
{
    [Fact]
    public async Task SyncBookAsync_AutoExportsLocalProgressWhenLocalBookmarkIsNewer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecars = await CreateSidecarsAsync(temp.Path, ct);
        var localModified = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);
        await sidecars.Book.SaveBookmarkAsync(
            temp.Path,
            new NovelBookmark(ChapterIndex: 1, Progress: 0.25, CharacterCount: 1250, LastModified: localModified),
            ct);
        var remote = new FakeTtuSyncRemoteStore();
        var sut = CreateSut(sidecars, remote);

        var result = await sut.SyncBookAsync(
            CreateBook(temp.Path),
            new TtuSyncOptions(Direction: TtuSyncDirection.Auto),
            ct);

        result.Kind.Should().Be(TtuSyncResultKind.Exported);
        remote.LastExportedProgress.Should().NotBeNull();
        remote.LastExportedProgress!.ExploredCharCount.Should().Be(1250);
        remote.LastExportedProgress.Progress.Should().Be(0.625);
        remote.LastExportedProgress.LastBookmarkModified.Should().Be(localModified);
    }

    [Fact]
    public async Task SyncBookAsync_AutoImportsRemoteProgressWhenRemoteBookmarkIsNewer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecars = await CreateSidecarsAsync(temp.Path, ct);
        await sidecars.Book.SaveBookmarkAsync(
            temp.Path,
            new NovelBookmark(ChapterIndex: 0, Progress: 0.1, CharacterCount: 100, LastModified: DateTimeOffset.FromUnixTimeMilliseconds(1000)),
            ct);
        var remote = new FakeTtuSyncRemoteStore
        {
            ProgressFile = new TtuRemoteFile("progress-id", "progress_1_6_2000_0.625.json"),
            Progress = new TtuProgress(
                DataId: 1,
                ExploredCharCount: 1250,
                Progress: 0.625,
                LastBookmarkModified: DateTimeOffset.FromUnixTimeMilliseconds(2000)),
        };
        var sut = CreateSut(sidecars, remote);

        var result = await sut.SyncBookAsync(
            CreateBook(temp.Path),
            new TtuSyncOptions(Direction: TtuSyncDirection.Auto),
            ct);

        result.Kind.Should().Be(TtuSyncResultKind.Imported);
        result.CharacterCount.Should().Be(1250);
        var bookmark = await sidecars.Book.LoadBookmarkAsync(temp.Path, ct);
        bookmark.Should().Be(new NovelBookmark(
            ChapterIndex: 1,
            Progress: 0.25,
            CharacterCount: 1250,
            LastModified: DateTimeOffset.FromUnixTimeMilliseconds(2000)));
    }

    [Fact]
    public async Task SyncBookAsync_ImportOnlyImportsNewerAudioBookWhenBookmarkProgressIsSynced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecars = await CreateSidecarsAsync(temp.Path, ct);
        var syncedModified = DateTimeOffset.FromUnixTimeMilliseconds(2000);
        await sidecars.Book.SaveBookmarkAsync(
            temp.Path,
            new NovelBookmark(ChapterIndex: 1, Progress: 0.25, CharacterCount: 1250, LastModified: syncedModified),
            ct);
        await sidecars.Sasayaki.SavePlaybackAsync(
            temp.Path,
            new SasayakiPlaybackData { LastPosition = 10, Rate = 1.25 },
            ct);
        File.SetLastWriteTimeUtc(
            Path.Combine(temp.Path, ISasayakiSidecarService.PlaybackFileName),
            DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime);
        var remote = new FakeTtuSyncRemoteStore
        {
            ProgressFile = new TtuRemoteFile("progress-id", "progress_1_6_2000_0.625.json"),
            AudioBookFile = new TtuRemoteFile("audio-id", "audioBook_1_6_3000_42.5.json"),
            AudioBook = new TtuAudioBook("星を読む", PlaybackPosition: 42.5, LastAudioBookModified: 3000),
        };
        var sut = CreateSut(sidecars, remote);

        var result = await sut.SyncBookAsync(
            CreateBook(temp.Path),
            new TtuSyncOptions(
                Direction: TtuSyncDirection.Auto,
                SyncAudioBook: true,
                ImportOnly: true),
            ct);

        result.Kind.Should().Be(TtuSyncResultKind.Imported);
        var playback = await sidecars.Sasayaki.LoadPlaybackAsync(temp.Path, ct);
        playback.LastPosition.Should().Be(42.5);
        playback.Rate.Should().Be(1.25);
    }

    [Fact]
    public async Task SyncBookAsync_MergesStatisticsByLatestModifiedWhenExporting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var sidecars = await CreateSidecarsAsync(temp.Path, ct);
        await sidecars.Book.SaveBookmarkAsync(
            temp.Path,
            new NovelBookmark(ChapterIndex: 0, Progress: 0.5, CharacterCount: 500, LastModified: DateTimeOffset.FromUnixTimeMilliseconds(2000)),
            ct);
        await sidecars.Statistics.SaveAsync(
            temp.Path,
            [Statistic("2026-07-08", charactersRead: 20, modified: 2)],
            ct);
        var remote = new FakeTtuSyncRemoteStore
        {
            StatisticsFile = new TtuRemoteFile("statistics-id", "statistics_1_6_3_40_2_0_0_0_0_1_1_20_20_7200_7200_na.json"),
            Statistics = [Statistic("2026-07-08", charactersRead: 10, modified: 1), Statistic("2026-07-09", charactersRead: 30, modified: 3)],
        };
        var sut = CreateSut(sidecars, remote);

        await sut.SyncBookAsync(
            CreateBook(temp.Path),
            new TtuSyncOptions(
                Direction: TtuSyncDirection.ExportToTtu,
                SyncStatistics: true,
                StatisticsSyncMode: StatisticsSyncMode.Merge),
            ct);

        remote.LastExportedStatistics.Should().NotBeNull();
        remote.LastExportedStatistics!.Select(statistic => statistic.DateKey)
            .Should()
            .BeEquivalentTo(["2026-07-08", "2026-07-09"]);
        remote.LastExportedStatistics!.Single(statistic => statistic.DateKey == "2026-07-08")
            .CharactersRead
            .Should()
            .Be(20);
    }

    private static async Task<Sidecars> CreateSidecarsAsync(
        string bookRootPath,
        CancellationToken ct)
    {
        var book = new NovelBookSidecarService();
        var statistics = new NovelStatisticsSidecarService();
        var sasayaki = new SasayakiSidecarService();
        await book.SaveBookInfoAsync(
            bookRootPath,
            new NovelBookInfo(
                CharacterCount: 2000,
                ChapterInfo: new Dictionary<string, NovelBookInfoChapter>
                {
                    ["chapter1.xhtml"] = new(SpineIndex: 0, CurrentTotal: 0, ChapterCount: 1000),
                    ["chapter2.xhtml"] = new(SpineIndex: 1, CurrentTotal: 1000, ChapterCount: 1000),
                }),
            ct);
        return new Sidecars(book, statistics, sasayaki);
    }

    private static TtuSyncService CreateSut(
        Sidecars sidecars,
        ITtuSyncRemoteStore remote) =>
        new(
            sidecars.Book,
            sidecars.Statistics,
            sidecars.Sasayaki,
            remote);

    private static NovelBook CreateBook(string rootPath) => new()
    {
        Id = "book-1",
        Title = "星を読む",
        ExtractedPath = rootPath,
        TotalCharacterCount = 2000,
    };

    private static NovelReadingStatistic Statistic(
        string dateKey,
        int charactersRead,
        long modified) =>
        new(
            Title: "Book",
            DateKey: dateKey,
            CharactersRead: charactersRead,
            ReadingTime: 1,
            MinReadingSpeed: 0,
            AltMinReadingSpeed: 0,
            LastReadingSpeed: 0,
            MaxReadingSpeed: 0,
            LastStatisticModified: modified);

    private sealed record Sidecars(
        INovelBookSidecarService Book,
        INovelStatisticsSidecarService Statistics,
        ISasayakiSidecarService Sasayaki);

    private sealed class FakeTtuSyncRemoteStore : ITtuSyncRemoteStore
    {
        public TtuRemoteFile? ProgressFile { get; set; }
        public TtuRemoteFile? StatisticsFile { get; set; }
        public TtuRemoteFile? AudioBookFile { get; set; }
        public TtuProgress? Progress { get; set; }
        public IReadOnlyList<NovelReadingStatistic>? Statistics { get; set; }
        public TtuAudioBook? AudioBook { get; set; }
        public TtuProgress? LastExportedProgress { get; private set; }
        public IReadOnlyList<NovelReadingStatistic>? LastExportedStatistics { get; private set; }
        public TtuAudioBook? LastExportedAudioBook { get; private set; }

        public Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TtuRemoteBook>>([]);

        public Task TrashRemoteBookAsync(
            TtuRemoteBook remoteBook,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TtuRemoteBookFiles> ListBookFilesAsync(
            string bookTitle,
            CancellationToken ct = default) =>
            Task.FromResult(new TtuRemoteBookFiles(
                ProgressFile,
                StatisticsFile,
                AudioBookFile,
                BookData: null,
                Cover: null));

        public Task<TtuProgress?> GetProgressAsync(
            TtuRemoteFile file,
            CancellationToken ct = default) =>
            Task.FromResult(Progress);

        public Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(
            TtuRemoteFile file,
            CancellationToken ct = default) =>
            Task.FromResult(Statistics);

        public Task<TtuAudioBook?> GetAudioBookAsync(
            TtuRemoteFile file,
            CancellationToken ct = default) =>
            Task.FromResult(AudioBook);

        public Task DownloadBookDataAsync(
            TtuRemoteFile file,
            string destinationFilePath,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpsertProgressAsync(
            string bookTitle,
            TtuProgress progress,
            TtuRemoteFile? existingFile,
            CancellationToken ct = default)
        {
            LastExportedProgress = progress;
            return Task.CompletedTask;
        }

        public Task UpsertStatisticsAsync(
            string bookTitle,
            IReadOnlyList<NovelReadingStatistic> statistics,
            TtuRemoteFile? existingFile,
            CancellationToken ct = default)
        {
            LastExportedStatistics = statistics;
            return Task.CompletedTask;
        }

        public Task UpsertAudioBookAsync(
            string bookTitle,
            TtuAudioBook audioBook,
            TtuRemoteFile? existingFile,
            CancellationToken ct = default)
        {
            LastExportedAudioBook = audioBook;
            return Task.CompletedTask;
        }
    }

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
}
