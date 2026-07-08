using FluentAssertions;
using CommunityToolkit.Mvvm.Input;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Settings;
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
                Result<IReadOnlyList<NovelBook>>.Success(
                    [
                        new NovelBook
                        {
                            Id = "book-1",
                            Title = "Book One",
                            FilePath = "D:\\Books\\one.epub",
                        },
                    ]
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

    private static NovelLibraryPageViewModel CreateSut(
        INovelLibraryService? novelService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        FakeMessenger? messenger = null,
        ISasayakiMatchService? sasayakiMatchService = null,
        ISettingsService? settingsService = null,
        INovelStatisticsDashboardService? statisticsDashboardService = null
    )
    {
        var serviceMock = new Mock<INovelLibraryService>();
        serviceMock
            .Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NovelBook>>.Success([]));
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
            statisticsDashboardService ?? dashboardMock.Object
        );
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

        public Task<Result<IReadOnlyList<NovelBook>>> GetNovelBooksAsync(
            string? queryText = null,
            CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<NovelBook>>.Success(Books));

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
}
