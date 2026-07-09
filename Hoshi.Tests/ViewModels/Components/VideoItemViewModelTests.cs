using FluentAssertions;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.ViewModels.Components;

namespace Hoshi.Tests.ViewModels.Components;

public sealed class VideoItemViewModelTests
{
    [Fact]
    public void NearStartProgress_IsDisplayedAsUnwatched()
    {
        var sut = new VideoItemViewModel(new VideoItem
        {
            Id = "video-1",
            Title = "Episode 1",
            FilePath = @"D:\Videos\episode1.mkv",
            LastPositionSeconds = 2,
            DurationSeconds = 2406,
        });

        sut.OverallProgress.Should().Be(0);
        sut.ProgressText.Should().Be("");
        sut.WatchStatusText.Should().Be(
            ResourceStringHelper.GetString("VideoWatchStatusUnwatched", "Unwatched"));
    }

    [Fact]
    public void MeaningfulProgress_IsDisplayedAsContinueWatching()
    {
        var sut = new VideoItemViewModel(new VideoItem
        {
            Id = "video-1",
            Title = "Episode 1",
            FilePath = @"D:\Videos\episode1.mkv",
            LastPositionSeconds = 60,
            DurationSeconds = 240,
        });

        sut.OverallProgress.Should().Be(0.25);
        sut.ProgressText.Should().Be("25%");
        sut.WatchStatusText.Should().Be(
            ResourceStringHelper.GetString("VideoWatchStatusContinue", "Continue"));
    }

    [Fact]
    public void ArtworkPath_PrefersPosterThenThumbnail()
    {
        var posterPath = Path.GetTempFileName();
        var thumbnailPath = Path.GetTempFileName();
        try
        {
            new VideoItemViewModel(new VideoItem
            {
                PosterPath = posterPath,
                ThumbnailPath = thumbnailPath,
            }).ArtworkPath.Should().Be(posterPath);

            new VideoItemViewModel(new VideoItem
            {
                ThumbnailPath = thumbnailPath,
            }).ArtworkPath.Should().Be(thumbnailPath);
        }
        finally
        {
            File.Delete(posterPath);
            File.Delete(thumbnailPath);
        }
    }

    [Fact]
    public void ArtworkPath_FallsBackToExistingThumbnailWhenPosterIsMissing()
    {
        var thumbnailPath = Path.GetTempFileName();
        try
        {
            var sut = new VideoItemViewModel(new VideoItem
            {
                PosterPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg"),
                ThumbnailPath = thumbnailPath,
            });

            sut.ArtworkPath.Should().Be(thumbnailPath);
        }
        finally
        {
            File.Delete(thumbnailPath);
        }
    }
}
