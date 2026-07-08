using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Sync;
using Hoshi.Services.Novels;

namespace Hoshi.Services.Sync;

public sealed class TtuBookImportService : ITtuBookImportService
{
    private readonly ITtuSyncRemoteStore _remoteStore;
    private readonly ITtuBookDataConverter _converter;
    private readonly INovelLibraryService _libraryService;
    private readonly ITtuSyncService _syncService;

    public TtuBookImportService(
        ITtuSyncRemoteStore remoteStore,
        ITtuBookDataConverter converter,
        INovelLibraryService libraryService,
        ITtuSyncService syncService)
    {
        _remoteStore = remoteStore;
        _converter = converter;
        _libraryService = libraryService;
        _syncService = syncService;
    }

    public async Task<Result<NovelBook>> ImportRemoteBookAsync(
        TtuRemoteBook remoteBook,
        TtuBookImportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            if (remoteBook.Files.BookData == null)
                return Result<NovelBook>.Failure("The remote book does not include TTU book data.", "Import failed");

            var importRoot = Path.Combine(
                AppDataHelper.GetDataPath(),
                "TtuSync",
                "Imports",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importRoot);

            var bookDataPath = Path.Combine(importRoot, remoteBook.Files.BookData.Name);
            await _remoteStore.DownloadBookDataAsync(remoteBook.Files.BookData, bookDataPath, progress, ct);
            var epubPath = await _converter.ConvertToEpubAsync(bookDataPath, importRoot, ct);
            TryDelete(bookDataPath);

            var importResult = await _libraryService.ImportEpubAsync(epubPath, ct);
            if (!importResult.IsSuccess || importResult.Value == null)
                return importResult;

            await _syncService.SyncBookAsync(
                importResult.Value,
                new TtuSyncOptions(
                    Direction: TtuSyncDirection.ImportFromTtu,
                    SyncBookData: false,
                    SyncStatistics: options.SyncStatistics,
                    StatisticsSyncMode: options.StatisticsSyncMode,
                    SyncAudioBook: options.SyncAudioBook,
                    ImportOnly: true),
                ct);

            return importResult;
        }
        catch (OperationCanceledException)
        {
            return Result<NovelBook>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<NovelBook>.Failure(ex.Message, "Google Drive import failed");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
