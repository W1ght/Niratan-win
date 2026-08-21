using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Helpers;
using Niratan.Models.QBittorrent;

namespace Niratan.ViewModels.Components;

public sealed class QbittorrentTorrentDetailsViewModel
{
    public QbittorrentTorrentProperties Properties { get; }
    public IReadOnlyList<QbittorrentTorrentFileViewModel> Files { get; }
    public IReadOnlyList<QbittorrentTorrentTrackerViewModel> Trackers { get; }

    public string Hash { get; }
    public string Status { get; }
    public double ProgressValue { get; }
    public string ProgressText { get; }
    public string SizeText { get; }
    public string TorrentSizeText { get; }
    public string RemainingText { get; }
    public string DownloadRateText { get; }
    public string UploadRateText { get; }
    public string AverageDownloadRateText { get; }
    public string AverageUploadRateText { get; }
    public string EtaText { get; }
    public string RatioText { get; }
    public string TotalDownloadedText { get; }
    public string TotalUploadedText { get; }
    public string TotalWastedText { get; }
    public string AddedAtText { get; }
    public string CompletedAtText { get; }
    public string CreationDateText { get; }
    public string PieceSizeText { get; }
    public string CreatedByText { get; }
    public string SavePathText { get; }
    public string ContentPathText { get; }
    public string PeerSummaryText { get; }
    public string SeedSummaryText { get; }
    public string PiecesText { get; }
    public string ConnectionSummaryText { get; }
    public string PrivacyText { get; }
    public string CommentText { get; }
    public bool HasComment => !string.IsNullOrWhiteSpace(Properties.Comment);

    public QbittorrentTorrentDetailsViewModel(
        QbittorrentTorrent task,
        QbittorrentTorrentDetails details)
    {
        Properties = details.Properties;
        Files = details.Files.Select(file => new QbittorrentTorrentFileViewModel(file)).ToList();
        Trackers = details.Trackers.Select(tracker => new QbittorrentTorrentTrackerViewModel(tracker)).ToList();

        Hash = task.Hash;
        Status = task.State;
        ProgressValue = task.ProgressPercent;
        ProgressText = $"{task.ProgressPercent:0.##}%";
        SizeText = FormatBytes(task.SizeBytes);
        TorrentSizeText = FormatBytes(details.Properties.TotalSizeBytes > 0
            ? details.Properties.TotalSizeBytes
            : task.SizeBytes);
        RemainingText = FormatBytes(task.AmountLeftBytes);
        DownloadRateText = FormatRate(task.DownloadRateBytesPerSecond);
        UploadRateText = FormatRate(task.UploadRateBytesPerSecond);
        AverageDownloadRateText = FormatRate(details.Properties.DownloadSpeedAverageBytesPerSecond);
        AverageUploadRateText = FormatRate(details.Properties.UploadSpeedAverageBytesPerSecond);
        EtaText = FormatEta(task.EtaSeconds);
        RatioText = details.Properties.ShareRatio.ToString("0.##");
        TotalDownloadedText = FormatBytes(details.Properties.TotalDownloadedBytes);
        TotalUploadedText = FormatBytes(details.Properties.TotalUploadedBytes);
        TotalWastedText = FormatBytes(details.Properties.TotalWastedBytes);
        AddedAtText = FormatDate(details.Properties.AddedAt ?? task.AddedAt);
        CompletedAtText = FormatDate(details.Properties.CompletedAt ?? task.CompletedAt);
        CreationDateText = FormatDate(details.Properties.CreationDate);
        PieceSizeText = FormatBytes(details.Properties.PieceSizeBytes);
        CreatedByText = string.IsNullOrWhiteSpace(details.Properties.CreatedBy)
            ? ResourceStringHelper.GetString("DownloadsUnknownValue", "Unknown")
            : details.Properties.CreatedBy;
        SavePathText = string.IsNullOrWhiteSpace(details.Properties.SavePath)
            ? task.SavePath
            : details.Properties.SavePath;
        ContentPathText = task.ContentPath;
        PeerSummaryText = FormatCount(details.Properties.Peers, details.Properties.PeersTotal);
        SeedSummaryText = FormatCount(details.Properties.Seeds, details.Properties.SeedsTotal);
        PiecesText = FormatCount(details.Properties.PiecesHave, details.Properties.PiecesTotal);
        ConnectionSummaryText = FormatCount(
            details.Properties.Connections,
            details.Properties.ConnectionsLimit);
        PrivacyText = details.Properties.IsPrivate
            ? ResourceStringHelper.GetString("DownloadsPrivateTorrent", "Private torrent")
            : ResourceStringHelper.GetString("DownloadsPublicTorrent", "Public torrent");
        CommentText = string.IsNullOrWhiteSpace(details.Properties.Comment)
            ? ResourceStringHelper.GetString("DownloadsNoComment", "No comment")
            : details.Properties.Comment;
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

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        ?? ResourceStringHelper.GetString("DownloadsUnknownValue", "Unknown");

    private static string FormatCount(long current, long total) =>
        current < 0
            ? ResourceStringHelper.GetString("DownloadsUnknownValue", "Unknown")
            : total < 0 ? current.ToString() : $"{current} / {total}";
}

public sealed class QbittorrentTorrentFileViewModel
{
    public string Name { get; }
    public string SizeText { get; }
    public string ProgressText { get; }
    public string PriorityText { get; }
    public string AvailabilityText { get; }
    public string SeedStatusText { get; }

