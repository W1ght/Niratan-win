using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class DownloadsPageAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void Downloads_page_exposes_discovery_tasks_subscriptions_and_settings_sections()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DownloadsPage.xaml"));

        xaml.Should().Contain("DownloadsDiscoveryTab");
        xaml.Should().Contain("DownloadsTasksTab");
        xaml.Should().Contain("DownloadsSubscriptionsTab");
        xaml.Should().Contain("DownloadsSettingsTab");
        xaml.Should().Contain("PaneDisplayMode=\"Top\"");
        xaml.Should().Contain("DownloadsSectionNavigation_ItemInvoked");
        xaml.Should().Contain("DownloadsSearchResults");
        xaml.Should().Contain("DownloadsTaskList");
        xaml.Should().Contain("DownloadsMonoTorrentTaskList");
        xaml.Should().Contain("DownloadsSubscriptionsList");
        xaml.Should().Contain("DownloadsCheckAllSubscriptionsButton");
        xaml.Should().Contain("ToggleSubscriptionCommand");
        xaml.Should().Contain("CheckSubscriptionCommand");
        xaml.Should().Contain("RemoveSubscriptionCommand");
        xaml.Should().Contain("Width=\"40\"");
        xaml.Should().Contain("Height=\"60\"");
        xaml.Should().Contain("Source=\"{x:Bind PosterImage, Mode=OneTime}\"");
        xaml.Should().Contain("DownloadsBackendBox");
        xaml.Should().Contain("DownloadsMonoTorrentTrackersBox");
        xaml.Should().Contain("DownloadsMonoTorrentDownloadRootBox");
        xaml.Should().Contain("DownloadsMonoTorrentBrowseDownloadRootButton");
        xaml.Should().Contain("DownloadsMonoTorrentResetDownloadRootButton");
        xaml.Should().Contain("DownloadsMonoTorrentListenPortBox");
        xaml.Should().Contain("DownloadsMonoTorrentMaximumConnectionsBox");
        xaml.Should().Contain("DownloadsMonoTorrentPerTorrentConnectionsBox");
        xaml.Should().Contain("DownloadsMonoTorrentDownloadLimitBox");
        xaml.Should().Contain("DownloadsMonoTorrentUploadLimitBox");
        xaml.Should().Contain("DownloadsMonoTorrentPortForwardingCheckBox");
        xaml.Should().Contain("DownloadsMonoTorrentDhtCheckBox");
        xaml.Should().Contain("DownloadsMonoTorrentPeerExchangeCheckBox");
        xaml.Should().Contain("DownloadsMonoTorrentLocalPeerDiscoveryCheckBox");
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
        codeBehind.Should().Contain("public enum DownloadsPageSection");
        codeBehind.Should().Contain("DownloadsPageSection.Subscriptions");
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
