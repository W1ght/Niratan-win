using FluentAssertions;
using Niratan.Models;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class NovelShelfSectionViewModelTests
{
    [Fact]
    public void CollapseCommands_ToggleAndExpandCollapsibleShelf()
    {
        var sut = new NovelShelfSectionViewModel
        {
            Id = "shelf:收藏",
            DisplayName = "收藏",
            CanCollapse = true,
            IsCollapsed = true,
            Books =
            [
                new NovelBookItemViewModel(new NovelBook
                {
                    Id = "book-1",
                    Title = "Book One",
                    FilePath = "D:\\Books\\one.epub",
                }),
            ],
        };

        sut.ShowsCollapsedPreview.Should().BeTrue();
        sut.ToggleCollapseCommand.Execute(null);
        sut.IsCollapsed.Should().BeFalse();
        sut.ShowsFullContent.Should().BeTrue();

        sut.ToggleCollapseCommand.Execute(null);
        sut.ExpandCommand.Execute(null);

        sut.IsCollapsed.Should().BeFalse();
    }

    [Fact]
    public void CollapseCommands_LeaveUnshelvedSectionExpanded()
    {
        var sut = new NovelShelfSectionViewModel
        {
            Id = "unshelved",
            DisplayName = "Unshelved",
            IsUnshelved = true,
        };

        sut.ToggleCollapseCommand.Execute(null);

        sut.IsCollapsed.Should().BeFalse();
        sut.ShowsFullContent.Should().BeTrue();
        sut.ShowsCollapsedPreview.Should().BeFalse();
    }
}
