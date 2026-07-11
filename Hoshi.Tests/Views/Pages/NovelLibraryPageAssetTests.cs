using FluentAssertions;

namespace Hoshi.Tests.Views.Pages;

public sealed class NovelLibraryPageAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

    [Fact]
    public void ShelfSections_UseExplicitCommandsAndWrappingGrid()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs"));

        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.ShelfSections, Mode=OneWay}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.OpenNovelCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("CommandParameter=\"{x:Bind}\"");
        xaml.Should().Contain("Click=\"DeleteNovelMenuItem_Click\"");
        code.Should().Contain("ViewModel.DeleteNovelCommand.ExecuteAsync(novelItem)");
        xaml.Should().Contain("<UniformGridLayout");
        xaml.Should().NotContain("HorizontalScrollMode=\"Enabled\"");
        xaml.Should().NotContain("NovelUnshelvedBooksRepeater");
        code.Should().NotContain("NovelBookButton_Click");
    }

    [Fact]
    public void RemoteNovelBookTemplate_ExposesExplicitDeleteAction()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"RemoteNovelBookDeleteMenuItem\"");
        xaml.Should().Contain("Click=\"DeleteRemoteNovelMenuItem_Click\"");
        code.Should().Contain("ViewModel.DeleteRemoteBookCommand.ExecuteAsync(remoteItem)");
    }
}
