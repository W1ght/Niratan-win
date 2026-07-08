using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Models.Sync;

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
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }
}
