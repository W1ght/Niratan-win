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
        xaml.Should().Contain("<UniformGridLayout");
        xaml.Should().NotContain("HorizontalScrollMode=\"Enabled\"");
        xaml.Should().NotContain("NovelUnshelvedBooksRepeater");
        code.Should().NotContain("NovelBookButton_Click");
    }
}
