using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Services.Sync;

public sealed class TtuSyncService : ITtuSyncService
{
    private readonly INovelBookSidecarService _bookSidecars;
    private readonly INovelStatisticsSidecarService _statisticsSidecars;
    private readonly ISasayakiSidecarService _sasayakiSidecars;
    private readonly ITtuSyncRemoteStore _remoteStore;

    public TtuSyncService(
        INovelBookSidecarService bookSidecars,
        INovelStatisticsSidecarService statisticsSidecars,
        ISasayakiSidecarService sasayakiSidecars,
        ITtuSyncRemoteStore remoteStore)
    {
        _bookSidecars = bookSidecars;
        _statisticsSidecars = statisticsSidecars;
        _sasayakiSidecars = sasayakiSidecars;
        _remoteStore = remoteStore;
    }

    public async Task<TtuSyncResult> SyncBookAsync(
        NovelBook book,
        TtuSyncOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (string.IsNullOrWhiteSpace(book.ExtractedPath))
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title);

        var bookRootPath = book.ExtractedPath;
        var remoteFiles = options.KnownRemoteFiles
            ?? await _remoteStore.ListBookFilesAsync(book.Title, ct);
        var localBookmark = await _bookSidecars.LoadBookmarkAsync(bookRootPath, ct);
        var direction = ResolveDirection(options.Direction, localBookmark, remoteFiles.Progress);

        if (options.ImportOnly
            && direction == TtuResolvedSyncDirection.Synced
            && options.SyncAudioBook
            && ShouldImportAudioBook(remoteFiles.AudioBook, bookRootPath)
            && remoteFiles.AudioBook != null)
        {
            var audioBook = await _remoteStore.GetAudioBookAsync(remoteFiles.AudioBook, ct);
            if (audioBook == null)
                return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title, localBookmark?.CharacterCount ?? 0);

