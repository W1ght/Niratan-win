using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public interface ITtuSyncRemoteStore
{
    Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(
        CancellationToken ct = default);

    Task TrashRemoteBookAsync(
        TtuRemoteBook remoteBook,
        CancellationToken ct = default);

    Task<TtuRemoteBookFiles> ListBookFilesAsync(
        string bookTitle,
        CancellationToken ct = default);

    Task<TtuProgress?> GetProgressAsync(
        TtuRemoteFile file,
        CancellationToken ct = default);

    Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(
        TtuRemoteFile file,
        CancellationToken ct = default);

    Task<TtuAudioBook?> GetAudioBookAsync(
        TtuRemoteFile file,
        CancellationToken ct = default);

    Task DownloadBookDataAsync(
        TtuRemoteFile file,
        string destinationFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    Task UpsertProgressAsync(
        string bookTitle,
        TtuProgress progress,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default);

    Task UpsertStatisticsAsync(
        string bookTitle,
        IReadOnlyList<NovelReadingStatistic> statistics,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default);

    Task UpsertAudioBookAsync(
        string bookTitle,
        TtuAudioBook audioBook,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default);
}
