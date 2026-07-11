using FluentAssertions;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Dialogs;
using Moq;

namespace Hoshi.Tests.ViewModels.Dialogs;

public sealed class NovelShelfManagementViewModelTests
{
    [Fact]
    public async Task CreateShelfCommand_ReplacesProjectionFromServiceResult()
    {
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState([], [])));
        shelves.Setup(service => service.CreateAsync("收藏", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState(
                [new NovelShelf("收藏", [])],
                [])));
        var sut = new NovelShelfManagementViewModel(
            shelves.Object,
            Mock.Of<INotificationService>());
        await sut.InitializeAsync();

        await sut.CreateShelfCommand.ExecuteAsync("收藏");

        sut.Shelves.Select(shelf => shelf.Name).Should().Equal("收藏");
        shelves.VerifyAll();
    }

    [Fact]
    public async Task RenameFailure_NotifiesAndDoesNotMutateProjectionOptimistically()
    {
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState(
                [new NovelShelf("收藏", [])],
                [])));
        shelves.Setup(service => service.RenameAsync(
                "收藏",
                "重复",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Failure("duplicate"));
        var notification = new Mock<INotificationService>();
        var sut = new NovelShelfManagementViewModel(shelves.Object, notification.Object);
        await sut.InitializeAsync();

        await sut.RenameShelfCommand.ExecuteAsync(
            new NovelShelfRenameRequest("收藏", "重复"));

        sut.Shelves.Select(shelf => shelf.Name).Should().Equal("收藏");
        notification.Verify(service => service.ShowError("duplicate", "Shelf update failed"), Times.Once);
    }
}