            await ImportAudioBookAsync(bookRootPath, audioBook, ct);
            return new TtuSyncResult(TtuSyncResultKind.Imported, book.Title, localBookmark?.CharacterCount ?? 0);
        }

        if (direction == TtuResolvedSyncDirection.Synced)
            return new TtuSyncResult(TtuSyncResultKind.Synced, book.Title, localBookmark?.CharacterCount ?? 0);

        if (options.ImportOnly && direction != TtuResolvedSyncDirection.ImportFromTtu)
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title, localBookmark?.CharacterCount ?? 0);

        return direction switch
        {
            TtuResolvedSyncDirection.ImportFromTtu => await ImportAsync(book, options, remoteFiles, ct),
            TtuResolvedSyncDirection.ExportToTtu => await ExportAsync(book, options, remoteFiles, localBookmark, ct),
            _ => new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title, localBookmark?.CharacterCount ?? 0),
        };
    }

    private async Task<TtuSyncResult> ImportAsync(
        NovelBook book,
        TtuSyncOptions options,
        TtuRemoteBookFiles remoteFiles,
        CancellationToken ct)
    {
        if (remoteFiles.Progress == null || string.IsNullOrWhiteSpace(book.ExtractedPath))
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title);

        var progress = await _remoteStore.GetProgressAsync(remoteFiles.Progress, ct);
        if (progress == null)
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title);

        IReadOnlyList<NovelReadingStatistic>? importedStatistics = null;
        if (options.SyncStatistics && remoteFiles.Statistics != null)
        {
            var remoteStatistics = await _remoteStore.GetStatisticsAsync(remoteFiles.Statistics, ct);
            if (remoteStatistics != null)
            {
                var localStatistics = await _statisticsSidecars.LoadAsync(book.ExtractedPath, ct);
                importedStatistics = MergeStatistics(
                    localStatistics,
                    remoteStatistics,
                    options.StatisticsSyncMode);
            }
        }

        TtuAudioBook? importedAudioBook = null;
        if (options.SyncAudioBook && remoteFiles.AudioBook != null)
            importedAudioBook = await _remoteStore.GetAudioBookAsync(remoteFiles.AudioBook, ct);

        var importedBookmark = await CreateImportedBookmarkAsync(book, progress, ct);
        SasayakiPlaybackData? importedPlayback = null;
        if (importedAudioBook != null)
        {
            importedPlayback = await _sasayakiSidecars.LoadPlaybackAsync(book.ExtractedPath, ct);
            importedPlayback.LastPosition = importedAudioBook.PlaybackPosition;
        }

        var snapshot = await SidecarSnapshot.CaptureAsync(book.ExtractedPath, ct);
        try
        {
            await _bookSidecars.SaveBookmarkAsync(book.ExtractedPath, importedBookmark, ct);
            if (importedStatistics != null)
                await _statisticsSidecars.SaveAsync(book.ExtractedPath, importedStatistics, ct);
            if (importedPlayback != null)
                await _sasayakiSidecars.SavePlaybackAsync(book.ExtractedPath, importedPlayback, ct);
        }
        catch (Exception commitFailure)
        {
            try
            {
                await snapshot.RestoreAsync();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "TTU import failed and local sidecar rollback also failed.",
                    commitFailure,
                    rollbackFailure);
            }

            throw;
        }

        return new TtuSyncResult(TtuSyncResultKind.Imported, book.Title, progress.ExploredCharCount);
    }

    private async Task<TtuSyncResult> ExportAsync(
        NovelBook book,
        TtuSyncOptions options,
        TtuRemoteBookFiles remoteFiles,
        NovelBookmark? localBookmark,
        CancellationToken ct)
    {
        if (localBookmark?.LastModified == null || string.IsNullOrWhiteSpace(book.ExtractedPath))
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title, localBookmark?.CharacterCount ?? 0);

        var bookInfo = await _bookSidecars.LoadBookInfoAsync(book.ExtractedPath, ct);
        if (bookInfo == null)
            return new TtuSyncResult(TtuSyncResultKind.Skipped, book.Title, localBookmark.CharacterCount);

        var remoteProgress = remoteFiles.Progress == null
            ? null
            : await _remoteStore.GetProgressAsync(remoteFiles.Progress, ct);
        var roundedModified = DateTimeOffset.FromUnixTimeMilliseconds(
            localBookmark.LastModified.Value.ToUnixTimeMilliseconds());
        var progress = new TtuProgress(
            DataId: remoteProgress?.DataId ?? 0,
            ExploredCharCount: localBookmark.CharacterCount,
            Progress: bookInfo.CharacterCount > 0
                ? localBookmark.CharacterCount / (double)bookInfo.CharacterCount
                : 0,
            LastBookmarkModified: roundedModified);

        await _remoteStore.UpsertProgressAsync(book.Title, progress, remoteFiles.Progress, ct);

        if (options.SyncStatistics)
        {
            var localStatistics = await _statisticsSidecars.LoadAsync(book.ExtractedPath, ct);
            var remoteStatistics = remoteFiles.Statistics == null
                ? []
                : await _remoteStore.GetStatisticsAsync(remoteFiles.Statistics, ct) ?? [];
            var merged = MergeStatistics(
                remoteStatistics,
                localStatistics,
                options.StatisticsSyncMode);
            if (merged.Count > 0 || options.StatisticsSyncMode == StatisticsSyncMode.Replace)
                await _remoteStore.UpsertStatisticsAsync(book.Title, merged, remoteFiles.Statistics, ct);
        }

        if (options.SyncAudioBook)
            await ExportAudioBookAsync(book.Title, book.ExtractedPath, remoteFiles.AudioBook, ct);

        return new TtuSyncResult(TtuSyncResultKind.Exported, book.Title, localBookmark.CharacterCount);
    }

    private TtuResolvedSyncDirection ResolveDirection(
        TtuSyncDirection requestedDirection,
        NovelBookmark? localBookmark,
        TtuRemoteFile? remoteProgressFile) =>
        requestedDirection switch
        {
            TtuSyncDirection.ImportFromTtu => TtuResolvedSyncDirection.ImportFromTtu,
            TtuSyncDirection.ExportToTtu => TtuResolvedSyncDirection.ExportToTtu,
            _ => DetermineSyncDirection(localBookmark, remoteProgressFile),
        };

    private static TtuResolvedSyncDirection DetermineSyncDirection(
        NovelBookmark? localBookmark,
        TtuRemoteFile? remoteProgressFile)
    {
        var localModified = localBookmark?.LastModified;
        var remoteModified = TtuSyncFileNames.ParseProgressTimestamp(remoteProgressFile?.Name);

        return (localModified, remoteModified) switch
        {
            (null, null) => TtuResolvedSyncDirection.Synced,
            (null, not null) => TtuResolvedSyncDirection.ImportFromTtu,
            (not null, null) => TtuResolvedSyncDirection.ExportToTtu,
            ({ } local, { } remote) when local > remote => TtuResolvedSyncDirection.ExportToTtu,
            ({ } local, { } remote) when remote > local => TtuResolvedSyncDirection.ImportFromTtu,
            _ => TtuResolvedSyncDirection.Synced,
        };
    }

    private async Task<NovelBookmark> CreateImportedBookmarkAsync(
        NovelBook book,
        TtuProgress progress,
        CancellationToken ct)
    {
        var bookRootPath = book.ExtractedPath!;
        var bookInfo = await _bookSidecars.LoadBookInfoAsync(bookRootPath, ct);
        var resolved = ResolveCharacterPosition(bookInfo, progress);
        return new NovelBookmark(
            resolved.ChapterIndex,
            resolved.ChapterProgress,
            progress.ExploredCharCount,
            progress.LastBookmarkModified);
    }

    private static ReaderPosition ResolveCharacterPosition(
        NovelBookInfo? bookInfo,
        TtuProgress progress)
    {
        if (bookInfo == null || bookInfo.ChapterInfo.Count == 0)
            return new ReaderPosition(0, Math.Clamp(progress.Progress, 0, 1));

        var target = Math.Clamp(progress.ExploredCharCount, 0, bookInfo.CharacterCount);
        var chapters = bookInfo.ChapterInfo
            .Values
            .Where(chapter => chapter.SpineIndex.HasValue)
            .OrderBy(chapter => chapter.CurrentTotal)
            .ThenBy(chapter => chapter.SpineIndex)
            .ToList();
        if (chapters.Count == 0)
            return new ReaderPosition(0, Math.Clamp(progress.Progress, 0, 1));

        var chapter = chapters.LastOrDefault(item => target >= item.CurrentTotal) ?? chapters[0];
        var readInChapter = Math.Clamp(target - chapter.CurrentTotal, 0, chapter.ChapterCount);
        var chapterProgress = chapter.ChapterCount > 0
            ? readInChapter / (double)chapter.ChapterCount
            : 0;
        return new ReaderPosition(chapter.SpineIndex!.Value, chapterProgress);
    }

    private bool ShouldImportAudioBook(
        TtuRemoteFile? remoteFile,
        string bookRootPath)
    {
        var remoteModified = TtuSyncFileNames.ParseAudioBookTimestamp(remoteFile?.Name);
        if (remoteModified == null)
            return false;

        var playbackPath = Path.Combine(bookRootPath, ISasayakiSidecarService.PlaybackFileName);
        if (!File.Exists(playbackPath))
            return true;

        var localModified = new DateTimeOffset(File.GetLastWriteTimeUtc(playbackPath), TimeSpan.Zero);
        return remoteModified > localModified;
    }

    private async Task ImportAudioBookAsync(
        string bookRootPath,
        TtuAudioBook audioBook,
        CancellationToken ct)
    {
        var playback = await _sasayakiSidecars.LoadPlaybackAsync(bookRootPath, ct);
        playback.LastPosition = audioBook.PlaybackPosition;
        await _sasayakiSidecars.SavePlaybackAsync(bookRootPath, playback, ct);
    }

    private async Task ExportAudioBookAsync(
        string bookTitle,
        string bookRootPath,
        TtuRemoteFile? existingFile,
        CancellationToken ct)
    {
        var playbackPath = Path.Combine(bookRootPath, ISasayakiSidecarService.PlaybackFileName);
        if (!File.Exists(playbackPath))
            return;

        var playback = await _sasayakiSidecars.LoadPlaybackAsync(bookRootPath, ct);
        var audioBook = new TtuAudioBook(
            bookTitle,
            playback.LastPosition,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await _remoteStore.UpsertAudioBookAsync(bookTitle, audioBook, existingFile, ct);
    }

    private static IReadOnlyList<NovelReadingStatistic> MergeStatistics(
        IReadOnlyList<NovelReadingStatistic> localStatistics,
        IReadOnlyList<NovelReadingStatistic> externalStatistics,
        StatisticsSyncMode syncMode)
    {
        if (syncMode == StatisticsSyncMode.Replace)
            return DeduplicateStatistics(externalStatistics);

        var grouped = DeduplicateStatistics(localStatistics).ToDictionary(
            statistic => statistic.DateKey,
            StringComparer.Ordinal);
        foreach (var statistic in externalStatistics)
        {
            if (!grouped.TryGetValue(statistic.DateKey, out var existing)
                || statistic.LastStatisticModified > existing.LastStatisticModified)
            {
                grouped[statistic.DateKey] = statistic;
            }
        }

        return grouped
            .Values
            .OrderBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<NovelReadingStatistic> DeduplicateStatistics(
        IReadOnlyList<NovelReadingStatistic> statistics) =>
        statistics
            .GroupBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .Select(group => group.MaxBy(statistic => statistic.LastStatisticModified)!)
            .OrderBy(statistic => statistic.DateKey, StringComparer.Ordinal)
            .ToList();

    private sealed record ReaderPosition(int ChapterIndex, double ChapterProgress);

    private sealed class SidecarSnapshot
    {
        private readonly IReadOnlyList<FileSnapshot> _files;

        private SidecarSnapshot(IReadOnlyList<FileSnapshot> files)
        {
            _files = files;
        }

        public static async Task<SidecarSnapshot> CaptureAsync(
            string bookRootPath,
            CancellationToken ct)
        {
            var paths = new[]
            {
                Path.Combine(bookRootPath, NovelBookSidecarService.BookmarkFileName),
                Path.Combine(bookRootPath, NovelStatisticsSidecarService.StatisticsFileName),
                Path.Combine(bookRootPath, ISasayakiSidecarService.PlaybackFileName),
            };
            var files = new List<FileSnapshot>(paths.Length);
            foreach (var path in paths)
            {
                files.Add(new FileSnapshot(
                    path,
                    File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null));
            }

            return new SidecarSnapshot(files);
        }

        public async Task RestoreAsync()
        {
            foreach (var file in _files)
            {
                if (file.Content == null)
                {
                    if (File.Exists(file.Path))
                        File.Delete(file.Path);
                    continue;
                }

                var tempPath = file.Path + "." + Guid.NewGuid().ToString("N") + ".rollback.tmp";
                try
                {
                    await File.WriteAllBytesAsync(tempPath, file.Content, CancellationToken.None);
                    File.Move(tempPath, file.Path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }
        }

        private sealed record FileSnapshot(string Path, byte[]? Content);
    }
}
