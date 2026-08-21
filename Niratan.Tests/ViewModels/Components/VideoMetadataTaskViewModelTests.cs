using FluentAssertions;
using Niratan.Models.Video;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class VideoMetadataTaskViewModelTests
{
    [Theory]
    [InlineData(VideoCatalogJobState.Queued, true, false)]
    [InlineData(VideoCatalogJobState.Running, true, false)]
    [InlineData(VideoCatalogJobState.Completed, false, false)]
    [InlineData(VideoCatalogJobState.Cancelled, false, true)]
    [InlineData(VideoCatalogJobState.Interrupted, false, true)]
    [InlineData(VideoCatalogJobState.Failed, false, true)]
    public void StateControlsMatchTaskLifecycle(
        VideoCatalogJobState state,
        bool canCancel,
        bool canRetry)
    {
        var task = new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), state, 2, 5, 1, 1, null,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow),
            "Anime");

        task.TaskTitle.Should().Be("Anime");
        task.CanCancel.Should().Be(canCancel);
        task.CanRetry.Should().Be(canRetry);
        task.ProgressText.Should().Contain("2").And.Contain("5");
        task.HasReviewItems.Should().BeTrue();
    }

    [Fact]
    public void UpdateRefreshesCountsAndTerminalActions()
    {
        var task = new VideoMetadataTaskViewModel(
            new VideoMetadataTaskSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), VideoCatalogJobState.Running,
                1, 4, 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            "Anime");

        task.Update(new VideoMetadataBatchProgress(
            task.JobId, Guid.NewGuid(), VideoCatalogJobState.Interrupted,
            3, 4, 2, 1, null, "The application stopped."));

        task.Snapshot.ProcessedCount.Should().Be(3);
        task.Snapshot.MatchedCount.Should().Be(2);
        task.HasError.Should().BeTrue();
        task.CanCancel.Should().BeFalse();
        task.CanRetry.Should().BeTrue();
    }
}
