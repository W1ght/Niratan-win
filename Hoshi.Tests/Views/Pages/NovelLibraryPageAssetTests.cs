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

    [Fact]
    public void LocalNovelSyncFlyout_UsesExplicitUiBridgeInsteadOfCrossNamescopeBindings()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs"));

        xaml.Should().Contain("Opening=\"NovelBookContextFlyout_Opening\"");
        xaml.Should().Contain("Click=\"SyncNovelMenuItem_Click\"");
        xaml.Should().Contain("Click=\"ImportNovelFromTtuMenuItem_Click\"");
        xaml.Should().Contain("Click=\"ExportNovelMenuItem_Click\"");
        xaml.Should().NotContain(
            "Command=\"{Binding ViewModel.SyncNovelCommand, ElementName=ThisPage}\"");
        xaml.Should().NotContain(
            "Visibility=\"{Binding ViewModel.ShowAutomaticBookSyncAction");
        xaml.Should().NotContain(
            "Visibility=\"{Binding ViewModel.ShowManualBookSyncAction");

        code.Should().Contain(
            "automaticItem.Visibility = ViewModel.ShowAutomaticBookSyncAction");
        code.Should().Contain(
            "manualSubmenu.Visibility = ViewModel.ShowManualBookSyncAction");
        code.Should().Contain("ViewModel.SyncNovelCommand.ExecuteAsync(novelItem)");
        code.Should().Contain("ViewModel.ImportNovelFromTtuCommand.ExecuteAsync(novelItem)");
        code.Should().Contain("ViewModel.ExportNovelCommand.ExecuteAsync(novelItem)");
    }

    [Fact]
    public void BookCards_ClampTitlesToTwoLinesAndSyncMenuHasNoIcons()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));

        xaml.Should().NotContain("Glyph=\"&#xE895;\"");
        xaml.Should().NotContain("Glyph=\"&#xE896;\"");
        xaml.Should().NotContain("Glyph=\"&#xE898;\"");
        xaml.Should().NotContain("<MenuFlyoutItem.Icon>");
        xaml.Should().NotContain("<MenuFlyoutSubItem.Icon>");
        xaml.Split("MaxLines=\"2\"").Should().HaveCount(3);
        xaml.Split("TextTrimming=\"Clip\"").Should().HaveCount(3);
        xaml.Should().Contain("Width=\"180\"");
        xaml.Split("TextWrapping=\"WrapWholeWords\"").Should().HaveCount(3);
    }

    [Fact]
    public void CommandBar_UsesVisiblePrimaryActionsAndStatisticsIcon()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var englishResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var chineseResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        xaml.Should().Contain("<FontIcon Glyph=\"&#xE9D2;\" />");
        var primaryCommandsStart = xaml.IndexOf(
            "<CommandBar.PrimaryCommands>",
            StringComparison.Ordinal);
        var primaryCommandsEnd = xaml.IndexOf(
            "</CommandBar.PrimaryCommands>",
            StringComparison.Ordinal);
        var googleDriveButton = xaml.IndexOf(
            "NovelLibraryRefreshGoogleDriveButton",
            StringComparison.Ordinal);
        googleDriveButton.Should().BeGreaterThan(primaryCommandsStart)
            .And.BeLessThan(primaryCommandsEnd);

        foreach (var key in new[]
                 {
                     "NovelLibraryStatisticsButton.Label",
                     "NovelLibraryRefreshGoogleDriveButton.Label",
                     "ImportNovelButton.Label",
                 })
        {
            englishResources.Should().Contain($"name=\"{key}\"");
            chineseResources.Should().Contain($"name=\"{key}\"");
        }
    }

    [Fact]
    public void SortComboBox_BindsToTheSelectedOptionInstance()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));

        xaml.Should().Contain(
            "SelectedItem=\"{x:Bind ViewModel.SelectedSortOptionItem, Mode=TwoWay}\"");
        xaml.Should().NotContain(
            "SelectedValue=\"{x:Bind ViewModel.SelectedSortOption, Mode=TwoWay}\"");
    }

    [Fact]
    public void BookSyncProgressOverlay_IsBlockingLocalizedAndBoundToBusyState()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var englishResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var chineseResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncProgressOverlay\"");
        xaml.Should().Contain("Grid.RowSpan=\"3\"");
        xaml.Should().Contain("Canvas.ZIndex=\"100\"");
        xaml.Should().Contain(
            "Visibility=\"{x:Bind ViewModel.IsBookSyncing, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}\"");
        xaml.Should().Contain(
            "IsActive=\"{x:Bind ViewModel.IsBookSyncing, Mode=OneWay}\"");
        xaml.Should().Contain("x:Uid=\"NovelBookSyncProgressText\"");

        foreach (var key in new[]
                 {
                     "NovelBookSyncProgressOverlay.AutomationProperties.Name",
                     "NovelBookSyncProgressText.Text",
                 })
        {
            englishResources.Should().Contain($"name=\"{key}\"");
            chineseResources.Should().Contain($"name=\"{key}\"");
        }

        chineseResources.Should().Contain("<value>正在同步…</value>");
    }
}
