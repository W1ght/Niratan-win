using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Microsoft.Extensions.Logging;

namespace Hoshi.Services.Sync;

public sealed class ReaderAutoSyncCoordinator : IReaderAutoSyncCoordinator
{
    private static readonly TimeSpan ExportDelay = TimeSpan.FromSeconds(30);

    private readonly ITtuSyncService _ttuSyncService;
    private readonly ISettingsService _settingsService;
    private readonly IGoogleDriveAuthService _googleDriveAuthService;
    private readonly ILogger<ReaderAutoSyncCoordinator> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private bool _cancelled;
    private bool _pending;
    private bool _flushInProgress;
    private NovelBook? _pendingBook;
    private CancellationTokenSource? _debounceCts;
    private Task? _debounceTask;

    public ReaderAutoSyncCoordinator(
        ITtuSyncService ttuSyncService,
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        ILogger<ReaderAutoSyncCoordinator> logger)
        : this(
            ttuSyncService,
            settingsService,
            googleDriveAuthService,
            logger,
            DefaultDelay)
    {
    }

    internal ReaderAutoSyncCoordinator(
        ITtuSyncService ttuSyncService,
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        ILogger<ReaderAutoSyncCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _ttuSyncService = ttuSyncService;
        _settingsService = settingsService;
        _googleDriveAuthService = googleDriveAuthService;
        _logger = logger;
        _delayAsync = delayAsync;
    }

    public async Task<bool> ImportOnOpenAsync(
        NovelBook book,
        CancellationToken ct = default)
    {
        if (!CanAutoSync())
            return false;

        try
        {
            var result = await _ttuSyncService.SyncBookAsync(
                book,
                CreateOptions(TtuSyncDirection.Auto, importOnly: true),
                ct);
            return result.Kind == TtuSyncResultKind.Imported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException ex)
        {
            LogContainedFailure("open import", book, ex);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            LogContainedFailure("open import", book, ex);
            return false;
        }
    }

    public void ScheduleExport(NovelBook book)
    {
        if (!CanAutoSync())
            return;

        lock (_stateGate)
        {
            if (_cancelled)
                return;

            _pending = true;
            _pendingBook = book;
            if (_flushInProgress || _debounceTask != null)
                return;

            StartDebounceLocked();
        }
    }

    public async Task FlushAsync(NovelBook book, CancellationToken ct = default)
    {
        if (!CanAutoSync())
            return;

        await _flushGate.WaitAsync(ct);
        try
        {
            Task? debounceTask;
            lock (_stateGate)
            {
                if (_cancelled)
                    return;

                _flushInProgress = true;
                _pending = true;
                _pendingBook = book;
                debounceTask = _debounceTask;
                _debounceCts?.Cancel();
            }

            if (debounceTask != null)
                await debounceTask.WaitAsync(ct);

            while (true)
            {
                await RunPendingExportsAsync(ct);

                lock (_stateGate)
                {
                    if (_cancelled || !_pending)
                        return;
                }
            }
        }
        finally
        {
            lock (_stateGate)
            {
                _flushInProgress = false;
                if (!_cancelled && _pending && _debounceTask == null)
                    StartDebounceLocked();
            }

            _flushGate.Release();
        }
    }

    public void Cancel()
    {
        lock (_stateGate)
        {
            _cancelled = true;
            _pending = false;
            _pendingBook = null;
            _debounceCts?.Cancel();
        }
    }

    private bool CanAutoSync()
    {
        var sync = _settingsService.Current.TtuSyncSettings;
        return sync.EnableSync
            && sync.EnableAutoSync
            && _googleDriveAuthService.HasCredentials;
    }

    private TtuSyncOptions CreateOptions(
        TtuSyncDirection direction,
        bool importOnly)
    {
        var current = _settingsService.Current;
        return new TtuSyncOptions(
            Direction: direction,
            SyncBookData: current.TtuSyncSettings.UploadBooks,
            SyncStatistics: current.TtuSyncSettings.EnableSync
                && current.StatisticsSettings.EnableSync,
            StatisticsSyncMode: current.StatisticsSettings.SyncMode,
            SyncAudioBook: current.SasayakiSettings.EnableSasayaki
                && current.SasayakiSettings.EnableSync,
            ImportOnly: importOnly);
    }

    private void StartDebounceLocked()
    {
        var owner = new CancellationTokenSource();
        _debounceCts = owner;
        var task = RunDebounceAsync(owner);
        _debounceTask = task;

        if (!task.IsCompleted)
            return;

        _debounceTask = null;
        if (ReferenceEquals(_debounceCts, owner))
        {
            _debounceCts = null;
            owner.Dispose();
        }
    }

    private async Task RunDebounceAsync(CancellationTokenSource owner)
    {
        try
        {
            await _delayAsync(ExportDelay, owner.Token);
            await RunPendingExportsAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_debounceCts, owner))
                {
                    _debounceCts = null;
                    _debounceTask = null;
                    owner.Dispose();

                    if (!_cancelled && !_flushInProgress && _pending)
                        StartDebounceLocked();
                }
            }
        }
    }

    private async Task RunPendingExportsAsync(CancellationToken ct)
    {
        await _exportGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                if (!CanAutoSync())
                {
                    lock (_stateGate)
                    {
                        _pending = false;
                        _pendingBook = null;
                    }

                    return;
                }

                NovelBook? book;
                lock (_stateGate)
                {
                    if (_cancelled || !_pending)
                        return;

                    _pending = false;
                    book = _pendingBook;
                    _pendingBook = null;
                }

                if (book == null)
                    return;

                try
                {
                    await _ttuSyncService.SyncBookAsync(
                        book,
                        CreateOptions(TtuSyncDirection.ExportToTtu, importOnly: false),
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogContainedFailure("export", book, ex);
                }
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private void LogContainedFailure(
        string operation,
        NovelBook book,
        Exception exception)
    {
        _logger.LogWarning(
            "Reader {Operation} sync failed for {BookId} ({ExceptionType})",
            operation,
            book.Id,
            exception.GetType().Name);
    }

    private static Task DefaultDelay(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, ct);
}
