using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Sync;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Niratan.ViewModels.Components;

public enum RemoteNovelDownloadState
{
    Idle,
    Queued,
    Downloading,
    Failed,
}

public partial class RemoteNovelBookItemViewModel : ObservableObject
{
    public RemoteNovelBookItemViewModel(TtuRemoteBook book)
    {
        Book = book;
    }

    public TtuRemoteBook Book { get; }

    public string AutomationId => $"RemoteNovelBookCard_{Book.Id}";

    public double OverallProgressPercent => Book.Progress * 100;

    public string OverallProgressText => $"{OverallProgressPercent:0.0}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial BitmapImage? CoverImage { get; set; }

    public bool HasCover => CoverImage != null;
    public string? CoverPath { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(HasDownloadStatus))]
    [NotifyPropertyChangedFor(nameof(DownloadStatusText))]
    public partial RemoteNovelDownloadState DownloadState { get; set; }

    public bool IsDownloading => DownloadState is
        RemoteNovelDownloadState.Queued or RemoteNovelDownloadState.Downloading;
    public bool CanRetry => DownloadState is
        RemoteNovelDownloadState.Idle or RemoteNovelDownloadState.Failed;
    public bool HasDownloadStatus => DownloadState != RemoteNovelDownloadState.Idle;
    public string DownloadStatusText => DownloadState switch
    {
        RemoteNovelDownloadState.Queued => ResourceStringHelper.GetString(
            "RemoteNovelDownloadQueued",
            "Queued"),
        RemoteNovelDownloadState.Downloading => ResourceStringHelper.GetString(
            "RemoteNovelDownloading",
            "Downloading"),
        RemoteNovelDownloadState.Failed => ResourceStringHelper.GetString(
            "RemoteNovelDownloadRetry",
            "Retry"),
        _ => string.Empty,
    };

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    public void ApplyCoverPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            CoverPath = null;
            CoverImage = null;
            return;
        }

        CoverPath = path;
        try
        {
            CoverImage = new BitmapImage(new Uri(path, UriKind.Absolute));
        }
        catch
        {
            CoverImage = null;
        }
    }
}
