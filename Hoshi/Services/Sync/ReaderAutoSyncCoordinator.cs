using System;
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
    private readonly Func<Task> _onDebounceDrainedAsync;
    private readonly Func<Task> _afterFlushGateEvaluatedAsync;
    private readonly Func<Task> _afterFlushCompletionPublishedAsync;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    private bool _cancelled;
    private bool _pending;
    private long _pendingGeneration;
    private bool _flushInProgress;
    private NovelBook? _pendingBook;
    private CancellationTokenSource? _debounceCts;
    private Task? _debounceTask;
    private TaskCompletionSource? _flushCompletion;

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
        : this(
            ttuSyncService,
            settingsService,
            googleDriveAuthService,
            logger,
            delayAsync,
            new ReaderAutoSyncCoordinatorTestHooks())
    {
    }

    internal ReaderAutoSyncCoordinator(
        ITtuSyncService ttuSyncService,
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        ILogger<ReaderAutoSyncCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<Task> onDebounceDrainedAsync)
        : this(
            ttuSyncService,
            settingsService,
            googleDriveAuthService,
            logger,
            delayAsync,
            new ReaderAutoSyncCoordinatorTestHooks
            {
                AfterDebounceDrainedAsync = onDebounceDrainedAsync,
            })
    {
    }

    internal ReaderAutoSyncCoordinator(
        ITtuSyncService ttuSyncService,
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        ILogger<ReaderAutoSyncCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        ReaderAutoSyncCoordinatorTestHooks testHooks)
    {
        _ttuSyncService = ttuSyncService;
        _settingsService = settingsService;
        _googleDriveAuthService = googleDriveAuthService;
        _logger = logger;
        _delayAsync = delayAsync;
        _onDebounceDrainedAsync = testHooks.AfterDebounceDrainedAsync;
        _afterFlushGateEvaluatedAsync = testHooks.AfterFlushGateEvaluatedAsync;
        _afterFlushCompletionPublishedAsync = testHooks.AfterFlushCompletionPublishedAsync;
    }

    public async Task<bool> ImportOnOpenAsync(
        NovelBook book,
        CancellationToken ct = default)
    {
        try
        {
            lock (_stateGate)
            {
                if (_cancelled)
                    return false;
            }

            if (!CanAutoSync())
                return false;

            lock (_stateGate)
            {
                if (_cancelled)
                    return false;
            }

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
        catch (Exception ex)
        {
            LogContainedFailure("open import", book, ex);
            return false;
        }
    }

    public void ScheduleExport(NovelBook book)
    {
        lock (_stateGate)
        {
            if (_cancelled)
                return;
        }

        bool canAutoSync;
        try
        {
            canAutoSync = CanAutoSync();
        }
        catch (Exception ex)
        {
            LogContainedFailure("schedule gate", book, ex);
            return;
        }

        if (!canAutoSync)
            return;

        lock (_stateGate)
        {
            if (_cancelled)
                return;

            _pending = true;
            _pendingBook = book;
            _pendingGeneration++;
            if (_flushInProgress || _debounceTask != null)
                return;

            StartDebounceLocked();
        }
    }

    public async Task FlushAsync(NovelBook book, CancellationToken ct = default)
    {
        Task? activeFlush;
        lock (_stateGate)
        {
            if (_cancelled)
                return;
            activeFlush = _flushCompletion?.Task;
        }

        if (activeFlush != null)
        {
            await activeFlush.WaitAsync(ct);
            return;
        }

        bool canAutoSync;
        Exception? gateFailure = null;
        try
        {
            canAutoSync = CanAutoSync();
        }
        catch (Exception ex)
        {
            gateFailure = ex;
            canAutoSync = false;
        }

        await _afterFlushGateEvaluatedAsync();

        if (gateFailure != null)
            LogContainedFailure("flush gate", book, gateFailure);

        TaskCompletionSource? ownedCompletion = null;
        Task? sharedFlush = null;
        Task? debounceTask = null;
        lock (_stateGate)
        {
            if (_cancelled)
                return;

            if (_flushCompletion != null)
            {
                sharedFlush = _flushCompletion.Task;
            }
            else if (canAutoSync)
            {
                ownedCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _flushCompletion = ownedCompletion;
                sharedFlush = ownedCompletion.Task;
                _flushInProgress = true;
                _pending = true;
                _pendingBook = book;
                _pendingGeneration++;
                debounceTask = _debounceTask;
                _debounceCts?.Cancel();
            }
        }

        if (sharedFlush == null)
            return;

        if (ownedCompletion == null)
        {
            await sharedFlush.WaitAsync(ct);
            return;
        }

        Exception? failure = null;
        try
        {
            if (debounceTask != null)
                await debounceTask.WaitAsync(ct);

            while (true)
            {
                await RunPendingExportsAsync(ct);

                lock (_stateGate)
                {
                    if (_cancelled || !_pending)
                    {
                        if (ReferenceEquals(_flushCompletion, ownedCompletion))
                            ownedCompletion.TrySetResult();

                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (!ownedCompletion.Task.IsCompleted)
            {
                if (failure is OperationCanceledException && ct.IsCancellationRequested)
                    ownedCompletion.TrySetCanceled(ct);
                else if (failure != null)
                {
                    ownedCompletion.TrySetException(failure);
                    _ = ownedCompletion.Task.Exception;
                }
                else
                    ownedCompletion.TrySetResult();
            }

            try
            {
                await _afterFlushCompletionPublishedAsync();
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_flushCompletion, ownedCompletion))
                    {
                        _flushCompletion = null;
                        _flushInProgress = false;
                        if (!_cancelled && _pending && _debounceTask == null)
                            StartDebounceLocked();
                    }
                }
            }
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
            await _onDebounceDrainedAsync();
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
                NovelBook? candidate;
                long candidateGeneration;
                lock (_stateGate)
                {
                    if (_cancelled || !_pending)
                        return;

                    candidate = _pendingBook;
                    candidateGeneration = _pendingGeneration;
                }

                if (candidate == null)
                    return;

                bool canAutoSync;
                try
                {
                    canAutoSync = CanAutoSync();
                }
                catch (Exception ex)
                {
                    LogContainedFailure("export gate", candidate, ex);
                    canAutoSync = false;
                }

                if (!canAutoSync)
                {
                    lock (_stateGate)
                    {
                        if (_pending && _pendingGeneration == candidateGeneration)
                        {
                            _pending = false;
                            _pendingBook = null;
                        }
                    }

                    return;
                }

                NovelBook? book;
                lock (_stateGate)
                {
                    if (_cancelled || !_pending)
                        return;

                    if (_pendingGeneration != candidateGeneration)
                        continue;

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
                    lock (_stateGate)
                    {
                        if (!_cancelled && !_pending)
                        {
                            _pending = true;
                            _pendingBook = book;
                            _pendingGeneration++;
                        }
                    }

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
        try
        {
            _logger.LogWarning(
                "Reader {Operation} sync failed for {BookId} ({ExceptionType})",
                operation,
                book.Id,
                exception.GetType().Name);
        }
        catch
        {
        }
    }

    private static Task DefaultDelay(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, ct);
}

internal sealed class ReaderAutoSyncCoordinatorTestHooks
{
    public Func<Task> AfterDebounceDrainedAsync { get; init; } =
        static () => Task.CompletedTask;

    public Func<Task> AfterFlushGateEvaluatedAsync { get; init; } =
        static () => Task.CompletedTask;

    public Func<Task> AfterFlushCompletionPublishedAsync { get; init; } =
        static () => Task.CompletedTask;
}
