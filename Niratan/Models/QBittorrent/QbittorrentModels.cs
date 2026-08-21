using System;
using System.Collections.Generic;

namespace Niratan.Models.QBittorrent;

public sealed class QbittorrentSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string DefaultSavePath { get; set; } = "";
    public string DefaultCategory { get; set; } = "niratan";
    public bool AddPaused { get; set; }

    public QbittorrentSettings Clone() => new()
    {
        BaseUrl = BaseUrl,
        DefaultSavePath = DefaultSavePath,
        DefaultCategory = DefaultCategory,
        AddPaused = AddPaused,
    };
}

public sealed record QbittorrentCredentials(
    string Username,
    string Password,
    string ApiKey);

public sealed record QbittorrentConnectionInfo(
    string ApplicationVersion,
    string WebApiVersion);

public sealed record QbittorrentTorrent(
    string Hash,
    string Name,
    string State,
    double Progress,
    long SizeBytes,
    long AmountLeftBytes,
    long DownloadRateBytesPerSecond,
    long UploadRateBytesPerSecond,
    long EtaSeconds,
    double Ratio,
    string Category,
    string Tags,
    string SavePath,
    string ContentPath,
    DateTimeOffset? AddedAt,
    DateTimeOffset? CompletedAt)
{
    public double ProgressPercent => Math.Clamp(Progress, 0, 1) * 100;

    public bool IsPaused => State.Contains("paused", StringComparison.OrdinalIgnoreCase);

    public bool IsCompleted => Progress >= 0.999999 || State.Contains("UP", StringComparison.Ordinal);

    public bool CanPause => !IsPaused && !IsCompleted && !State.Equals("error", StringComparison.OrdinalIgnoreCase);

    public bool CanResume => IsPaused;

    public bool CanDelete => !string.IsNullOrWhiteSpace(Hash);
}

public sealed record QbittorrentTorrentProperties(
    string SavePath,
    DateTimeOffset? CreationDate,
    long PieceSizeBytes,
    string Comment,
    long TotalWastedBytes,
    long TotalUploadedBytes,
    long TotalDownloadedBytes,
    long DownloadSpeedAverageBytesPerSecond,
    long UploadSpeedAverageBytesPerSecond,
    long EtaSeconds,
    long Peers,
    long PeersTotal,
    long Seeds,
    long SeedsTotal,
    long PiecesHave,
    long PiecesTotal,
    long Connections,
    long ConnectionsLimit,
    double ShareRatio,
    long TotalSizeBytes,
    bool IsPrivate,
    string CreatedBy,
    DateTimeOffset? AddedAt,
    DateTimeOffset? CompletedAt);

public sealed record QbittorrentTorrentFile(
    int Index,
    string Name,
    long SizeBytes,
    double Progress,
    int Priority,
    bool IsSeed,
    double Availability)
{
    public double ProgressPercent => Math.Clamp(Progress, 0, 1) * 100;
}

public sealed record QbittorrentTorrentTracker(
    string Url,
    int Status,
    int Tier,
    int Peers,
    int Seeds,
    int Leeches,
    int Downloaded,
    string Message);

public sealed record QbittorrentTorrentDetails(
    QbittorrentTorrentProperties Properties,
    IReadOnlyList<QbittorrentTorrentFile> Files,
    IReadOnlyList<QbittorrentTorrentTracker> Trackers);
