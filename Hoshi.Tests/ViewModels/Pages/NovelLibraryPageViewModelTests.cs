using FluentAssertions;
using CommunityToolkit.Mvvm.Input;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;
using Hoshi.Tests.TestUtils;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class NovelLibraryPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsNovelBooks()
    {
        var service = new Mock<INovelLibraryService>();
        service
            .Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                Result<NovelBookCatalogSnapshot>.Success(
                    new NovelBookCatalogSnapshot(
                    [
                        new NovelBook
                        {
                            Id = "book-1",
                            Title = "Book One",
                            FilePath = "D:\\Books\\one.epub",
                        },
                    ],
                    [])
                )
            );

        var sut = CreateSut(service.Object);

        await sut.InitializeAsync();

        sut.NovelBooks.Should().ContainSingle();
        sut.NovelBooks[0].Book.Title.Should().Be("Book One");
    }

    [Fact]
    public async Task ImportCommand_ShowsNoError_WhenPickerCancelled()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.OpenFilePickerAsync(".epub")).ReturnsAsync((string?)null);
        var notification = new Mock<INotificationService>();

        var sut = CreateSut(dialogService: dialog.Object, notificationService: notification.Object);

        await sut.ImportNovelCommand.ExecuteAsync(null);

        notification.Verify(n => n.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OpenNovelCommand_SendsNavigateMessage()
    {
        var messenger = new FakeMessenger();
        var sut = CreateSut(messenger: messenger);
        var book = new NovelBook
        {
            Id = "book-1",
            Title = "Book One",
            FilePath = "D:\\Books\\one.epub",
        };

        sut.OpenNovelCommand.Execute(new NovelBookItemViewModel(book));

        messenger.SentMessages.Should().Contain(m => m is SwitchAppModeMessage);
    }

    [Fact]
    public async Task MatchSasayakiCommand_PicksFilesAndRunsMatch()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .SetupSequence(d => d.OpenFilePickerAsync(It.IsAny<string[]>()))
            .ReturnsAsync("D:\\Audio\\book.m4b")
            .ReturnsAsync("D:\\Subs\\book.srt");
        var notification = new Mock<INotificationService>();
        var matchService = new Mock<ISasayakiMatchService>();
        var settings = Mock.Of<ISettingsService>(s => s.Current == new AppSettings
        {
            SasayakiSettings = new SasayakiSettings { SearchWindowSize = 321 },
        });
        var book = new NovelBook
        {
            Id = "book-1",
            Title = "Book One",
            FilePath = "D:\\Books\\one.epub",
        };
        matchService
            .Setup(s => s.MatchAsync(
                book,
                "D:\\Audio\\book.m4b",
                "D:\\Subs\\book.srt",
                321,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasayakiMatchData
            {
                BookId = book.Id,
                Cues = [new SasayakiCue { Id = 1, Text = "星", StartTime = 0, EndTime = 1 }],
                Matches = [new SasayakiMatch { CueIndex = 0, ChapterIndex = 0, Length = 1 }],
            });
        var sut = CreateSut(
            dialogService: dialog.Object,
            notificationService: notification.Object,
            sasayakiMatchService: matchService.Object,
            settingsService: settings);

        await sut.MatchSasayakiCommand.ExecuteAsync(new NovelBookItemViewModel(book));

        matchService.VerifyAll();
        notification.Verify(
            n => n.ShowSuccess(
                It.Is<string>(message => message.Contains("1/1")),
                "Sasayaki matched"),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_AppliesMacAlignedSortOptions()
    {
        var books = new[]
        {
            new NovelBook
            {
                Id = "recent",
                Title = "Beta",
                FilePath = "D:\\Books\\recent.epub",
                ImportedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                LastOpenedAt = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            },
            new NovelBook
            {
                Id = "old",
                Title = "Alpha",
                FilePath = "D:\\Books\\old.epub",
                ImportedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
            },
            new NovelBook
            {
                Id = "middle",
                Title = "Gamma",
                FilePath = "D:\\Books\\middle.epub",
                ImportedAt = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
            },
        };
        var service = new RecordingNovelLibraryService { Books = books };
        var sut = CreateSut(novelService: service);

        SetSortOption(sut, "Title");
        await sut.InitializeAsync();

        sut.NovelBooks.Select(item => item.Book.Id).Should().Equal("old", "recent", "middle");

        SetSortOption(sut, "Recent");
        await sut.InitializeAsync();

        sut.NovelBooks.Select(item => item.Book.Id).Should().Equal("recent", "middle", "old");
    }

    [Fact]
    public async Task ImportDroppedNovelsCommand_ImportsOnlyEpubFilesInDropOrder()
    {
        var service = new RecordingNovelLibraryService();
        var sut = CreateSut(novelService: service);
        var command = GetAsyncCommand(sut, "ImportDroppedNovelsCommand");

        await command.ExecuteAsync(
            new[]
            {
                "D:\\Books\\first.epub",
                "D:\\Books\\notes.txt",
                "D:\\Books\\second.EPUB",
            });

        service.ImportedPaths.Should().Equal("D:\\Books\\first.epub", "D:\\Books\\second.EPUB");
    }

    [Fact]
    public async Task MoveNovelBeforeCommand_SwitchesToManualAndPersistsOrder()
    {
        var service = new RecordingNovelLibraryService
        {
            Books =
            [
                new NovelBook { Id = "alpha", Title = "Alpha", FilePath = "D:\\Books\\alpha.epub" },
                new NovelBook { Id = "beta", Title = "Beta", FilePath = "D:\\Books\\beta.epub" },
                new NovelBook { Id = "gamma", Title = "Gamma", FilePath = "D:\\Books\\gamma.epub" },
            ],
        };
        var sut = CreateSut(novelService: service);
        SetSortOption(sut, "Title");
        await sut.InitializeAsync();
        var command = GetAsyncCommand(sut, "MoveNovelBeforeCommand");
        var request = CreateMoveRequest("gamma", "alpha");

        await command.ExecuteAsync(request);

        GetSortOptionName(sut).Should().Be("Manual");
        sut.NovelBooks.Select(item => item.Book.Id).Should().Equal("gamma", "alpha", "beta");
        service.SavedOrders.Should().ContainSingle()
            .Which.Should().Equal("gamma", "alpha", "beta");
    }

    [Fact]
    public async Task RefreshRemoteBooksCommand_WhenGoogleDriveDisconnected_ShowsError()
    {
        var notification = new Mock<INotificationService>();
        var auth = new FakeGoogleDriveAuthService { HasCredentials = false };
        var settings = Mock.Of<ISettingsService>(s => s.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        });
        var sut = CreateSut(
            notificationService: notification.Object,
            settingsService: settings,
            googleDriveAuthService: auth);

        await sut.RefreshRemoteBooksCommand.ExecuteAsync(null);

        notification.Verify(
            n => n.ShowError(
                It.Is<string>(message => message.Contains("Google Drive")),
                "Sync unavailable"),
            Times.Once);
    }

    [Fact]
    public async Task RefreshRemoteBooksCommand_FiltersAlreadyImportedTtuTitles()
    {
        var service = new RecordingNovelLibraryService
        {
            Books =
            [
                new NovelBook { Id = "local", Title = "星*読む/", FilePath = "D:\\Books\\local.epub" },
            ],
        };
        var remote = new FakeTtuSyncRemoteStore
        {
            RemoteBooks =
            [
                RemoteBook("folder-local", "星*読む/", "星~ttu-star~読む%2F"),
                RemoteBook("folder-cloud", "雲の本", "雲の本"),
            ],
        };
        var settings = Mock.Of<ISettingsService>(s => s.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        });
        var sut = CreateSut(
            novelService: service,
            syncRemoteStore: remote,
            settingsService: settings,
            googleDriveAuthService: new FakeGoogleDriveAuthService { HasCredentials = true });
        await sut.InitializeAsync();

        await sut.RefreshRemoteBooksCommand.ExecuteAsync(null);

        sut.RemoteBooks.Should().ContainSingle();
        sut.RemoteBooks[0].Book.Title.Should().Be("雲の本");
    }

    [Fact]
    public async Task DownloadRemoteBookCommand_ImportsRemoteBookAndRemovesItFromRemoteShelf()
    {
        var remoteBook = RemoteBook("folder-cloud", "雲の本", "雲の本");
        var remote = new FakeTtuSyncRemoteStore { RemoteBooks = [remoteBook] };
        var importer = new FakeTtuBookImportService();
        var notification = new Mock<INotificationService>();
        var settings = Mock.Of<ISettingsService>(s => s.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        });
        var sut = CreateSut(
            syncRemoteStore: remote,
            ttuBookImportService: importer,
            notificationService: notification.Object,
            settingsService: settings,
            googleDriveAuthService: new FakeGoogleDriveAuthService { HasCredentials = true });
        await sut.RefreshRemoteBooksCommand.ExecuteAsync(null);

        await sut.DownloadRemoteBookCommand.ExecuteAsync(sut.RemoteBooks.Single());

        importer.ImportedBook.Should().Be(remoteBook);
        sut.RemoteBooks.Should().BeEmpty();
        notification.Verify(
            n => n.ShowSuccess("EPUB imported from Google Drive.", "Novel imported"),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_ProjectsReadingCustomAndUnshelvedSectionsWithWarnings()
    {
        var books = new[]
        {
            new NovelBook { Id = "a", Title = "Reading", CurrentCharacterCount = 10, TotalCharacterCount = 100 },
            new NovelBook { Id = "b", Title = "Complete", CurrentCharacterCount = 100, TotalCharacterCount = 100 },
            new NovelBook { Id = "c", Title = "New" },
        };
        var library = new Mock<INovelLibraryService>();
        library.Setup(service => service.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBookCatalogSnapshot>.Success(
                new NovelBookCatalogSnapshot(books, ["broken/metadata.json"])));
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState(
                [new NovelShelf("收藏", ["b"])],
                ["a", "c"])));
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            BookshelfShowReading = true,
        });
        var sut = CreateSut(
            novelService: library.Object,
            shelfService: shelves.Object,
            settingsService: settings);

        await sut.InitializeAsync();

        sut.RailSections.Select(section => section.Id).Should().Equal("reading", "shelf:收藏");
        sut.RailSections[0].Books.Select(item => item.Book.Id).Should().Equal("a");
        sut.RailSections[1].Books.Select(item => item.Book.Id).Should().Equal("b");
        sut.UnshelvedBooks.Select(item => item.Book.Id).Should().Equal("a", "c");
        sut.HasNovelStorageWarnings.Should().BeTrue();
        sut.NovelStorageWarnings.Should().Equal("broken/metadata.json");
    }

    [Fact]
    public async Task MoveBooksToShelfCommand_RebuildsProjectionFromServiceResult()
    {
        var library = new RecordingNovelLibraryService
        {
            Books =
            [
                new NovelBook { Id = "a", Title = "A" },
                new NovelBook { Id = "b", Title = "B" },
            ],
        };
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState([], ["a", "b"])));
        shelves.Setup(service => service.MoveBooksAsync(
                It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "a" })),
                "收藏",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState(
                [new NovelShelf("收藏", ["a"])],
                ["b"])));
        var sut = CreateSut(novelService: library, shelfService: shelves.Object);
        await sut.InitializeAsync();

        await sut.MoveBooksToShelfCommand.ExecuteAsync(
            new NovelShelfMoveRequest(["a"], "收藏"));

        sut.RailSections.Should().ContainSingle();
        sut.RailSections[0].Books.Select(item => item.Book.Id).Should().Equal("a");
        sut.UnshelvedBooks.Select(item => item.Book.Id).Should().Equal("b");
        shelves.VerifyAll();
    }

    private static NovelLibraryPageViewModel CreateSut(
        INovelLibraryService? novelService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        FakeMessenger? messenger = null,
        ISasayakiMatchService? sasayakiMatchService = null,
        ISettingsService? settingsService = null,
        INovelStatisticsDashboardService? statisticsDashboardService = null,
        INovelShelfService? shelfService = null,
        ITtuSyncRemoteStore? syncRemoteStore = null,
        ITtuBookImportService? ttuBookImportService = null,
        IGoogleDriveAuthService? googleDriveAuthService = null
    )
    {
        var serviceMock = new Mock<INovelLibraryService>();
        serviceMock
            .Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBookCatalogSnapshot>.Success(
                new NovelBookCatalogSnapshot([], [])));
        var dashboardMock = new Mock<INovelStatisticsDashboardService>();
        dashboardMock
            .Setup(s => s.LoadSnapshotAsync(
                It.IsAny<IReadOnlyList<NovelBook>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NovelStatisticsDashboardSnapshot([], []));

        return new NovelLibraryPageViewModel(
            novelService ?? serviceMock.Object,
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            messenger ?? new FakeMessenger(),
            sasayakiMatchService ?? Mock.Of<ISasayakiMatchService>(),
            settingsService ?? Mock.Of<ISettingsService>(s => s.Current == new AppSettings()),
            statisticsDashboardService ?? dashboardMock.Object,
            shelfService ?? CreateDefaultShelfService(),
            syncRemoteStore ?? new FakeTtuSyncRemoteStore(),
            ttuBookImportService ?? new FakeTtuBookImportService(),
            googleDriveAuthService ?? new FakeGoogleDriveAuthService { HasCredentials = true }
        );
    }

    private static INovelShelfService CreateDefaultShelfService()
    {
        var service = new Mock<INovelShelfService>();
        service.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState([], [])));
        return service.Object;
    }

    private static TtuRemoteBook RemoteBook(
        string id,
        string title,
        string sanitizedTitle) =>
        new(
            id,
            title,
            sanitizedTitle,
            new TtuRemoteBookFiles(
                Progress: new TtuRemoteFile($"{id}-progress", "progress_1_6_2000_0.25.json"),
                Statistics: null,
                AudioBook: null,
                BookData: new TtuRemoteFile($"{id}-bookdata", "bookdata_1_6_1200_2000_1000.zip"),
                Cover: null),
            Progress: 0.25);

    private static void SetSortOption(NovelLibraryPageViewModel sut, string optionName)
    {
        var property = typeof(NovelLibraryPageViewModel).GetProperty("SelectedSortOption");
        property.Should().NotBeNull();
        var value = Enum.Parse(property!.PropertyType, optionName);
        property.SetValue(sut, value);
    }

    private static string GetSortOptionName(NovelLibraryPageViewModel sut)
    {
        var property = typeof(NovelLibraryPageViewModel).GetProperty("SelectedSortOption");
        property.Should().NotBeNull();
        return property!.GetValue(sut)?.ToString() ?? string.Empty;
    }

    private static IAsyncRelayCommand GetAsyncCommand(NovelLibraryPageViewModel sut, string propertyName)
    {
        var property = typeof(NovelLibraryPageViewModel).GetProperty(propertyName);
        property.Should().NotBeNull();
        property!.GetValue(sut).Should().BeAssignableTo<IAsyncRelayCommand>();
        return (IAsyncRelayCommand)property.GetValue(sut)!;
    }

    private static object CreateMoveRequest(string sourceBookId, string targetBookId)
    {
        var requestType = typeof(NovelLibraryPageViewModel).Assembly.GetType(
            "Hoshi.ViewModels.Pages.NovelBookMoveRequest",
            throwOnError: false);
        requestType.Should().NotBeNull();
        return Activator.CreateInstance(requestType!, sourceBookId, targetBookId)!;
    }

    private sealed class RecordingNovelLibraryService : INovelLibraryService
    {
        public IReadOnlyList<NovelBook> Books { get; init; } = [];
        public List<string> ImportedPaths { get; } = [];
        public List<IReadOnlyList<string>> SavedOrders { get; } = [];

        public Task<Result<NovelBookCatalogSnapshot>> GetNovelBooksAsync(
            string? queryText = null,
            CancellationToken ct = default) =>
            Task.FromResult(Result<NovelBookCatalogSnapshot>.Success(
                new NovelBookCatalogSnapshot(Books, [])));

        public Task<Result<NovelBook>> ImportEpubAsync(string filePath, CancellationToken ct = default)
        {
            ImportedPaths.Add(filePath);
            return Task.FromResult(Result<NovelBook>.Success(new NovelBook
            {
                Id = Path.GetFileNameWithoutExtension(filePath),
                Title = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
            }));
        }

        public Task<Result<NovelBook?>> GetNovelBookAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result<NovelBook?>.Success(Books.FirstOrDefault(book => book.Id == bookId)));

        public Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SaveProgressAsync(
            string bookId,
            int chapterIndex,
            double progress,
            int currentCharacterCount,
            int totalCharacterCount,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SaveNovelBookOrderAsync(
            IReadOnlyList<string> orderedBookIds,
            CancellationToken ct = default)
        {
            SavedOrders.Add(orderedBookIds.ToList());
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetNovelProfileAsync(
            string bookId,
            string? profileId,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class FakeTtuSyncRemoteStore : ITtuSyncRemoteStore
    {
        public IReadOnlyList<TtuRemoteBook> RemoteBooks { get; init; } = [];

        public Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(CancellationToken ct = default) =>
            Task.FromResult(RemoteBooks);

        public Task DownloadBookDataAsync(TtuRemoteFile file, string destinationFilePath, IProgress<double>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;

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

    private sealed class FakeTtuBookImportService : ITtuBookImportService
    {
        public TtuRemoteBook? ImportedBook { get; private set; }

        public Task<Result<NovelBook>> ImportRemoteBookAsync(
            TtuRemoteBook remoteBook,
            TtuBookImportOptions options,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            ImportedBook = remoteBook;
            return Task.FromResult(Result<NovelBook>.Success(new NovelBook
            {
                Id = remoteBook.Id,
                Title = remoteBook.Title,
                FilePath = "D:\\Books\\remote.epub",
            }));
        }
    }

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials { get; init; }

        public Task AuthenticateAsync(string clientId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("token");

        public Task SignOutAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
