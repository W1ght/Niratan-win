using FluentAssertions;
using System.Xml.Linq;

namespace Niratan.Tests.Views.Pages;

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
            "Niratan"));

    [Fact]
    public void VideoLibraryPage_DefinesNiratanStyleMinimalLibraryControls()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));

        xaml.Should().Contain("x:Name=\"VideoLibrarySecondaryNavigationView\"");
        xaml.Should().Contain("Padding=\"20,14,28,16\"");
        xaml.Should().NotContain("VideoLibraryTitleBarBackground");
        xaml.Should().NotContain("Margin=\"0,-32,0,0\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryAllNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryContinueWatchingNavItem\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"VideoLibraryWatchedNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySearchBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySortComboBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ScanVideoFolderButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryLayoutSegment\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryListView\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoGridView\"");
        xaml.Should().Contain("x:Key=\"VideoListItemTemplate\"");
        xaml.Should().Contain("x:Key=\"VideoPosterItemTemplate\"");
        xaml.Should().Contain("x:Name=\"VideoPosterTitleText\"");
        xaml.Should().Contain("MaxLines=\"2\"");
        xaml.Should().Contain("ItemHeight=\"260\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"CreateSmartCollectionButton\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.CreateSmartCollectionCommand}\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.SmartRuleDrafts");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.AddSmartRuleCommand}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"RefreshVideoSourcesButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ManageVideoSourcesButton\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.MarkSelectedWatchedCommand}\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.SaveVideoDetailsCommand}\"");
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
        xaml.Should().Contain("Command=\"{Binding ViewModel.OpenVideoCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.OpenVideoFromBeginningCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.ToggleFavoriteCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.MarkWatchedCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.ClearProgressCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.RevealFileCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.AddToNewCollectionCommand, ElementName=ThisPage}\"");
    }

    [Fact]
    public void VideoLibraryPage_UsesCompactFixedWidthFilterCards()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        AssertFilterTemplateWidth(document, x, "FolderFilterTemplate", "220");
        AssertFilterTemplateWidth(document, x, "CollectionFilterTemplate", "220");
        AssertFilterTemplateWidth(document, x, "TagFilterTemplate", "180");

        AssertFilterPanelWidth(document, x, "VideoLibraryFolderFilters", "228");
        AssertFilterPanelWidth(document, x, "VideoLibraryCollectionFilters", "228");
        AssertFilterPanelWidth(document, x, "VideoLibraryTagFilters", "188");
    }

    [Fact]
    public void VideoLibraryPage_UsesCompactResponsiveHeader()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var searchBox = document.Descendants(presentation + "TextBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "VideoLibrarySearchBox");
        searchBox.Attribute("Grid.Row")?.Value.Should().Be("1");

        var commandBar = searchBox.Parent!.Elements(presentation + "CommandBar").Single();
        commandBar.Attribute("Grid.Row")?.Value.Should().Be("1");
        commandBar.Attribute("DefaultLabelPosition")?.Value.Should().Be("Collapsed");
        searchBox.Parent!.Attribute("RowDefinitions")?.Value.Should().Be("Auto,Auto");
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
            "VideoLibraryPlayMenuItem",
            "VideoLibraryPlayFromBeginningMenuItem",
            "VideoLibraryAddFavoriteMenuItem",
            "VideoLibraryRemoveFavoriteMenuItem",
            "VideoLibraryMarkWatchedMenuItem",
            "VideoLibraryClearProgressMenuItem",
            "VideoLibraryRevealFileMenuItem",
            "VideoLibraryAddToNewCollectionMenuItem",
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
            "VideoLibrarySmartCollectionRuleField.Header",
            "VideoLibrarySmartRuleFieldFileName",
            "VideoLibrarySmartRuleFieldParentFolder",
            "VideoLibrarySmartRuleFieldPath",
            "VideoLibrarySmartRuleFieldTag",
            "VideoLibrarySmartRuleFieldHasBoundSubtitle",
            "VideoLibrarySmartRuleFieldPlaybackState",
            "VideoLibrarySmartCollectionRuleValue.PlaceholderText",
            "VideoLibraryCreateSmartCollectionPrimaryButton",
            "VideoLibraryCreateSmartCollectionSecondaryButton",
            "VideoLibraryPreviewMatches",
            "VideoLibraryPlayMenuItem.Text",
            "VideoLibraryPlayFromBeginningMenuItem.Text",
            "VideoLibraryAddFavoriteMenuItem.Text",
            "VideoLibraryRemoveFavoriteMenuItem.Text",
            "VideoLibraryMarkWatchedMenuItem.Text",
            "VideoLibraryClearProgressMenuItem.Text",
            "VideoLibraryRevealFileMenuItem.Text",
            "VideoLibraryAddToNewCollectionMenuItem.Text",
            "VideoLibraryDeleteMenuItem.Text",
            "VideoLibraryManualCollectionPromptTitle",
            "VideoLibraryManualCollectionPromptPlaceholder",
            "VideoLibraryManualCollectionPromptPrimary",
            "VideoLibraryManualCollectionCreatedMessage",
            "VideoLibraryFavoriteAddedMessage",
            "VideoLibraryFavoriteRemovedMessage",
            "VideoLibraryRevealFileMissingMessage",
            "VideoLibrarySortRecent",
            "VideoLibrarySortTitle",
            "VideoLibrarySortProgress",
            "VideoLibrarySortFolder",
            "VideoLibraryViewAll",
            "VideoLibraryViewContinueWatching",
            "VideoLibraryViewFinished",
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

    [Fact]
    public void VideoPlayerWindowService_KeepsPlaybackStateSavedSubscribedThroughClosed()
    {
        var source = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "Video", "VideoPlayerWindowService.cs"));

        source.Should().NotContain("PlaybackStateSaved -= OnWindowPlaybackStateSaved");
    }

    private static void AssertFilterTemplateWidth(
        XDocument document,
        XNamespace x,
        string templateKey,
        string expectedWidth)
    {
        var template = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && (string?)element.Attribute(x + "Key") == templateKey);
        var button = template.Elements().Single(element => element.Name.LocalName == "Button");

        button.Attribute("Width")?.Value.Should().Be(expectedWidth);
        button.Attribute("MinWidth").Should().BeNull();
    }

    private static void AssertFilterPanelWidth(
        XDocument document,
        XNamespace x,
        string itemsControlName,
        string expectedWidth)
    {
        var itemsControl = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "ItemsControl"
                && (string?)element.Attribute(x + "Name") == itemsControlName);
        var itemsWrapGrid = itemsControl.Descendants()
            .Single(element => element.Name.LocalName == "ItemsWrapGrid");

        itemsWrapGrid.Attribute("ItemWidth")?.Value.Should().Be(expectedWidth);
    }
}