    public QbittorrentTorrentFileViewModel(QbittorrentTorrentFile file)
    {
        Name = file.Name;
        SizeText = FormatBytes(file.SizeBytes);
        ProgressText = $"{file.ProgressPercent:0.##}%";
        PriorityText = file.Priority switch
        {
            0 => ResourceStringHelper.GetString("DownloadsPriorityDoNotDownload", "Do not download"),
            6 => ResourceStringHelper.GetString("DownloadsPriorityHigh", "High"),
            7 => ResourceStringHelper.GetString("DownloadsPriorityMaximum", "Maximum"),
            _ => ResourceStringHelper.GetString("DownloadsPriorityNormal", "Normal"),
        };
        SeedStatusText = file.IsSeed
            ? ResourceStringHelper.GetString("DownloadsFileSeed", "Seeding")
            : ResourceStringHelper.GetString("DownloadsFileNotSeed", "Not seeding");
        AvailabilityText = $"{Math.Max(0, file.Availability) * 100:0.##}% · {SeedStatusText}";
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
}

public sealed class QbittorrentTorrentTrackerViewModel
{
    public string Url { get; }
    public string StatusText { get; }
    public string PeerSummaryText { get; }
    public string MessageText { get; }
    public string TierText { get; }
    public string DownloadedText { get; }

    public QbittorrentTorrentTrackerViewModel(QbittorrentTorrentTracker tracker)
    {
        Url = tracker.Url;
        StatusText = tracker.Status switch
        {
            2 => ResourceStringHelper.GetString("DownloadsTrackerWorking", "Working"),
            3 => ResourceStringHelper.GetString("DownloadsTrackerUpdating", "Updating"),
            4 => ResourceStringHelper.GetString("DownloadsTrackerError", "Error"),
            0 => ResourceStringHelper.GetString("DownloadsTrackerDisabled", "Disabled"),
            _ => ResourceStringHelper.GetString("DownloadsTrackerNotContacted", "Not contacted"),
        };
        PeerSummaryText = $"{tracker.Seeds} seeds · {tracker.Peers} peers · {tracker.Leeches} leeches";
        MessageText = tracker.Message;
        TierText = $"{ResourceStringHelper.GetString("DownloadsTrackerTier", "Tier")}: {tracker.Tier}";
        DownloadedText = $"{ResourceStringHelper.GetString("DownloadsTrackerDownloaded", "Downloaded")}: {tracker.Downloaded}";
    }
}
