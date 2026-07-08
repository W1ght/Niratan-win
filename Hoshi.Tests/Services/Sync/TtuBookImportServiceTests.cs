using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Novels;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class TtuBookImportServiceTests
{
    [Fact]
    public async Task ImportRemoteBookAsync_DownloadsConvertsImportsAndAppliesRemoteSidecars()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = new FakeRemoteStore();
        var converter = new FakeConverter();
        var library = new FakeNovelLibraryService();
        var sync = new FakeTtuSyncService();
        var service = new TtuBookImportService(remote, converter, library, sync);
        var remoteBook = new TtuRemoteBook(
            Id: "folder-id",
            Title: "星を読む",
            SanitizedTitle: "星を読む",
            Files: new TtuRemoteBookFiles(
                Progress: new TtuRemoteFile("progress-id", "progress_1_6_2000_0.5.json"),
                Statistics: new TtuRemoteFile("stats-id", "statistics_1_6_1_1_1_0_0_0_0_1_1_1_1_3600_3600_na.json"),
                AudioBook: new TtuRemoteFile("audio-id", "audioBook_1_6_2000_42.json"),
                BookData: new TtuRemoteFile("bookdata-id", "bookdata_1_6_1200_2000_1000.zip"),
                Cover: null),
            Progress: 0.5);

        var result = await service.ImportRemoteBookAsync(
            remoteBook,
            new TtuBookImportOptions(SyncStatistics: true, SyncAudioBook: true),
            progress: null,
            ct);

        result.IsSuccess.Should().BeTrue();
        remote.DownloadedFile!.Name.Should().Be("bookdata_1_6_1200_2000_1000.zip");
        converter.InputBookDataPath.Should().NotBeNull();
        library.ImportedEpubPath.Should().Be(converter.OutputEpubPath);
        sync.Book.Should().BeSameAs(result.Value);
        sync.Options.Should().Be(new TtuSyncOptions(
            Direction: TtuSyncDirection.ImportFromTtu,
            SyncBookData: false,
            SyncStatistics: true,
            StatisticsSyncMode: StatisticsSyncMode.Merge,
            SyncAudioBook: true,
            ImportOnly: true));
    }

    private sealed class FakeRemoteStore : ITtuSyncRemoteStore
    {
        public TtuRemoteFile? DownloadedFile { get; private set; }

        public Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TtuRemoteBook>>([]);

        public async Task DownloadBookDataAsync(
            TtuRemoteFile file,
            string destinationFilePath,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            DownloadedFile = file;
            await File.WriteAllBytesAsync(destinationFilePath, [1, 2, 3], ct);
            progress?.Report(1);
        }

        public Task<TtuRemoteBookFiles> ListBookFilesAsync(string bookTitle, CancellationToken ct = default) =>
            Task.FromResult(new TtuRemoteBookFiles(null, null, null, null, null));

        public Task<TtuProgress?> GetProgressAsync(TtuRemoteFile file, CancellationToken ct = default) =>
            Task.FromResult<TtuProgress?>(null);

        public Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(TtuRemoteFile file, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NovelReadingStatistic>?>(null);

        public Task<TtuAudioBook?> GetAudioBookAsync(TtuRemoteFile file, CancellationToken ct = default) =>
            Task.FromResult<TtuAudioBook?>(null);

        public Task UpsertProgressAsync(string bookTitle, TtuProgress progress, TtuRemoteFile? existingFile, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpsertStatisticsAsync(string bookTitle, IReadOnlyList<NovelReadingStatistic> statistics, TtuRemoteFile? existingFile, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpsertAudioBookAsync(string bookTitle, TtuAudioBook audioBook, TtuRemoteFile? existingFile, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeConverter : ITtuBookDataConverter
    {
        public string? InputBookDataPath { get; private set; }
        public string OutputEpubPath { get; private set; } = "";

        public async Task<string> ConvertToEpubAsync(
            string ttuBookDataPath,
            string outputDirectory,
            CancellationToken ct = default)
        {
            InputBookDataPath = ttuBookDataPath;
            Directory.CreateDirectory(outputDirectory);
            OutputEpubPath = Path.Combine(outputDirectory, "converted.epub");
            await File.WriteAllTextAsync(OutputEpubPath, "epub", ct);
            return OutputEpubPath;
        }
    }

    private sealed class FakeNovelLibraryService : INovelLibraryService
    {
        public string? ImportedEpubPath { get; private set; }

        public Task<Result<NovelBook>> ImportEpubAsync(string filePath, CancellationToken ct = default)
        {
            ImportedEpubPath = filePath;
            return Task.FromResult(Result<NovelBook>.Success(new NovelBook
            {
                Id = "book-1",
                Title = "星を読む",
                FilePath = filePath,
                ExtractedPath = "D:\\Books\\book-1",
            }));
        }

        public Task<Result<IReadOnlyList<NovelBook>>> GetNovelBooksAsync(string? queryText = null, CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<NovelBook>>.Success([]));

        public Task<Result<NovelBook?>> GetNovelBookAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result<NovelBook?>.Success(null));

        public Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SaveProgressAsync(string bookId, int chapterIndex, double progress, int currentCharacterCount, int totalCharacterCount, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SaveNovelBookOrderAsync(IReadOnlyList<string> orderedBookIds, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeTtuSyncService : ITtuSyncService
    {
        public NovelBook? Book { get; private set; }
        public TtuSyncOptions? Options { get; private set; }

        public Task<TtuSyncResult> SyncBookAsync(
            NovelBook book,
            TtuSyncOptions options,
            CancellationToken ct = default)
        {
            Book = book;
            Options = options;
            return Task.FromResult(new TtuSyncResult(TtuSyncResultKind.Imported, book.Title));
        }
    }
}
