using System;
using System.Collections.Generic;

namespace Niratan.Models.Nyaa;

public sealed record NyaaSearchCategory(string Code, string DisplayName);

public sealed record NyaaSearchRequest(
    string Query,
    string CategoryCode = "0_0",
    int Page = 1);

public sealed record NyaaTorrentItem(
    string Id,
    string Title,
    Uri TorrentUri,
    Uri DetailsUri,
    string Category,
    long SizeBytes,
    int Seeders,
    int Leechers,
    int Downloads,
    DateTimeOffset? PublishedAt,
    bool IsTrusted,
    bool IsRemake);

public sealed record TorrentDownloadProgress(
    string Status,
    double Percent,
    long DownloadRateBytesPerSecond,
    int ConnectedPeers);

public sealed record TorrentDownloadResult(
    string DownloadRootPath,
    IReadOnlyList<string> Files);

public sealed record ResourcePackageAnalysis(
    string RootPath,
    IReadOnlyList<string> EpubFiles,
    IReadOnlyList<string> AudioFiles,
    IReadOnlyList<string> SubtitleFiles,
    IReadOnlyList<string> VideoFiles,
    IReadOnlyList<string> OtherFiles,
    NovelResourceMatch? NovelMatch,
    IReadOnlyDictionary<string, string> VideoSubtitleMatches,
    IReadOnlyList<string> Warnings)
{
    public bool CanAutoMatchNovel => NovelMatch is not null;
}

public sealed record NovelResourceMatch(
    string EpubPath,
    string AudiobookPath,
    string SubtitlePath,
    double Confidence);

public sealed record ResourcePackageImportResult(
    int ImportedNovelCount,
    int MatchedNovelCount,
    int ImportedVideoCount,
    IReadOnlyList<string> Warnings);

public enum NyaaDownloadTaskState
{
    Queued,
    Downloading,
    Paused,
    Importing,
    Completed,
    Failed,
    Cancelled,
}

public sealed record NyaaDownloadTaskSnapshot(
    string TaskId,
    NyaaTorrentItem Item,
    NyaaDownloadTaskState State,
    double ProgressPercent,
    long DownloadRateBytesPerSecond,
    int ConnectedPeers,
    string Status,
    string? DownloadRootPath,
    string? Error,
    ResourcePackageImportResult? ImportResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string StateText => State.ToString();
    public bool CanPause => State == NyaaDownloadTaskState.Downloading;
    public bool CanResume => State == NyaaDownloadTaskState.Paused;
    public bool CanCancel => State is NyaaDownloadTaskState.Queued
        or NyaaDownloadTaskState.Downloading
        or NyaaDownloadTaskState.Paused
        or NyaaDownloadTaskState.Importing;
    public bool CanRetry => State is NyaaDownloadTaskState.Failed
        or NyaaDownloadTaskState.Cancelled;
    public bool CanRemove => State is NyaaDownloadTaskState.Completed
        or NyaaDownloadTaskState.Failed
        or NyaaDownloadTaskState.Cancelled;
    public bool CanOpenFolder => DownloadRootPath is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string ProgressText => $"{ProgressPercent:0.0}%";
    public string DownloadRateText => DownloadRateBytesPerSecond <= 0
        ? "0 MiB/s"
        : $"{DownloadRateBytesPerSecond / 1024d / 1024d:0.0} MiB/s";
}
