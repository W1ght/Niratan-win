using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Nyaa;

namespace Niratan.ViewModels.Components;

public partial class NyaaTorrentItemViewModel : ObservableObject
{
    public NyaaTorrentItem Item { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsImported { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public string Title => Item.Title;
    public string Metadata => ResourceStringHelper.FormatString(
        "NyaaResultMetadata",
        "{0} · ↑ {1} · ↓ {2} · {3} downloads",
        FormatBytes(Item.SizeBytes),
        Item.Seeders,
        Item.Leechers,
        Item.Downloads);
    public string TrustLabel => Item.IsTrusted
        ? ResourceStringHelper.GetString("NyaaTrustedLabel", "Trusted")
        : Item.IsRemake
            ? ResourceStringHelper.GetString("NyaaRemakeLabel", "Remake")
            : "";
    public bool HasTrustLabel => TrustLabel.Length > 0;
    public bool CanDownload => !IsDownloading && !IsImported;

    public NyaaTorrentItemViewModel(NyaaTorrentItem item)
    {
        Item = item;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return ResourceStringHelper.GetString("NyaaUnknownSize", "Unknown size");
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
