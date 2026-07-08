using System;
using System.Collections.Generic;
using Hoshi.Models.Novel;
using Hoshi.Models.Settings;

namespace Hoshi.Models.Sync;

public enum TtuSyncDirection
{
    Auto,
    ImportFromTtu,
    ExportToTtu,
}

public enum TtuResolvedSyncDirection
{
    Synced,
    ImportFromTtu,
    ExportToTtu,
}

public enum TtuSyncResultKind
{
    Synced,
    Imported,
    Exported,
    Skipped,
}

public sealed record TtuSyncOptions(
    TtuSyncDirection Direction = TtuSyncDirection.Auto,
    bool SyncBookData = false,
    bool SyncStatistics = false,
    StatisticsSyncMode StatisticsSyncMode = StatisticsSyncMode.Merge,
    bool SyncAudioBook = false,
    bool ImportOnly = false);

public sealed record TtuSyncResult(
    TtuSyncResultKind Kind,
    string Title,
    int CharacterCount = 0);

public sealed record TtuRemoteFile(
    string Id,
    string Name,
    string? ParentId = null,
    string? ThumbnailLink = null);

public sealed record TtuRemoteBook(
    string Id,
    string Title,
    string SanitizedTitle,
    TtuRemoteBookFiles Files,
    double Progress);

public sealed record TtuBookImportOptions(
    bool SyncStatistics = false,
    bool SyncAudioBook = false,
    StatisticsSyncMode StatisticsSyncMode = StatisticsSyncMode.Merge);

public sealed record TtuRemoteBookFiles(
    TtuRemoteFile? Progress,
    TtuRemoteFile? Statistics,
    TtuRemoteFile? AudioBook,
    TtuRemoteFile? BookData,
    TtuRemoteFile? Cover)
{
    public static TtuRemoteBookFiles FromFiles(IEnumerable<TtuRemoteFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        TtuRemoteFile? progress = null;
        TtuRemoteFile? statistics = null;
        TtuRemoteFile? audioBook = null;
        TtuRemoteFile? bookData = null;
        TtuRemoteFile? cover = null;

        foreach (var file in files)
        {
            if (file.Name.StartsWith("progress_", StringComparison.Ordinal) && progress == null)
                progress = file;
            else if (file.Name.StartsWith("statistics_", StringComparison.Ordinal) && statistics == null)
                statistics = file;
            else if (file.Name.StartsWith("audioBook_", StringComparison.Ordinal) && audioBook == null)
                audioBook = file;
            else if (file.Name.StartsWith("bookdata_", StringComparison.Ordinal) && bookData == null)
                bookData = file;
            else if (file.Name.StartsWith("cover_", StringComparison.Ordinal) && cover == null)
                cover = file;
        }

        return new TtuRemoteBookFiles(progress, statistics, audioBook, bookData, cover);
    }
}

public sealed record TtuProgress(
    int DataId,
    int ExploredCharCount,
    double Progress,
    DateTimeOffset LastBookmarkModified);

public sealed record TtuAudioBook(
    string Title,
    double PlaybackPosition,
    long LastAudioBookModified);

public sealed class TtuSyncSettings
{
    public bool EnableSync { get; set; }
    public TtuSettingsSyncMode SyncMode { get; set; } = TtuSettingsSyncMode.Auto;
    public bool EnableAutoSync { get; set; }
    public string GoogleClientId { get; set; } = "";
    public bool UploadBooks { get; set; }
}

public enum TtuSettingsSyncMode
{
    Auto,
    Manual,
}
