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
}
