using FluentAssertions;
using System.Collections.Immutable;
using System.Xml.Linq;
using Niratan.Models.Video;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.Views.Pages;

public sealed class DiscoverPageAssetTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Niratan", "Niratan.csproj")))
                return Path.Combine(directory.FullName, "Niratan");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Niratan project root.");
    }

    [Fact]
    public void Video_page_exposes_search_recommendation_details_and_resource_actions()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DiscoverPage.xaml"));

        xaml.Should().Contain("DiscoverVideoSearchBox");
        xaml.Should().Contain("DiscoverVideoSearchButton");
        xaml.Should().Contain("VideoContentScrollViewer_ViewChanged");
        xaml.Should().Contain("DiscoverExploreResults");
        xaml.Should().Contain("DiscoverRecommendationSections");
        xaml.Should().Contain("DiscoverSearchNyaaButton");
        xaml.Should().Contain("DiscoverNyaaDownloadButton");
        xaml.Should().Contain("DiscoverAddToQbButton");
        xaml.Should().Contain("DiscoverBackButton");
        xaml.Should().Contain("DiscoverResourceQueryBox");
        xaml.Should().Contain("Stretch=\"Uniform\"");
        xaml.Should().Contain("DiscoverHeroSearchResourcesButton");
        xaml.Should().Contain("DiscoverHeroSearchSubtitlesButton");
        xaml.Should().Contain("DiscoverHeroSubscribeButton");
    }

    [Fact]
    public void Video_library_hosts_discover_without_a_second_global_video_module()
    {
        var navigation = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml"));
        var videoLibrary = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var videoLibraryCodeBehind = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml.cs"));

        navigation.Should().NotContain("DiscoverNavItem");
        navigation.Should().NotContain("Niratan.Views.Pages.DiscoverPage");
        navigation.Should().NotContain("Niratan.Views.Pages.BrowsePage");
        videoLibrary.Should().Contain("VideoLibraryDiscoverPage");
        videoLibraryCodeBehind.Should().Contain("typeof(DiscoverPage)");
        videoLibrary.Should().Contain("Tag=\"Discover\"");
    }

    [Fact]
    public void Details_placeholder_initializes_optional_collections()
    {
        var candidate = new VideoMetadataCandidate(
            "tmdb",
            "123",
            VideoMetadataMediaKind.Movie,
            "Test title",
            null,
            2026,
            null,
            null,
            null,
            ["Test title"],
            ImmutableDictionary<string, string>.Empty,
            null);

        var details = new VideoDiscoveryDetailsViewModel(candidate);

        details.People.Should().BeEmpty();
        details.RelatedItems.Should().BeEmpty();
    }

    [Fact]
    public void Details_projection_is_created_on_the_page_context_for_WinUI_images()
    {
        var viewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "DiscoverPageViewModel.cs"));
        var start = viewModel.IndexOf(
            "var detailsViewModel = new VideoDiscoveryDetailsViewModel",
            StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = viewModel.IndexOf("SelectedDetails = detailsViewModel", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        viewModel[start..end].Should().NotContain("Task.Run");
    }

    [Fact]
    public void Details_hero_uses_the_image_bounds_instead_of_a_fixed_banner()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DiscoverPage.xaml"));

        xaml.Should().NotContain("<Grid Height=\"210\"");
        xaml.Should().Contain("Height=\"480\"");
        xaml.Should().Contain("Stretch=\"UniformToFill\"");
    }

    [Fact]
    public void Video_search_toolbar_stays_outside_the_vertical_content_scrollviewer()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DiscoverPage.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var toolbar = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "VideoSearchToolbar");
        var contentScrollViewer = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "VideoContentScrollViewer");

        toolbar.Descendants()
            .Should()
            .Contain(element => (string?)element.Attribute(x + "Name") == "VideoSearchBox");
        contentScrollViewer.Descendants()
            .Should()
            .NotContain(element => (string?)element.Attribute(x + "Name") == "VideoSearchBox");
        ((string?)toolbar.Attribute("Grid.Row")).Should().Be("0");
        ((string?)contentScrollViewer.Attribute("Grid.Row")).Should().Be("1");
    }

    [Fact]
    public void Recommendation_wheel_forwarding_keeps_scrollviewer_animation_enabled()
    {
        var code = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DiscoverPage.xaml.cs"));

        code.Should().Contain("verticalScrollViewer.ChangeView(");
        code.Should().Contain("horizontalScrollViewer.ChangeView(");
        code.Should().Contain("disableAnimation: false");
        code.Should().NotContain("ChangeView(null, verticalTarget, null, true)");
        code.Should().NotContain("ChangeView(target, null, null, true)");
    }

    [Fact]
    public void Resource_results_use_the_builtin_Nyaa_download_manager_before_qbittorrent()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "DiscoverPage.xaml"));
        var viewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "DiscoverPageViewModel.cs"));

        xaml.Should().Contain("DownloadAndImportResourceCommand");
        xaml.Should().Contain("DiscoverNyaaDownloadButton");
        viewModel.Should().Contain("INyaaDownloadManager");
        viewModel.Should().Contain("downloadManager.Enqueue(row.Item)");
    }
}
