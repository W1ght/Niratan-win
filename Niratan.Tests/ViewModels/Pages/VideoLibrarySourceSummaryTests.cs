using FluentAssertions;
using Niratan.Models.Video;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class VideoLibrarySourceSummaryTests
{
    [Fact]
    public void UpdateProgress_FormatsKnownAndIndeterminateStages()
    {
        var summary = new VideoLibrarySourceSummary(new VideoLibrarySource(), 0, 0, 0);

        summary.UpdateProgress(new VideoLibraryScanProgress(
            Guid.NewGuid(), 1, VideoCatalogJobState.Running,
            VideoLibraryScanStage.Enumerating, 23, null, 0, 4.25,
            @"C:\Media\作品.mkv", null));

        summary.IsScanProgressVisible.Should().BeTrue();
        summary.IsScanIndeterminate.Should().BeTrue();
        summary.CurrentItemText.Should().Be("作品.mkv");
        summary.ScanProgressText.Should().Contain("23").And.Contain("4.3");

        summary.UpdateProgress(new VideoLibraryScanProgress(
            Guid.NewGuid(), 1, VideoCatalogJobState.Running,
            VideoLibraryScanStage.Analyzing, 25, 100, 10, 5,
            null, null));
        summary.IsScanIndeterminate.Should().BeFalse();
        summary.ScanProgressValue.Should().Be(25);
    }

    [Fact]
    public void ProviderOrderDraft_NormalizesKnownProvidersAndRejectsUnknownValues()
    {
        var source = new VideoLibrarySource();
        var summary = new VideoLibrarySourceSummary(source, 0, 0, 0)
        {
            MediaTypeDraft = VideoLibraryMediaType.Movie,
            ProviderOrderDraft = "Local → TMDB, anidb, tmdb",
        };

        summary.MediaTypeSelectedIndex.Should().Be((int)VideoLibraryMediaType.Movie);
        summary.MediaTypeSelectedIndex = (int)VideoLibraryMediaType.Anime;
        summary.MediaTypeDraft.Should().Be(VideoLibraryMediaType.Anime);
        summary.MediaTypeDraft = VideoLibraryMediaType.Movie;

        summary.TryApplyProviderOrder(out var invalid).Should().BeTrue();
        invalid.Should().BeNull();
        source.MediaType.Should().Be(VideoLibraryMediaType.Movie);
        source.ProviderOrder.Should().Equal("local", "tmdb", "anidb");

        summary.ProviderOrderDraft = "local, bangumi";
        summary.TryApplyProviderOrder(out invalid).Should().BeFalse();
        invalid.Should().Be("bangumi");
        source.ProviderOrder.Should().Equal("local", "tmdb", "anidb");
    }

    [Fact]
    public void UpdateMetadataProgress_ShowsBackgroundCountsAndFailure()
    {
        var summary = new VideoLibrarySourceSummary(new VideoLibrarySource(), 0, 0, 0);
        summary.UpdateMetadataProgress(new VideoMetadataBatchProgress(
            Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Running,
            3, 10, 2, 1, Guid.NewGuid()));

        summary.IsMetadataProgressVisible.Should().BeTrue();
        summary.MetadataProgressValue.Should().Be(30);
        summary.MetadataProgressText.Should().Contain("3").And.Contain("10");
        summary.HasMetadataError.Should().BeFalse();

        summary.UpdateMetadataProgress(new VideoMetadataBatchProgress(
            Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Failed,
            4, 10, 2, 2, null, "provider unavailable", 1));
        summary.IsMetadataProgressVisible.Should().BeTrue();
        summary.HasMetadataError.Should().BeTrue();
        summary.MetadataErrorText.Should().Be("provider unavailable");
        summary.ScrapeSummaryText.Should().Contain("1");
    }

    [Fact]
    public void UpdateMetadataProgress_ExposesLastScrapeSummaryAndSettingsStartCollapsed()
    {
        var summary = new VideoLibrarySourceSummary(new VideoLibrarySource(), 0, 0, 0);

        summary.IsSourceSettingsExpanded.Should().BeFalse();
        summary.ScrapeSummaryText.Should().NotBeNullOrWhiteSpace();

        summary.UpdateMetadataProgress(new VideoMetadataBatchProgress(
            Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Completed,
            1, 1, 1, 0, null));

        summary.IsMetadataProgressVisible.Should().BeFalse();
        summary.ScrapeSummaryText.Should().Contain("1");
    }
}
