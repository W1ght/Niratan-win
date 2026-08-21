using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.ViewModels.Components;

public sealed class VideoMetadataTaskViewModel : ObservableObject
{
    public VideoMetadataTaskViewModel(VideoMetadataTaskSnapshot snapshot, string? sourceName)
    {
        Snapshot = snapshot;
        SourceName = sourceName ?? "";
    }

    public VideoMetadataTaskSnapshot Snapshot { get; private set; }
    public Guid JobId => Snapshot.JobId;
    public string SourceName { get; }

    public int SuccessfulCount => Math.Max(
        0,
        Snapshot.ProcessedCount - Snapshot.NeedsReviewCount - Snapshot.FailedCount);

    public int PendingCount => Snapshot.NeedsReviewCount;

    public string TaskTitle => string.IsNullOrWhiteSpace(SourceName)
        ? ResourceStringHelper.GetString("VideoMetadataTaskAllSources", "All video sources")
        : SourceName;

    public string StatusText => Snapshot.State switch
    {
        VideoCatalogJobState.Queued => ResourceStringHelper.GetString(
            "VideoMetadataTaskQueued", "Queued"),
        VideoCatalogJobState.Running => ResourceStringHelper.GetString(
            "VideoMetadataTaskRunning", "Running in background"),
        VideoCatalogJobState.Paused => ResourceStringHelper.GetString(
            "VideoMetadataTaskPaused", "Paused"),
        VideoCatalogJobState.Completed => ResourceStringHelper.GetString(
            "VideoMetadataTaskCompleted", "Completed"),
        VideoCatalogJobState.Cancelled => ResourceStringHelper.GetString(
            "VideoMetadataTaskCancelled", "Cancelled"),
        VideoCatalogJobState.Interrupted => ResourceStringHelper.GetString(
            "VideoMetadataTaskInterrupted", "Interrupted; retry available"),
        VideoCatalogJobState.Failed => ResourceStringHelper.GetString(
            "VideoMetadataTaskFailed", "Failed; retry available"),
        _ => Snapshot.State.ToString(),
    };

    public string ProgressText => ResourceStringHelper.FormatString(
        "VideoMetadataTaskProgressFormat",
        "{0} / {1} · {2} success · {3} pending · {4} failed",
        Snapshot.ProcessedCount,
        Snapshot.TotalCount,
        SuccessfulCount,
        PendingCount,
        Snapshot.FailedCount);

    public string UpdatedText => ResourceStringHelper.FormatString(
        "VideoMetadataTaskUpdatedFormat",
        "Updated {0:g}",
        Snapshot.UpdatedAt.ToLocalTime());

    public string ErrorText => Snapshot.Error ?? "";
    public bool HasError => !string.IsNullOrWhiteSpace(Snapshot.Error);
    public bool HasReviewItems => Snapshot.NeedsReviewCount > 0;
    public bool IsActive => Snapshot.State is VideoCatalogJobState.Queued
        or VideoCatalogJobState.Running
        or VideoCatalogJobState.Paused;
    public bool CanCancel => IsActive;
    public bool CanRetry => Snapshot.State is VideoCatalogJobState.Failed
        or VideoCatalogJobState.Cancelled
        or VideoCatalogJobState.Interrupted;

    public void Update(VideoMetadataBatchProgress progress)
    {
        Snapshot = Snapshot with
        {
            State = progress.State,
            ProcessedCount = progress.ProcessedCount,
            TotalCount = progress.TotalCount,
            MatchedCount = progress.MatchedCount,
            NeedsReviewCount = progress.NeedsReviewCount,
            FailedCount = progress.FailedCount,
            Error = progress.Error,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SuccessfulCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasReviewItems));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }
}
