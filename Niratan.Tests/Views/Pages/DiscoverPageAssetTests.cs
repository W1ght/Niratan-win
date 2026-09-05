using System.Collections.Immutable;
using FluentAssertions;
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
    public void Discovery_cards_use_the_host_frame_for_a_dedicated_detail_route()
    {
        var xaml = ReadPage("DiscoverPage.xaml");
        var code = ReadPage("DiscoverPage.xaml.cs");

        xaml.Should().Contain("DiscoverExploreResults");
        xaml.Should().Contain("DiscoverRecommendationSections");
        xaml.Should().Contain("Click=\"DiscoveryCard_Click\"");
        xaml.Should().Contain("DiscoverOpenDownloadsButton");
        xaml.Should().Contain("DiscoverOpenSubscriptionsButton");
        xaml.Should().NotContain("IsDetailsVisible");
        xaml.Should().NotContain("DiscoverResourceResults");
        code.Should().Contain("Frame.Navigate(typeof(VideoDiscoveryDetailPage), target)");
    }

    [Fact]
    public void Discovery_search_and_explore_use_fixed_all_source_all_kind_aggregation()
    {
        var xaml = ReadPage("DiscoverPage.xaml");
        var viewModel = ReadViewModel("DiscoverPageViewModel.cs");

        xaml.Should().Contain("ColumnDefinitions=\"*,Auto\"");
        xaml.Should().NotContain("DiscoverVideoSearchKindBox");
        xaml.Should().NotContain("ViewModel.SearchCategories");
        xaml.Should().NotContain("ViewModel.SelectedSearchCategory");
        xaml.Should().NotContain("DiscoverProviderBox");
        xaml.Should().NotContain("ViewModel.Providers");
        xaml.Should().NotContain("ViewModel.SelectedProvider");
        xaml.Should().NotContain("DiscoverFeedBox");
        xaml.Should().NotContain("ViewModel.ExploreFeeds");
        xaml.Should().NotContain("ViewModel.SelectedExploreFeed");
        xaml.Should().Contain("DiscoverProviderWarning");
        viewModel.Should().Contain("SearchAggregatedAsync");
        viewModel.Should().Contain("GetAggregatedPageAsync");
        viewModel.Should().Contain("VideoDiscoverySearchCategory.All");
        viewModel.Should().Contain("AggregatedSearchProviderOrder = [\"anilist\", \"tmdb\"]");
    }

    [Fact]
    public void Detail_route_keeps_hero_overview_people_related_and_all_acquisition_routes()
    {
        var xaml = ReadPage("VideoDiscoveryDetailPage.xaml");
        var code = ReadPage("VideoDiscoveryDetailPage.xaml.cs");

        xaml.Should().Contain("Height=\"480\"");
        xaml.Should().Contain("Stretch=\"UniformToFill\"");
        xaml.Should().Contain("DiscoverOverviewHeading");
        xaml.Should().Contain("DiscoverPeopleHeading");
        xaml.Should().Contain("DiscoverRelatedHeading");
        xaml.Should().Contain("VideoDiscoveryDetailSearchResourcesButton");
        xaml.Should().Contain("VideoDiscoveryDetailSearchSubtitlesButton");
        xaml.Should().Contain("VideoDiscoveryDetailSubscribeButton");
        code.Should().Contain("VideoDiscoveryResourceRouteMode.Download");
        code.Should().Contain("VideoDiscoveryResourceRouteMode.Subscription");
        code.Should().Contain("typeof(VideoDiscoverySubtitleSearchPage)");
        code.Should().Contain("OpenSubscriptionsCommand");
        code.Should().Contain("Frame.Navigate(typeof(VideoDiscoveryDetailPage), target)");
    }

    [Fact]
    public void Resource_route_has_one_backend_aware_submit_action_and_strict_subscription_fields()
    {
        var xaml = ReadPage("VideoDiscoveryResourceSearchPage.xaml");
        var viewModel = ReadViewModel("VideoDiscoveryResourceSearchPageViewModel.cs");

        xaml.Should().Contain("SelectionMode=\"Single\"");
        xaml.Should().Contain("VideoDiscoveryResourceSubmitButton");
        xaml.Should().Contain("VideoDiscoverySubscriptionReleaseGroupLabel");
        xaml.Should().Contain("VideoDiscoverySubscriptionResolutionLabel");
        xaml.Should().Contain("VideoDiscoverySubscriptionStartFromLabel");
        xaml.Should().NotContain("DownloadAndImportResourceCommand");
        xaml.Should().NotContain("AddResourceToQbCommand");
        viewModel.Should().Contain("_settings.Current.DownloadBackend");
        viewModel.Should().Contain("NyaaSubscriptionArtwork");
        viewModel.Should().Contain("Target.Work.Identity.PosterUrl");
        viewModel.Should().Contain("Target.Work.Artwork.PosterPath");
    }

    [Fact]
    public void Subtitle_route_offers_all_destinations_and_generates_a_unique_target()
    {
        var xaml = ReadPage("VideoDiscoverySubtitleSearchPage.xaml");
        var viewModel = ReadViewModel("VideoDiscoverySubtitleSearchPageViewModel.cs");

        xaml.Should().Contain("VideoDiscoverySubtitleDestinationBox");
        xaml.Should().Contain("VideoDiscoverySubtitlePickTargetButton");
        xaml.Should().Contain("VideoDiscoverySubtitleSaveButton");
        viewModel.Should().Contain("VideoDiscoverySubtitleDestination.SaveAs");
        viewModel.Should().Contain("VideoDiscoverySubtitleDestination.ExistingVideo");
        viewModel.Should().Contain("VideoDiscoverySubtitleDestination.Directory");
        viewModel.Should().Contain("FindUniqueDestination");
        viewModel.Should().Contain("File.Exists");
        viewModel.Should().Contain("_subtitles.DownloadAsync");
    }

    [Fact]
    public void Detail_projection_is_created_on_the_page_context_for_WinUI_images()
    {
        var viewModel = ReadViewModel("VideoDiscoveryDetailPageViewModel.cs");
        var start = viewModel.IndexOf("await _discovery.GetDetailsAsync", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = viewModel.IndexOf("Details = new VideoDiscoveryDetailsViewModel", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        viewModel[start..end].Should().NotContain("Task.Run");
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
    public void Video_library_hosts_discover_without_a_second_global_video_module()
    {
        var navigation = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml"));
        var videoLibrary = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));
        var videoLibraryCodeBehind = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "VideoLibraryPage.xaml.cs"));

        navigation.Should().NotContain("DiscoverNavItem");
        navigation.Should().NotContain("Niratan.Views.Pages.DiscoverPage");
        videoLibrary.Should().Contain("VideoLibraryDiscoverPage");
        videoLibraryCodeBehind.Should().Contain("typeof(DiscoverPage)");
        videoLibrary.Should().Contain("Tag=\"Discover\"");
    }

    [Fact]
    public void Recommendation_wheel_forwarding_keeps_scrollviewer_animation_enabled()
    {
        var code = ReadPage("DiscoverPage.xaml.cs");

        code.Should().Contain("verticalScrollViewer.ChangeView(");
        code.Should().Contain("horizontalScrollViewer.ChangeView(");
        code.Should().Contain("disableAnimation: false");
    }

    private static string ReadPage(string fileName) =>
        File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", fileName));

    private static string ReadViewModel(string fileName) =>
        File.ReadAllText(Path.Combine(ProjectRoot, "ViewModels", "Pages", fileName));
}
