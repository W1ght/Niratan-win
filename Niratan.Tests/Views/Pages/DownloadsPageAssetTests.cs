using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class DownloadsPageAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void Downloads_page_exposes_discovery_tasks_and_settings_sections()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DownloadsPage.xaml"));

        xaml.Should().Contain("DownloadsDiscoveryTab");
        xaml.Should().Contain("DownloadsTasksTab");
        xaml.Should().Contain("DownloadsSettingsTab");
        xaml.Should().Contain("PaneDisplayMode=\"Top\"");
        xaml.Should().Contain("DownloadsSectionNavigation_ItemInvoked");
        xaml.Should().Contain("DownloadsSearchResults");
        xaml.Should().Contain("DownloadsTaskList");
        xaml.Should().Contain("DownloadsMonoTorrentTaskList");
        xaml.Should().Contain("DownloadsBackendBox");
        xaml.Should().Contain("AddToBackendCommand");
        xaml.Should().Contain("DownloadsTaskDetailsDialog");
        xaml.Should().Contain("DownloadsTaskDetailsFilesList");
        xaml.Should().Contain("DownloadsTaskDetailsTrackersList");
        xaml.Should().Contain("DownloadsTaskDetailsCancelButton");
        xaml.Should().Contain("DownloadsTaskDetailsResumeButton");
        xaml.Should().Contain("DownloadsTaskDetailsOpenLocationButton");
        xaml.Should().Contain("DownloadsTaskDetailsDeleteButton");
        xaml.Should().Contain("MinWidth=\"1040\"");
        xaml.Should().Contain("DownloadsTaskProgressLabel");
        xaml.Should().Contain("DownloadsTaskAverageRateLabel");
        xaml.Should().Contain("DownloadsTaskDownloadedLabel");
        xaml.Should().Contain("DownloadsTaskCreatedByLabel");
        xaml.Should().Contain("ItemClick=\"TaskList_ItemClick\"");
        var codeBehind = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DownloadsPage.xaml.cs"));
        codeBehind.Should().Contain("ContentDialogMaxWidth");
        xaml.Should().Contain("DownloadsSaveSettingsButton");
        xaml.Should().Contain("DownloadsTestConnectionButton");
    }

    [Fact]
    public void Navigation_includes_downloads_page()
    {
        var navigation = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "UI", "NavigationService.cs"));

        navigation.Should().Contain("DownloadsNavItem");
        navigation.Should().Contain("Niratan.Views.Pages.DownloadsPage");
        service.Should().Contain("typeof(DownloadsPage) => AppPage.DownloadsPage");
    }
}
