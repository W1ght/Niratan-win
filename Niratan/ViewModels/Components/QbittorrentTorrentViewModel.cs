using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.QBittorrent;

namespace Niratan.ViewModels.Components;

public partial class QbittorrentTorrentViewModel : ObservableObject
{
    public QbittorrentTorrent Torrent { get; }

    public string Hash => Torrent.Hash;
    public string Name => Torrent.Name;
    public string State => Torrent.State;
    public double ProgressPercent => Torrent.ProgressPercent;
    public string SizeText => FormatBytes(Torrent.SizeBytes);
    public string RemainingText => FormatBytes(Torrent.AmountLeftBytes);
    public string DownloadRateText => FormatRate(Torrent.DownloadRateBytesPerSecond);
    public string UploadRateText => FormatRate(Torrent.UploadRateBytesPerSecond);
    public string EtaText => FormatEta(Torrent.EtaSeconds);
    public string LocationText => string.IsNullOrWhiteSpace(Torrent.ContentPath)
        ? Torrent.SavePath
        : Torrent.ContentPath;
    public string LocationPath => string.IsNullOrWhiteSpace(Torrent.ContentPath)
        ? Torrent.SavePath
        : Torrent.ContentPath;
    public bool CanOpenLocation => !string.IsNullOrWhiteSpace(LocationPath);
    public string CategoryText => string.IsNullOrWhiteSpace(Torrent.Category)
        ? ResourceStringHelper.GetString("DownloadsNoCategory", "No category")
        : Torrent.Category;
    public bool CanPause => Torrent.CanPause;
    public bool CanResume => Torrent.CanResume;
    public bool CanDelete => Torrent.CanDelete;

    public QbittorrentTorrentViewModel(QbittorrentTorrent torrent)
    {
        Torrent = torrent;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
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

    private static string FormatRate(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? "0 B/s" : $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatEta(long seconds)
    {
        if (seconds <= 0 || seconds == long.MaxValue)
            return ResourceStringHelper.GetString("DownloadsEtaUnknown", "Unknown ETA");
        var duration = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds));
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration.Hours:00}h"
            : duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
