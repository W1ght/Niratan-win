using FluentAssertions;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.Novels;
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

    private static NovelLibraryPageViewModel CreateSut(
        INovelLibraryService? novelService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        FakeMessenger? messenger = null
    )
    {
        var serviceMock = new Mock<INovelLibraryService>();
        serviceMock
            .Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NovelBook>>.Success([]));

        return new NovelLibraryPageViewModel(
            novelService ?? serviceMock.Object,
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            messenger ?? new FakeMessenger()
        );
    }
}
