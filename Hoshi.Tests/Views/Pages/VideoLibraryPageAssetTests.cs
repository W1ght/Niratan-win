using FluentAssertions;

namespace Hoshi.Tests.Views.Pages;

public class VideoLibraryPageAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi"));

    [Fact]
    public void VideoLibraryPage_DefinesNiratanStyleMinimalLibraryControls()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));

        xaml.Should().Contain("x:Name=\"VideoLibrarySecondaryNavigationView\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryAllNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryContinueWatchingNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryWatchedNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySearchBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySortComboBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ScanVideoFolderButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryLayoutSegment\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryListView\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoGridView\"");
        xaml.Should().Contain("x:Key=\"VideoListItemTemplate\"");
        xaml.Should().Contain("x:Key=\"VideoPosterItemTemplate\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"CreateSmartCollectionButton\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.CreateSmartCollectionCommand}\"");
        xaml.Should().Contain("VideoLibraryUnwatchedNavItem");
        xaml.Should().Contain("VideoLibraryFinishedNavItem");
        xaml.Should().Contain("VideoLibraryRecentNavItem");
        xaml.Should().Contain("VideoLibraryNeedsReviewNavItem");
        xaml.Should().Contain("VideoLibraryFavoritesNavItem");
        xaml.Should().Contain("VideoLibrarySeriesNavItem");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryFolderFilters\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryCollectionFilters\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryTagFilters\"");
        xaml.Should().Contain("Source=\"{x:Bind ArtworkImage");
        xaml.Should().Contain("Command=\"{Binding ViewModel.MarkWatchedCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.ClearProgressCommand, ElementName=ThisPage}\"");
    }

    [Fact]
    public void VideoLibraryPage_UsesLocalizedVisibleText()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(ProjectRoot, "ViewModels", "Pages", "VideoLibraryPageViewModel.cs"));
        var videoItemViewModel = File.ReadAllText(Path.Combine(ProjectRoot, "ViewModels", "Components", "VideoItemViewModel.cs"));
        var enResources = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var zhResources = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        foreach (var uid in new[]
        {
            "VideoLibrarySecondaryNavigationView",
            "VideoLibrarySectionHeader",
            "VideoLibraryAllNavItem",
            "VideoLibraryContinueWatchingNavItem",
            "VideoLibraryWatchedNavItem",
            "VideoLibraryUnwatchedNavItem",
            "VideoLibraryFinishedNavItem",
            "VideoLibraryRecentNavItem",
            "VideoLibraryNeedsReviewNavItem",
            "VideoLibraryFavoritesNavItem",
            "VideoLibrarySeriesNavItem",
            "VideoLibraryOrganizeSectionHeader",
            "VideoLibraryFoldersNavItem",
            "VideoLibraryCollectionsNavItem",
            "VideoLibraryTagsNavItem",
            "VideoLibraryLayoutList",
            "VideoLibraryLayoutPosters",
            "VideoLibrarySearchBox",
            "VideoLibrarySortComboBox",
            "ScanVideoFolderButton",
            "ImportVideoButton",
            "CreateSmartCollectionButton",
            "VideoLibrarySmartCollectionName",
            "VideoLibrarySmartCollectionRuleValue",
            "VideoLibraryMarkWatchedMenuItem",
            "VideoLibraryClearProgressMenuItem",
            "VideoLibraryDeleteMenuItem",
            "NoVideosText",
        })
        {
            xaml.Should().Contain($"x:Uid=\"{uid}\"");
        }

        viewModel.Should().Contain("ResourceStringHelper.GetString");
        videoItemViewModel.Should().Contain("ResourceStringHelper.GetString");

        foreach (var key in new[]
        {
            "VideoLibrarySecondaryNavigationView.PaneTitle",
            "VideoLibraryAllNavItem.Content",
            "VideoLibraryContinueWatchingNavItem.Content",
            "VideoLibraryWatchedNavItem.Content",
            "VideoLibraryUnwatchedNavItem.Content",
            "VideoLibraryFinishedNavItem.Content",
            "VideoLibraryRecentNavItem.Content",
            "VideoLibraryNeedsReviewNavItem.Content",
            "VideoLibraryFavoritesNavItem.Content",
            "VideoLibrarySeriesNavItem.Content",
            "VideoLibraryFoldersNavItem.Content",
            "VideoLibraryCollectionsNavItem.Content",
            "VideoLibraryTagsNavItem.Content",
            "VideoLibraryLayoutList",
            "VideoLibraryLayoutPosters",
            "VideoLibrarySearchBox.PlaceholderText",
            "VideoLibrarySortComboBox.AutomationProperties.Name",
            "ScanVideoFolderButton.Label",
            "ImportVideoButton.Label",
            "CreateSmartCollectionButton.Label",
            "VideoLibrarySmartCollectionName.PlaceholderText",
            "VideoLibrarySmartCollectionRuleValue.PlaceholderText",
            "VideoLibraryCreateSmartCollectionPrimaryButton",
            "VideoLibraryCreateSmartCollectionSecondaryButton",
            "VideoLibraryPreviewMatches",
            "VideoLibraryMarkWatchedMenuItem.Text",
            "VideoLibraryClearProgressMenuItem.Text",
            "VideoLibraryDeleteMenuItem.Text",
            "VideoLibrarySortRecent",
            "VideoLibrarySortTitle",
            "VideoLibrarySortProgress",
            "VideoLibrarySortFolder",
            "VideoLibraryViewAll",
            "VideoLibraryViewContinueWatching",
            "VideoLibraryViewWatched",
            "VideoLibraryViewFolders",
            "VideoLibraryViewCollections",
            "VideoLibraryViewTags",
            "VideoLibraryCountFormat",
            "VideoLibraryImportedMessage",
            "VideoLibraryFolderScannedMessage",
            "VideoLibraryDeleteTitle",
            "VideoLibraryDeleteMessageFormat",
            "VideoWatchStatusContinue",
        })
        {
            enResources.Should().Contain($"name=\"{key}\"");
            zhResources.Should().Contain($"name=\"{key}\"");
        }

        zhResources.Should().Contain("<value>继续观看</value>");
        zhResources.Should().Contain("<value>扫描文件夹</value>");
    }
}
