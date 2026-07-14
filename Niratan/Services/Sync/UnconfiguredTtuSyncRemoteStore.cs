using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public sealed class UnconfiguredTtuSyncRemoteStore : ITtuSyncRemoteStore
{
    public Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(
        CancellationToken ct = default) =>
        throw CreateException();

    public Task TrashRemoteBookAsync(
        TtuRemoteBook remoteBook,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task<TtuRemoteBookFiles> ListBookFilesAsync(
        string bookTitle,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task<TtuProgress?> GetProgressAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task<TtuAudioBook?> GetAudioBookAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task DownloadBookDataAsync(
        TtuRemoteFile file,
        string destinationFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task UpsertProgressAsync(
        string bookTitle,
        TtuProgress progress,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task UpsertStatisticsAsync(
        string bookTitle,
        IReadOnlyList<NovelReadingStatistic> statistics,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        throw CreateException();

    public Task UpsertAudioBookAsync(
        string bookTitle,
        TtuAudioBook audioBook,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        throw CreateException();

    private static InvalidOperationException CreateException() =>
        new("ッツ Sync remote transport is not configured.");
}
