using FluentAssertions;
using Moq;
using System.Collections.Immutable;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class DiscoverPageViewModelTests
{
    [Fact]
    public async Task Downloading_a_resource_enqueues_the_builtin_Nyaa_import_task()
    {
        var downloadManager = new Mock<INyaaDownloadManager>();
        var item = CreateItem();
        var row = new NyaaTorrentItemViewModel(item);
        var identity = new VideoMetadataCandidate(
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
        var settings = new AppSettings();
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(settings);

        using var viewModel = new DiscoverPageViewModel(
            Mock.Of<IVideoDiscoveryService>(),
            Mock.Of<IVideoResourceSearchService>(),
            new Lazy<INyaaDownloadManager>(() => downloadManager.Object),
            Mock.Of<IQbittorrentCredentialStore>(),
            Mock.Of<IQbittorrentDownloadCoordinator>(),
            settingsService.Object,
            Mock.Of<INavigationService>())
        {
            SelectedDetails = new VideoDiscoveryDetailsViewModel(identity),
        };

        await viewModel.DownloadAndImportResourceCommand.ExecuteAsync(row);

        downloadManager.Verify(manager => manager.Enqueue(item), Times.Once);
        row.IsImported.Should().BeTrue();
        row.Status.Should().NotBeNullOrWhiteSpace();
    }

    private static NyaaTorrentItem CreateItem() =>
        new(
            "test-resource",
            "[Test] Test title 2026",
            new Uri("https://nyaa.si/download/test-resource.torrent"),
            new Uri("https://nyaa.si/view/test-resource"),
            "Live action",
            1024,
            12,
            1,
            0,
            DateTimeOffset.UtcNow,
            true,
            false);
}
