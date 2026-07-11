using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Models.Sync;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Hoshi.ViewModels.Components;

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
    public partial bool IsDownloading { get; set; }

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
