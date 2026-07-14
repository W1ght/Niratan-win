using FluentAssertions;

namespace Hoshi.Tests.Services.Sync;

public sealed class NovelLibraryTtuSyncAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "Hoshi"));

    [Fact]
    public void NovelLibraryPage_ExposesGoogleDriveBookRefreshAndRemoteBooks()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibraryRefreshGoogleDriveButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"RemoteNovelBookItemsControl\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.RefreshRemoteBooksCommand}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.DownloadRemoteBookCommand, ElementName=ThisPage}\"");
        appCode.Should().Contain("AddSingleton<ITtuBookImportService, TtuBookImportService>");
        appCode.Should().Contain("AddSingleton<ITtuBookDataConverter, TtuBookDataConverter>");
    }

    [Fact]
    public void NovelLibraryPage_ExposesNiratanPerBookSyncMenu()
    {
        var xaml = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Views",
            "Pages",
            "NovelLibraryPage.xaml"));
        var code = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Views",
            "Pages",
            "NovelLibraryPage.xaml.cs"));
        var en = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Strings",
            "en-US",
            "Resources.resw"));
        var zh = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Strings",
            "zh-CN",
            "Resources.resw"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncMenuItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncSubmenu\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncImportMenuItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncExportMenuItem\"");
        code.Should().Contain("ViewModel.ShowAutomaticBookSyncAction");
        code.Should().Contain("ViewModel.ShowManualBookSyncAction");
        code.Should().Contain("ViewModel.SyncNovelCommand");
        code.Should().Contain("ViewModel.ImportNovelFromTtuCommand");
        code.Should().Contain("ViewModel.ExportNovelCommand");

        foreach (var key in new[]
        {
            "NovelBookSyncMenuItem.Text",
            "NovelBookSyncSubmenu.Text",
            "NovelBookSyncImportMenuItem.Text",
            "NovelBookSyncExportMenuItem.Text",
            "NovelBookSyncUnavailableTitle",
            "NovelBookSyncUnavailableMessage",
            "NovelBookAlreadySyncedFormat",
            "NovelBookSyncedFromTtuFormat",
            "NovelBookSyncedToTtuFormat",
            "NovelBookSyncFailedTitle",
            "NovelBookSyncFailedFormat",
        })
        {
            en.Should().Contain(key);
            zh.Should().Contain(key);
        }
    }
}
