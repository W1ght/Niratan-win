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
        xaml.Should().Contain("PaneDisplayMode=\"Top\"");
        xaml.Should().Contain("Padding=\"20,14,28,16\"");
        xaml.Should().NotContain("VideoLibraryTitleBarBackground");
        xaml.Should().NotContain("Margin=\"0,-32,0,0\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryHomeNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryDiscoverNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySeriesNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryAllVideosNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySourcesNavItem\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySearchBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySortComboBox\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"ScanVideoFolderButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"AddYouTubeLinkButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"RefreshVideoSourcesButton\"");
        xaml.Should().Contain("ViewModel.IsLibraryHeaderVisible");
        xaml.Should().Contain("ViewModel.IsSourcesView");
        xaml.Should().Contain("ViewModel.IsMetadataTaskPanelVisible");
        xaml.Should().Contain("DefaultLabelPosition=\"Right\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"VideoLibraryLayoutSegment\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"VideoLibraryListView\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoGridView\"");
        xaml.Should().Contain("x:Key=\"VideoPosterItemTemplate\"");
        xaml.Should().Contain("x:Name=\"VideoPosterTitleText\"");
        xaml.Should().Contain("MaxLines=\"2\"");
        xaml.Should().Contain("ItemHeight=\"320\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryDiscoverPage\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.CreateSmartCollectionCommand}\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.SmartRuleDrafts");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.AddSmartRuleCommand}\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"ManageVideoSourcesButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryScrapeAllButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryMetadataTasksButton\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"ImportVideoButton\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"ImportNyaaResourcesButton\"");
        xaml.Should().NotContain("AutomationProperties.AutomationId=\"CreateSmartCollectionButton\"");
        xaml.Should().Contain("x:Key=\"VideoMetadataTaskTemplate\"");
        xaml.Should().Contain("ViewModel.CancelMetadataTaskCommand");
        xaml.Should().Contain("ViewModel.RetryMetadataTaskCommand");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.MarkSelectedWatchedCommand}\"");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.SaveVideoDetailsCommand}\"");
        xaml.Should().NotContain("VideoLibraryNeedsReviewNavItem");
        xaml.Should().NotContain("VideoLibraryFavoritesNavItem");
        xaml.Should().NotContain("VideoLibraryUnorganizedNavItem");
        xaml.Should().Contain("Command=\"{x:Bind ViewModel.RefreshSelectedMetadataCommand}\"");
        xaml.Should().Contain("ViewModel.ActiveScanText, Mode=OneWay");
        xaml.Should().Contain("ViewModel.MetadataRefreshText, Mode=OneWay");
        xaml.Should().Contain("IsIndeterminate=\"{x:Bind IsScanIndeterminate, Mode=OneWay}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryHomeSections\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.HomeNextEpisodes, Mode=OneWay}\"");
        xaml.Should().Contain("x:Uid=\"VideoLibrarySourceProviderOrderBox\"");
        xaml.Should().Contain("SelectedIndex=\"{x:Bind MediaTypeSelectedIndex, Mode=TwoWay}\"");
        xaml.Should().NotContain("SelectedValue=\"{x:Bind Source.MediaType, Mode=TwoWay}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.BindMatchCandidateCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryFolderFilters\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryCollectionFilters\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryTagFilters\"");
        xaml.Should().Contain("Source=\"{x:Bind ArtworkImage");
        xaml.Should().Contain("Command=\"{Binding ViewModel.OpenVideoCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.OpenVideoFromBeginningCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.SelectSeriesSeasonCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"{x:Bind AutomationId, Mode=OneTime}\"");
        xaml.Should().Contain("IsChecked=\"{x:Bind IsSelected, Mode=OneWay}\"");
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
    public void VideoLibraryPage_HomeVideoCardsKeepOneFixedLandscapeWidth()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var template = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && (string?)element.Attribute(x + "Key") == "VideoPosterItemTemplate");
        var card = template.Elements().Single(element => element.Name.LocalName == "Grid");
        var button = card.Elements().Single(element => element.Name.LocalName == "Button");
        var artwork = button.Descendants()
            .Single(element =>
                element.Name.LocalName == "Border"
                && (string?)element.Attribute("Height") == "169");
        var title = button.Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBlock"
                && (string?)element.Attribute(x + "Name") == "VideoPosterTitleText");

        card.Attribute("Width")?.Value.Should().Be("300");
        card.Attribute("MaxWidth")?.Value.Should().Be("300");
        card.Attribute("Margin")?.Value.Should().Be("0,0,12,0");
        button.Attribute("Width")?.Value.Should().Be("300");
        button.Attribute("MaxWidth")?.Value.Should().Be("300");
        artwork.Attribute("Width")?.Value.Should().Be("300");
        artwork.Attribute("Height")?.Value.Should().Be("169");
        title.Attribute("Width")?.Value.Should().Be("300");
        title.Attribute("MaxWidth")?.Value.Should().Be("300");
        title.Attribute("MaxLines")?.Value.Should().Be("2");

        var homeItems = document.Descendants()
            .Where(element => element.Name.LocalName == "ItemsControl")
            .Where(element => ((string?)element.Attribute("ItemsSource"))?.Contains("ViewModel.Home", StringComparison.Ordinal) == true)
            .ToList();

        homeItems.Should().HaveCount(3);
        homeItems.Should().OnlyContain(element =>
            (string?)element.Attribute("ItemTemplate") == "{StaticResource VideoPosterItemTemplate}");
    }

    [Fact]
    public void VideoLibraryPage_DoesNotReserveHiddenDetailsColumn()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var document = XDocument.Parse(xaml);

        var browseGrid = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Grid"
                && (string?)element.Attribute("Margin") == "20,0,28,0");
        var details = browseGrid.Elements()
            .Single(element =>
                element.Name.LocalName == "Border"
                && (string?)element.Attribute("Grid.Column") == "1");

        browseGrid.Attribute("ColumnDefinitions")?.Value.Should().Be("*,Auto");
        details.Attribute("Width")?.Value.Should().Be("340");
        details.Attribute("Visibility")?.Value.Should().Contain("HasSelectedVideo");
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
        commandBar.Attribute("DefaultLabelPosition")?.Value.Should().Be("Right");
        commandBar.Attribute("Visibility")?.Value.Should().Contain("IsSourcesView");
        searchBox.Parent!.Attribute("Visibility")?.Value.Should().Contain("IsLibraryHeaderVisible");
        searchBox.Parent!.Attribute("RowDefinitions")?.Value.Should().Be("Auto,Auto");
    }

    [Fact]
    public void VideoLibraryPage_UsesFullWidthSourcesPageAndVisibleBackgroundProgress()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibrarySourcesPage\"");
        xaml.Should().Contain("ViewModel.BackgroundMetadataProgress");
        xaml.Should().Contain("ViewModel.RefreshSourceMetadataCommand");
        xaml.Should().Contain("ViewModel.CancelSourceMetadataCommand");
        xaml.Should().NotContain("ManageVideoSourcesDialog");
        codeBehind.Should().NotContain("ManageVideoSourcesDialog");
    }

    [Fact]
    public void VideoLibraryPage_UsesCompactSourceScrapeActionsWithCollapsedSettings()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));

        xaml.Should().Contain("Text=\"{x:Bind ScrapeSummaryText, Mode=OneWay}\"");
        xaml.Should().Contain("x:Uid=\"VideoLibrarySourceSettingsButton\"");
        xaml.Should().Contain("Command=\"{Binding ViewModel.ToggleSourceSettingsCommand, ElementName=ThisPage}\"");
        xaml.Should().Contain("IsSourceSettingsExpanded");
        xaml.Should().Contain("OverflowButtonVisibility=\"Collapsed\"");
    }

    [Fact]
    public void VideoLibraryPage_HomeMatchesMediaHubSectionsWithoutDuplicatingBrowseList()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryHomePage\"");
        xaml.Should().Contain("x:Uid=\"VideoLibraryHomeMyMediaHeading\"");
        xaml.Should().Contain("ViewModel.HomeContinueWatching");
        xaml.Should().Contain("ViewModel.HomeNextEpisodes");
        xaml.Should().Contain("VideoLibraryEpisodeSlots");
        xaml.Should().Contain("DownloadEpisodeCommand");
        xaml.Should().Contain("IsLoadingSeriesEpisodes");
        xaml.Should().Contain("ViewModel.HomeRecentlyAdded");
        xaml.Should().Contain("Visibility=\"{x:Bind ViewModel.IsLibraryBrowseView");
        xaml.Should().Contain("HorizontalScrollMode=\"Enabled\"");

        var document = XDocument.Parse(xaml);
        var importCommandBar = document.Descendants()
            .Where(element => element.Name.LocalName == "CommandBar")
            .Single(element => element.Descendants().Any(button =>
                (string?)button.Attribute("AutomationProperties.AutomationId") == "RefreshVideoSourcesButton"));
        var importButtonIds = importCommandBar.Descendants()
            .Where(element => element.Name.LocalName == "AppBarButton")
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(id => id != null)
            .ToArray();
        importButtonIds.Should().Equal(
            "ScanVideoFolderButton",
            "AddYouTubeLinkButton",
            "RefreshVideoSourcesButton",
            "VideoLibraryScrapeAllButton",
            "VideoLibraryMetadataTasksButton");
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
            "VideoLibraryTopHomeNavItem",
            "VideoLibraryTopDiscoverNavItem",
            "VideoLibraryTopSeriesNavItem",
            "VideoLibraryTopAllVideosNavItem",
            "VideoLibraryTopImportNavItem",
            "VideoLibrarySearchBox",
            "VideoLibrarySortComboBox",
            "ScanVideoFolderButton",
            "AddYouTubeLinkButton",
            "RefreshVideoSourcesButton",
            "VideoLibraryScrapeAllButton",
            "VideoLibraryMetadataTasksButton",
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
            "VideoLibraryTopHomeNavItem.Content",
            "VideoLibraryTopDiscoverNavItem.Content",
            "VideoLibraryTopSeriesNavItem.Content",
            "VideoLibraryTopAllVideosNavItem.Content",
            "VideoLibraryTopImportNavItem.Content",
            "VideoLibrarySearchBox.PlaceholderText",
            "VideoLibrarySortComboBox.AutomationProperties.Name",
            "ScanVideoFolderButton.Label",
            "AddYouTubeLinkButton.Label",
            "RefreshVideoSourcesButton.Label",
            "VideoLibraryScrapeAllButton.Label",
            "VideoLibraryMetadataTasksButton.Label",
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
            "VideoLibraryViewDiscover",
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
