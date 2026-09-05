using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Video;

namespace Niratan.ViewModels.Components;

public partial class JimakuSubtitleItemViewModel : ObservableObject
{
    public JimakuSubtitleItemViewModel(JimakuSubtitleItem item) => Item = item;

    public JimakuSubtitleItem Item { get; }
    public string Title => Item.FileName;
    public string Metadata
    {
        get
        {
            var language = string.IsNullOrWhiteSpace(Item.Language)
                ? ResourceStringHelper.GetString("JimakuUnknownLanguage", "Unknown language")
                : Item.Language;
            var episode = Item.EpisodeNumber is int number ? $" · E{number:00}" : "";
            var size = Item.SizeBytes is long bytes ? $" · {bytes / 1024d / 1024d:0.0} MiB" : "";
            return $"Jimaku · {Item.EntryName} · {language}{episode}{size}";
        }
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    public bool CanDownload => !IsDownloading;

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));
}
