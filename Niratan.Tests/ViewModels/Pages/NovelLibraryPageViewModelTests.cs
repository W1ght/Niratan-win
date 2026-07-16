using System.Collections.Concurrent;
using FluentAssertions;
using CommunityToolkit.Mvvm.Input;
using Moq;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;
using Niratan.Models.Settings;
using Niratan.Models.Sync;
using Niratan.Services.Novels;
using Niratan.Services.Sasayaki;
using Niratan.Services.Settings;
using Niratan.Services.Sync;
using Niratan.Services.UI;
using Niratan.Tests.TestUtils;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

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
    public async Task InitializeAsync_UsesRecentWhenNoSortPreferenceHasBeenStored()
    {
        var settings = new AppSettings();
        var service = Mock.Of<ISettingsService>(value => value.Current == settings);
        var sut = CreateSut(settingsService: service);

        await sut.InitializeAsync();

        sut.SelectedSortOption.Should().Be(NovelLibrarySortOption.Recent);
    }

    [Fact]
    public async Task InitializeAsync_ReflectsTheCurrentPersistedSortPreference()
    {
        var settings = new AppSettings
        {
            NovelLibrarySortOption = NovelLibrarySortOption.Recent,
        };
        var service = Mock.Of<ISettingsService>(value => value.Current == settings);
        var sut = CreateSut(settingsService: service);
        settings.NovelLibrarySortOption = NovelLibrarySortOption.Manual;

        await sut.InitializeAsync();

        sut.SelectedSortOption.Should().Be(NovelLibrarySortOption.Manual);
    }

    [Fact]
    public async Task InitializeAsync_ExposesThePersistedSortOptionInstanceForTheComboBox()
    {
        var settings = new AppSettings
        {
            NovelLibrarySortOption = NovelLibrarySortOption.Title,
        };
        var service = Mock.Of<ISettingsService>(value => value.Current == settings);
        var sut = CreateSut(settingsService: service);

        await sut.InitializeAsync();

        sut.SelectedSortOptionItem.Should().BeSameAs(
            sut.SortOptions.Single(option => option.Value == NovelLibrarySortOption.Title));
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
    public async Task ExportNovelCommand_PickerCancellationDoesNothing()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.SaveFilePickerAsync("星_空", "EPUB books", ".epub"))
            .ReturnsAsync((string?)null);
        var library = new Mock<INovelLibraryService>();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
            new NovelBook { Id = "book-a", Title = "星?空.epub" }));

        library.Verify(service => service.ExportEpubAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        notification.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportNovelCommand_SuccessExportsSelectedBookAndNotifies()
    {
        var destination = @"D:\Exports\星.epub";
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.SaveFilePickerAsync("星", "EPUB books", ".epub"))
            .ReturnsAsync(destination);
        var library = new Mock<INovelLibraryService>();
        library.Setup(service => service.ExportEpubAsync(
                "book-a", destination, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
            new NovelBook { Id = "book-a", Title = "星" }));

        notification.Verify(service => service.ShowSuccess("EPUB exported.", "Novel exported"));
    }

    [Fact]
    public async Task ExportNovelCommand_FailureShowsServiceError()
    {
        var destination = @"D:\Exports\星.epub";
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.SaveFilePickerAsync("星", "EPUB books", ".epub"))
            .ReturnsAsync(destination);
        var library = new Mock<INovelLibraryService>();
        library.Setup(service => service.ExportEpubAsync(
                "book-a", destination, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("source missing", "EPUB file not found"));
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
            new NovelBook { Id = "book-a", Title = "星" }));

        notification.Verify(service => service.ShowError("source missing", "EPUB file not found"));
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
    public async Task StatisticsSurfaceCommands_ActivateAndDeactivateDashboard()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        await sut.EnterStatisticsCommand.ExecuteAsync(null);

        sut.ShowStatisticsDashboard.Should().BeTrue();
        sut.ShowBookshelf.Should().BeFalse();
        sut.StatisticsDashboard.IsActive.Should().BeTrue();

        sut.ReturnToBookshelfCommand.Execute(null);

        sut.ShowStatisticsDashboard.Should().BeFalse();
        sut.ShowBookshelf.Should().BeTrue();
        sut.StatisticsDashboard.IsActive.Should().BeFalse();
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
                Matches =
                [
                    new SasayakiMatch
                    {
                        Id = "0",
                        Text = "星",
                        StartTime = 0,
                        EndTime = 1,
                        ChapterIndex = 0,
                        Length = 1,
                    },
                ],
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
    public async Task RefreshRemoteBooksCommand_HydratesCoversWithoutReorderingCards()
    {
        using var temp = new TempDirectory();
        var firstPath = WritePng(temp.Path, "first.png");
        var secondPath = WritePng(temp.Path, "second.png");
        var remote = new FakeTtuSyncRemoteStore
        {
            RemoteBooks =
            [
                RemoteBook("a", "A", "A", "cover-a"),
                RemoteBook("b", "B", "B", "cover-b"),
            ],
        };
        var covers = new FakeGoogleDriveCoverCacheService(new Dictionary<string, string>
        {
            ["cover-a"] = firstPath,
            ["cover-b"] = secondPath,
        });
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        });
        var sut = CreateSut(
            syncRemoteStore: remote,
            googleDriveCoverCacheService: covers,
            settingsService: settings);

        await sut.RefreshRemoteBooksCommand.ExecuteAsync(null);

        sut.RemoteBooks.Select(item => item.Book.Id).Should().Equal("a", "b");
        sut.RemoteBooks.Select(item => item.CoverPath)
            .Should().Equal(firstPath, secondPath);
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
    public async Task DeleteRemoteBookCommand_ConfirmsTrashesAndRemovesRemoteBook()
    {
        var remote = new FakeTtuSyncRemoteStore();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                "Delete Google Drive book",
                It.Is<string>(message => message.Contains("CLOUD", StringComparison.Ordinal))))
            .ReturnsAsync(true);
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            syncRemoteStore: remote,
            dialogService: dialog.Object,
            notificationService: notification.Object);
        var item = RemoteItem("cloud");
        sut.RemoteBooks = new([item]);

        await sut.DeleteRemoteBookCommand.ExecuteAsync(item);

        remote.TrashedBooks.Should().Equal(item.Book);
        sut.RemoteBooks.Should().BeEmpty();
        notification.Verify(service => service.ShowSuccess(
            "Remote book moved to Google Drive trash.",
            "Google Drive book deleted"));
    }

    [Fact]
    public async Task MarkReadNovelCommand_ConfirmedMarksAndReloadsWithoutSuccessNotification()
    {
        var item = BookItem("book-a");
        var completedBook = new NovelBook
        {
            Id = "book-a",
            Title = "Book A",
            CurrentCharacterCount = 9000,
            TotalCharacterCount = 9000,
        };
        var library = new Mock<INovelLibraryService>();
        library.Setup(service => service.MarkReadAsync(
                "book-a",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        library.Setup(service => service.GetNovelBooksAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelBookCatalogSnapshot>.Success(
                new NovelBookCatalogSnapshot([completedBook], [])));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                It.Is<string>(title => title.Contains("book-a", StringComparison.Ordinal)),
                string.Empty,
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text)),
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text))))
            .ReturnsAsync(true);
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.MarkReadNovelCommand.ExecuteAsync(item);

        sut.NovelBooks.Should().ContainSingle()
            .Which.Book.Should().BeSameAs(completedBook);
        library.VerifyAll();
        dialog.VerifyAll();
        notification.Verify(service => service.ShowSuccess(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MarkReadNovelCommand_CancelledDoesNotWriteOrReload()
    {
        var item = BookItem("book-a");
        var library = new Mock<INovelLibraryService>();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                string.Empty,
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text)),
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text))))
            .ReturnsAsync(false);
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object);

        await sut.MarkReadNovelCommand.ExecuteAsync(item);

        library.Verify(service => service.MarkReadAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        library.Verify(service => service.GetNovelBooksAsync(
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarkReadNovelCommand_FailureShowsErrorWithoutReloading()
    {
        var item = BookItem("book-a");
        var library = new Mock<INovelLibraryService>();
        library.Setup(service => service.MarkReadAsync(
                "book-a",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("disk full", "Mark read failed"));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ConfirmAsync(
                It.IsAny<string>(),
                string.Empty,
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text)),
                It.Is<string>(text => !string.IsNullOrWhiteSpace(text))))
            .ReturnsAsync(true);
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            novelService: library.Object,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.MarkReadNovelCommand.ExecuteAsync(item);

        notification.Verify(service => service.ShowError(
            "disk full",
            "Mark read failed"), Times.Once);
        library.Verify(service => service.GetNovelBooksAsync(
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadRemoteBookCommand_RunsThreeImportsAndQueuesFourth()
    {
        var importer = new ControlledTtuBookImportService();
        var sut = CreateSut(ttuBookImportService: importer);
        sut.RemoteBooks = new([
            RemoteItem("a"),
            RemoteItem("b"),
            RemoteItem("c"),
            RemoteItem("d"),
        ]);

        var tasks = sut.RemoteBooks
            .Select(item => sut.DownloadRemoteBookCommand.ExecuteAsync(item))
            .ToArray();
        await importer.WaitForStartedCountAsync(3);

        importer.StartedIds.Should().BeEquivalentTo(["a", "b", "c"]);
        sut.RemoteBooks.Single(item => item.Book.Id == "d").DownloadState
            .Should().Be(RemoteNovelDownloadState.Queued);

        importer.Complete("a");
        await importer.WaitForStartedCountAsync(4);
        importer.StartedIds.Should().Contain("d");
        importer.CompleteAll();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task CompletedImport_DoesNotCancelAnotherActiveImport()
    {
        var importer = new ControlledTtuBookImportService();
        var sut = CreateSut(ttuBookImportService: importer);
        var first = RemoteItem("a");
        var second = RemoteItem("b");
        sut.RemoteBooks = new([first, second]);
        var firstTask = sut.DownloadRemoteBookCommand.ExecuteAsync(first);
        var secondTask = sut.DownloadRemoteBookCommand.ExecuteAsync(second);
        await importer.WaitForStartedCountAsync(2);

        importer.Complete("a");
        await firstTask;

        importer.TokenFor("b").IsCancellationRequested.Should().BeFalse();
        importer.Complete("b");
        await secondTask;
    }

    [Fact]
    public async Task FailedImport_LeavesOtherImportsRunningAndCardRetryable()
    {
        var importer = new ControlledTtuBookImportService();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            ttuBookImportService: importer,
            notificationService: notification.Object);
        var failed = RemoteItem("a");
        var active = RemoteItem("b");
        sut.RemoteBooks = new([failed, active]);
        var failedTask = sut.DownloadRemoteBookCommand.ExecuteAsync(failed);
        var activeTask = sut.DownloadRemoteBookCommand.ExecuteAsync(active);
        await importer.WaitForStartedCountAsync(2);

        importer.Fail("a", "network");
        await failedTask;

        failed.DownloadState.Should().Be(RemoteNovelDownloadState.Failed);
        failed.CanRetry.Should().BeTrue();
        importer.TokenFor("b").IsCancellationRequested.Should().BeFalse();
        importer.Complete("b");
        await activeTask;
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
            BookshelfShowReading = false,
            NovelLibrarySortOption = NovelLibrarySortOption.Manual,
        });
        var sut = CreateSut(
            novelService: library.Object,
            shelfService: shelves.Object,
            settingsService: settings);

        await sut.InitializeAsync();

        sut.ShelfSections.Select(section => section.Id)
            .Should().Equal("reading", "shelf:收藏", "unshelved");
        sut.ShelfSections[0].Books.Select(item => item.Book.Id).Should().Equal("a");
        sut.ShelfSections[1].Books.Select(item => item.Book.Id).Should().Equal("b");
        sut.ShelfSections[2].Books.Select(item => item.Book.Id).Should().Equal("a", "c");
        sut.HasNovelStorageWarnings.Should().BeTrue();
        sut.NovelStorageWarnings.Should().Equal("broken/metadata.json");
    }

    [Fact]
    public async Task InitializeAsync_OmitsReadingWhenNoBookIsInProgress()
    {
        var library = new RecordingNovelLibraryService
        {
            Books =
            [
                new NovelBook { Id = "new", Title = "New" },
                new NovelBook
                {
                    Id = "done",
                    Title = "Done",
                    CurrentCharacterCount = 10,
                    TotalCharacterCount = 10,
                },
            ],
        };
        var sut = CreateSut(novelService: library);

        await sut.InitializeAsync();

        sut.ShelfSections.Select(section => section.Id).Should().Equal("unshelved");
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

        sut.ShelfSections.Select(section => section.Id)
            .Should().Equal("shelf:收藏", "unshelved");
        sut.ShelfSections[0].Books.Select(item => item.Book.Id).Should().Equal("a");
        sut.ShelfSections[1].Books.Select(item => item.Book.Id).Should().Equal("b");
        shelves.VerifyAll();
    }

    [Theory]
    [InlineData("SyncNovelCommand", TtuSyncDirection.Auto)]
    [InlineData("ImportNovelFromTtuCommand", TtuSyncDirection.ImportFromTtu)]
    [InlineData("ExportNovelToTtuCommand", TtuSyncDirection.ExportToTtu)]
    public async Task BookSyncCommands_MapDirectionAndPayloadPreferences(
        string commandName,
        TtuSyncDirection expectedDirection)
    {
        var sync = new RecordingTtuSyncService();
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings
            {
                EnableSync = true,
                UploadBooks = true,
            },
            StatisticsSettings = new NovelStatisticsSettings
            {
                EnableSync = true,
                SyncMode = StatisticsSyncMode.Replace,
            },
            SasayakiSettings = new SasayakiSettings
            {
                EnableSasayaki = true,
                EnableSync = true,
            },
        });
        var sut = CreateSut(settingsService: settings, ttuSyncService: sync);
        var item = new NovelBookItemViewModel(new NovelBook
        {
            Id = "book-1",
            Title = "星を読む",
            ExtractedPath = "D:\\Books\\book-1",
        });

        await GetAsyncCommand(sut, commandName).ExecuteAsync(item);

        sync.Calls.Should().ContainSingle();
        var call = sync.Calls.Single();
        call.Options.Direction.Should().Be(expectedDirection);
        call.Options.SyncBookData.Should().BeTrue();
        call.Options.SyncStatistics.Should().BeTrue();
        call.Options.StatisticsSyncMode.Should().Be(StatisticsSyncMode.Replace);
        call.Options.SyncAudioBook.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, TtuSettingsSyncMode.Auto, true, false)]
    [InlineData(true, TtuSettingsSyncMode.Manual, false, true)]
    [InlineData(false, TtuSettingsSyncMode.Auto, false, false)]
    public void BookSyncActionVisibility_FollowsGlobalSyncMode(
        bool enabled,
        TtuSettingsSyncMode mode,
        bool showAutomatic,
        bool showManual)
    {
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings
            {
                EnableSync = enabled,
                SyncMode = mode,
            },
        });
        var sut = CreateSut(settingsService: settings);

        sut.ShowAutomaticBookSyncAction.Should().Be(showAutomatic);
        sut.ShowManualBookSyncAction.Should().Be(showManual);
    }

    [Fact]
    public async Task SyncNovelCommand_WhenSyncUnavailable_DoesNotCallServiceAndShowsError()
    {
        var sync = new RecordingTtuSyncService();
        var notification = new Mock<INotificationService>();
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = false },
        });
        var sut = CreateSut(
            settingsService: settings,
            googleDriveAuthService: new FakeGoogleDriveAuthService { HasCredentials = false },
            notificationService: notification.Object,
            ttuSyncService: sync);

        await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

        sync.Calls.Should().BeEmpty();
        sut.IsBookSyncing.Should().BeFalse();
        notification.Verify(service => service.ShowError(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SyncNovelCommand_WhenImported_ReloadsCatalogAndShowsSuccess()
    {
        var library = new RecordingNovelLibraryService();
        var notification = new Mock<INotificationService>();
        var sync = new RecordingTtuSyncService
        {
            Handler = (book, _, _) => Task.FromResult(new TtuSyncResult(
                TtuSyncResultKind.Imported,
                book.Title,
                321)),
        };
        var sut = CreateSut(
            novelService: library,
            notificationService: notification.Object,
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);

        await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

        library.LoadCount.Should().Be(1);
        notification.Verify(service => service.ShowSuccess(
            It.Is<string>(message => message.Contains("321", StringComparison.Ordinal)),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SyncNovelCommand_WhenSkipped_DoesNotShowSuccess()
    {
        var notification = new Mock<INotificationService>();
        var sync = new RecordingTtuSyncService
        {
            Handler = (book, _, _) => Task.FromResult(new TtuSyncResult(
                TtuSyncResultKind.Skipped,
                book.Title)),
        };
        var sut = CreateSut(
            notificationService: notification.Object,
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);

        await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

        notification.Verify(service => service.ShowSuccess(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SyncNovelCommand_WhenServiceFails_ShowsLocalizedError()
    {
        var notification = new Mock<INotificationService>();
        var sync = new RecordingTtuSyncService
        {
            Handler = (_, _, _) => Task.FromException<TtuSyncResult>(
                new InvalidOperationException("network down")),
        };
        var sut = CreateSut(
            notificationService: notification.Object,
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);

        await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

        sut.IsBookSyncing.Should().BeFalse();
        notification.Verify(service => service.ShowError(
            It.Is<string>(message => message.Contains("network down", StringComparison.Ordinal)),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SyncNovelCommand_WhenCancelled_ShowsNoError()
    {
        var notification = new Mock<INotificationService>();
        var sync = new RecordingTtuSyncService
        {
            Handler = (_, _, _) => Task.FromCanceled<TtuSyncResult>(
                new CancellationToken(canceled: true)),
        };
        var sut = CreateSut(
            notificationService: notification.Object,
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);

        await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

        sut.IsBookSyncing.Should().BeFalse();
        notification.Verify(service => service.ShowError(
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SyncNovelCommand_DeduplicatesSameBookButAllowsDifferentBooks()
    {
        var gates = new ConcurrentDictionary<string, TaskCompletionSource<TtuSyncResult>>();
        var started = new SemaphoreSlim(0);
        var sync = new RecordingTtuSyncService
        {
            Handler = (book, _, _) =>
            {
                var gate = gates.GetOrAdd(
                    book.Id,
                    _ => new TaskCompletionSource<TtuSyncResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                started.Release();
                return gate.Task;
            },
        };
        var sut = CreateSut(
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);
        var ct = TestContext.Current.CancellationToken;

        var first = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));
        (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();
        sut.IsBookSyncing.Should().BeTrue();
        var duplicate = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));
        await duplicate;
        sut.IsBookSyncing.Should().BeTrue();
        var secondBook = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-2"));
        (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();
        sut.IsBookSyncing.Should().BeTrue();

        sync.Calls.Select(call => call.Book.Id).Should().Equal("book-1", "book-2");
        gates["book-1"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-1"));
        await first;
        sut.IsBookSyncing.Should().BeTrue();
        gates["book-2"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-2"));
        await secondBook;
        sut.IsBookSyncing.Should().BeFalse();
    }

    [Fact]
    public async Task SyncNovelCommand_KeepsBusyStateUntilEveryBookFinishes()
    {
        var gates = new ConcurrentDictionary<string, TaskCompletionSource<TtuSyncResult>>();
        var started = new SemaphoreSlim(0);
        var sync = new RecordingTtuSyncService
        {
            Handler = (book, _, _) =>
            {
                var gate = gates.GetOrAdd(
                    book.Id,
                    _ => new TaskCompletionSource<TtuSyncResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                started.Release();
                return gate.Task;
            },
        };
        var sut = CreateSut(
            settingsService: EnabledSyncSettings(),
            ttuSyncService: sync);
        var ct = TestContext.Current.CancellationToken;

        var first = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));
        var second = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-2"));
        (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();
        (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();

        sut.IsBookSyncing.Should().BeTrue();

        gates["book-1"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-1"));
        await first;
        sut.IsBookSyncing.Should().BeTrue();

        gates["book-2"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-2"));
        await second;
        sut.IsBookSyncing.Should().BeFalse();
    }

    [Fact]
    public async Task StatisticsControls_ReprojectRangeMetricsCalendarAndCorruptWarning()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var contribution = new NovelStatisticsBookContribution(
            "a", "A", null, 1_200, 600, true);
        var day = new NovelStatisticsDayAggregate(
            today, 1_200, 600, [contribution]);
        var snapshot = new NovelStatisticsDashboardSnapshot(
            today.AddYears(-1).AddDays(1),
            today,
            [day],
            [new NovelStatisticsBookRecord("a", "A", null, 2_000)],
            ["broken-book"]);
        var dashboard = new Mock<INovelStatisticsDashboardService>();
        dashboard.Setup(service => service.LoadSnapshotAsync(
                It.IsAny<IReadOnlyList<NovelBook>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var sut = CreateSut(
            settingsService: settings.Object,
            statisticsDashboardService: dashboard.Object);

        await sut.InitializeAsync();
        await sut.EnterStatisticsCommand.ExecuteAsync(null);
        var statistics = sut.StatisticsDashboard;
        statistics.SelectedRangeMode = NovelStatisticsRangeMode.Day;
        statistics.SelectedTrendMetric = NovelStatisticsTrendMetric.Duration;
        statistics.SelectedRankingMetric = NovelStatisticsBookRankingMetric.Duration;
        statistics.SelectedCalendarDay = statistics.CalendarDays.Single(day => day.Date == today);

        statistics.HasCorruptBooks.Should().BeTrue();
        statistics.RangeTitle.Should().Be(today.ToString("yyyy-MM-dd"));
        statistics.RangeText.Should().Contain("1,200");
        statistics.TrendPoints.Should().ContainSingle().Which.ValueText.Should().Be("10m");
        statistics.BookRankingRows.Should().ContainSingle().Which.ValueText.Should().Be("10m");
        statistics.CalendarDetail.Text.Should().Contain("1,200 chars").And.Contain("1 book");
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
        ITtuSyncService? ttuSyncService = null,
        ITtuSyncRemoteStore? syncRemoteStore = null,
        ITtuBookImportService? ttuBookImportService = null,
        IGoogleDriveAuthService? googleDriveAuthService = null,
        IGoogleDriveCoverCacheService? googleDriveCoverCacheService = null
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

        var effectiveSettings = settingsService
            ?? Mock.Of<ISettingsService>(s => s.Current == new AppSettings());
        var effectiveDashboardService = statisticsDashboardService
            ?? dashboardMock.Object;

        return new NovelLibraryPageViewModel(
            novelService ?? serviceMock.Object,
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            messenger ?? new FakeMessenger(),
            sasayakiMatchService ?? Mock.Of<ISasayakiMatchService>(),
            effectiveSettings,
            new NovelStatisticsDashboardViewModel(
                effectiveDashboardService,
                effectiveSettings),
            shelfService ?? CreateDefaultShelfService(),
            ttuSyncService ?? new RecordingTtuSyncService(),
            syncRemoteStore ?? new FakeTtuSyncRemoteStore(),
            ttuBookImportService ?? new FakeTtuBookImportService(),
            googleDriveAuthService ?? new FakeGoogleDriveAuthService { HasCredentials = true },
            googleDriveCoverCacheService ?? new FakeGoogleDriveCoverCacheService(
                new Dictionary<string, string>())
        );
    }

    private static INovelShelfService CreateDefaultShelfService()
    {
        var service = new Mock<INovelShelfService>();
        service.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState([], [])));
        return service.Object;
    }

    private static NovelBookItemViewModel BookItem(string id) => new(new NovelBook
    {
        Id = id,
        Title = id,
        ExtractedPath = $"D:\\Books\\{id}",
    });

    private static ISettingsService EnabledSyncSettings() =>
        Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        });

    private static TtuRemoteBook RemoteBook(
        string id,
        string title,
        string sanitizedTitle,
        string? coverId = null) =>
        new(
            id,
            title,
            sanitizedTitle,
            new TtuRemoteBookFiles(
                Progress: new TtuRemoteFile($"{id}-progress", "progress_1_6_2000_0.25.json"),
                Statistics: null,
                AudioBook: null,
                BookData: new TtuRemoteFile($"{id}-bookdata", "bookdata_1_6_1200_2000_1000.zip"),
                Cover: coverId == null
                    ? null
                    : new TtuRemoteFile(
                        coverId,
                        "cover_1_6.png",
                        ThumbnailLink: $"https://thumb.test/{coverId}=s220")),
            Progress: 0.25);

    private static RemoteNovelBookItemViewModel RemoteItem(string id) =>
        new(RemoteBook(id, id.ToUpperInvariant(), id.ToUpperInvariant()));

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static string WritePng(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, PngBytes);
        return path;
    }

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
            "Niratan.ViewModels.Pages.NovelBookMoveRequest",
            throwOnError: false);
        requestType.Should().NotBeNull();
        return Activator.CreateInstance(requestType!, sourceBookId, targetBookId)!;
    }

    private sealed class RecordingNovelLibraryService : INovelLibraryService
    {
        public IReadOnlyList<NovelBook> Books { get; init; } = [];
        public List<string> ImportedPaths { get; } = [];
        public List<IReadOnlyList<string>> SavedOrders { get; } = [];
        public int LoadCount { get; private set; }

        public Task<Result<NovelBookCatalogSnapshot>> GetNovelBooksAsync(
            string? queryText = null,
            CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult(Result<NovelBookCatalogSnapshot>.Success(
                new NovelBookCatalogSnapshot(Books, [])));
        }

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

        public Task<Result> MarkReadAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ExportEpubAsync(
            string bookId,
            string destinationPath,
            CancellationToken ct = default) =>
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
        public List<TtuRemoteBook> TrashedBooks { get; } = [];

        public Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(CancellationToken ct = default) =>
            Task.FromResult(RemoteBooks);

        public Task TrashRemoteBookAsync(TtuRemoteBook remoteBook, CancellationToken ct = default)
        {
            TrashedBooks.Add(remoteBook);
            return Task.CompletedTask;
        }

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

    private sealed class ControlledTtuBookImportService : ITtuBookImportService
    {
        private readonly ConcurrentDictionary<
            string,
            TaskCompletionSource<Result<NovelBook>>> _results = new();
        private readonly ConcurrentDictionary<string, CancellationToken> _tokens = new();
        private readonly SemaphoreSlim _started = new(0);
        private int _observedStarts;

        public ConcurrentQueue<string> StartedIds { get; } = new();

        public Task<Result<NovelBook>> ImportRemoteBookAsync(
            TtuRemoteBook remoteBook,
            TtuBookImportOptions options,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            StartedIds.Enqueue(remoteBook.Id);
            _tokens[remoteBook.Id] = ct;
            _started.Release();
            return _results.GetOrAdd(
                remoteBook.Id,
                _ => new TaskCompletionSource<Result<NovelBook>>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
        }

        public async Task WaitForStartedCountAsync(int count)
        {
            while (_observedStarts < count)
            {
                (await _started.WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken)).Should().BeTrue();
                _observedStarts++;
            }
        }

        public CancellationToken TokenFor(string id) => _tokens[id];

        public void Complete(string id) =>
            _results[id].SetResult(Result<NovelBook>.Success(new NovelBook
            {
                Id = id,
                Title = id,
                FilePath = $"D:\\Books\\{id}.epub",
            }));

        public void Fail(string id, string error) =>
            _results[id].SetResult(Result<NovelBook>.Failure(error, "Import failed"));

        public void CompleteAll()
        {
            foreach (var id in StartedIds.Distinct())
            {
                if (!_results[id].Task.IsCompleted)
                    Complete(id);
            }
        }
    }

    private sealed class FakeGoogleDriveCoverCacheService(
        IReadOnlyDictionary<string, string> paths) : IGoogleDriveCoverCacheService
    {
        public Task<string?> GetCoverPathAsync(
            TtuRemoteFile? cover,
            CancellationToken ct = default) =>
            Task.FromResult(
                cover != null && paths.TryGetValue(cover.Id, out var path)
                    ? path
                    : null);

        public Task ClearAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record TtuSyncCall(
        NovelBook Book,
        TtuSyncOptions Options,
        CancellationToken CancellationToken);

    private sealed class RecordingTtuSyncService : ITtuSyncService
    {
        public ConcurrentQueue<TtuSyncCall> Calls { get; } = new();

        public Func<NovelBook, TtuSyncOptions, CancellationToken, Task<TtuSyncResult>>?
            Handler { get; init; }

        public Task<TtuSyncResult> SyncBookAsync(
            NovelBook book,
            TtuSyncOptions options,
            CancellationToken ct = default)
        {
            Calls.Enqueue(new TtuSyncCall(book, options, ct));
            return Handler?.Invoke(book, options, ct)
                ?? Task.FromResult(new TtuSyncResult(
                    TtuSyncResultKind.Synced,
                    book.Title));
        }
    }

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials { get; init; }

        public Task AuthenticateAsync(
            string clientId,
            string clientSecret,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("token");

        public Task SignOutAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
