using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Enums;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Models.DTO;
using Niratan.Models.Novel;
using Niratan.Models.Profiles;
using Niratan.Models.Settings;
using Niratan.Services.Novels;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.Services.Sync;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Pages;

public partial class NovelReaderPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;
    private readonly IReaderHighlightService _readerHighlightService;
    private readonly INovelBookSidecarService _novelBookSidecarService;
    private readonly IReaderStatisticsSession _statisticsSession;
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly ISettingsService _settingsService;
    private readonly IReaderAutoSyncCoordinator _readerAutoSyncCoordinator;
    private readonly ReaderNavigationTransactionCoordinator _navigationTransactions;

    [ObservableProperty]
    public partial NovelBook? CurrentBook { get; set; }

    [ObservableProperty]
    public partial int CurrentChapterIndex { get; set; }

    [ObservableProperty]
    public partial int ChapterCount { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial int CurrentCharacterCount { get; set; }

    [ObservableProperty]
    public partial int TotalCharacterCount { get; set; }

    [ObservableProperty]
    public partial bool IsStatisticsTracking { get; set; }

    [ObservableProperty]
    public partial bool IsStatisticsPaused { get; set; }

    private IReadOnlyList<ReaderHighlight> _highlights = [];

    public IReadOnlyList<ReaderHighlight> Highlights
    {
        get => _highlights;
        private set => SetProperty(ref _highlights, value);
    }

    private NovelReadingStatistic _sessionStatistics = InitialStatistic();

    public NovelReadingStatistic SessionStatistics
    {
        get => _sessionStatistics;
        private set => SetProperty(ref _sessionStatistics, value);
    }

    private NovelReadingStatistic _todaysStatistics = InitialStatistic();

    public NovelReadingStatistic TodaysStatistics
    {
        get => _todaysStatistics;
        private set => SetProperty(ref _todaysStatistics, value);
    }

    private NovelReadingStatistic _allTimeStatistics = InitialStatistic();

    public NovelReadingStatistic AllTimeStatistics
    {
        get => _allTimeStatistics;
        private set => SetProperty(ref _allTimeStatistics, value);
    }

    public string ReaderTitle => CurrentBook?.Title ?? "Novel reader";
    public string ChapterTitle => $"章节 {CurrentChapterIndex + 1} / {ChapterCount}";
    public double OverallProgress => TotalCharacterCount > 0
        ? Math.Clamp(CurrentCharacterCount / (double)TotalCharacterCount, 0, 1)
        : ChapterCount <= 0
            ? Math.Clamp(Progress, 0, 1)
            : Math.Clamp((CurrentChapterIndex + Progress) / ChapterCount, 0, 1);
    public string OverallProgressText => (OverallProgress * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    public string ReaderProgressText => TotalCharacterCount <= 0
        ? OverallProgressText
        : $"{CurrentCharacterCount} / {TotalCharacterCount} {OverallProgressText}";
    public bool CanGoNext => CurrentChapterIndex < ChapterCount - 1;
    public bool CanGoPrevious => CurrentChapterIndex > 0;
    public bool CanAcceptReaderPositionMutation =>
        !_navigationTransactions.BlocksPositionMutation;
    public string StatisticsTrackingButtonText => IsStatisticsTracking ? "Pause" : "Start";
    public bool IsEnglishStatisticsContent =>
        _profileRuntime.ActiveLanguage.Id == ContentLanguageProfile.English.Id;
    public string StatisticsSessionCharactersText =>
        FormatStatisticsCount(SessionStatistics.CharactersRead);
    public string StatisticsSessionSpeedText =>
        FormatStatisticsSpeed(SessionStatistics.LastReadingSpeed);
    public string StatisticsSessionTimeText => FormatDuration(SessionStatistics.ReadingTime);
    public string StatisticsSessionChromeTimeText => FormatChromeReadingTime(SessionStatistics.ReadingTime);
    public string StatisticsBookRemainingTimeText => FormatDuration(
        EstimateRemainingSeconds(TotalCharacterCount - CurrentCharacterCount));
    public string StatisticsChapterRemainingTimeText => FormatDuration(
        EstimateRemainingSeconds(CurrentChapterRemainingCharacters));
    public string StatisticsTodayCharactersText =>
        FormatStatisticsCount(TodaysStatistics.CharactersRead);
    public string StatisticsTodaySpeedText =>
        FormatStatisticsSpeed(TodaysStatistics.LastReadingSpeed);
    public string StatisticsTodayTimeText => FormatDuration(TodaysStatistics.ReadingTime);
    public string StatisticsAllTimeCharactersText =>
        FormatStatisticsCount(AllTimeStatistics.CharactersRead);
    public string StatisticsAllTimeSpeedText =>
        FormatStatisticsSpeed(AllTimeStatistics.LastReadingSpeed);
    public string StatisticsAllTimeTimeText => FormatDuration(AllTimeStatistics.ReadingTime);

    private CancellationTokenSource? _saveCts;
    private Task? _saveTask;
    private readonly object _saveGate = new();
    private Task _writerTail = Task.CompletedTask;
    private long _positionRevision;
    private long _currentPositionRevision;
    private bool _lifecycleWriterBarrier;
    private bool _readerWriterClosed;
    private IReadOnlyList<int> _chapterCharacterCounts = [];
    private readonly object _lifecycleCloseGate = new();
    private readonly SemaphoreSlim _lifecycleCheckpointLock = new(1, 1);
    private Task? _lifecycleCloseTask;
    private bool _didCompleteLifecycleClose;
    private bool _suppressPositionNotifications;
    private readonly HashSet<Task> _deferredNavigationStatisticsMutations = [];

    public NovelReaderPageViewModel(
        INovelLibraryService novelLibraryService,
        INotificationService notificationService,
        IMessenger messenger,
        IReaderHighlightService readerHighlightService,
        INovelBookSidecarService novelBookSidecarService,
        IReaderStatisticsSession statisticsSession,
        IProfileRuntimeService profileRuntime,
        ISettingsService settingsService,
        IReaderAutoSyncCoordinator readerAutoSyncCoordinator,
        ReaderNavigationTransactionCoordinator navigationTransactions
    )
    {
        _novelLibraryService = novelLibraryService;
        _notificationService = notificationService;
        _messenger = messenger;
        _readerHighlightService = readerHighlightService;
        _novelBookSidecarService = novelBookSidecarService;
        _statisticsSession = statisticsSession;
        _statisticsSession.StateChanged += OnStatisticsSessionStateChanged;
        _profileRuntime = profileRuntime;
        _settingsService = settingsService;
        _readerAutoSyncCoordinator = readerAutoSyncCoordinator;
        _navigationTransactions = navigationTransactions;
    }

    public async Task InitializeAsync(
        NovelReaderNavigationArgs args,
        CancellationToken ct = default)
    {
        var result = await _novelLibraryService.GetNovelBookAsync(args.BookId, ct);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        CurrentBook = result.Value;

        if (CurrentBook != null)
        {
            await _profileRuntime.ActivateForBookAsync(CurrentBook, ct);
            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id, ct);
        }

        OnPropertyChanged(nameof(ReaderTitle));
    }

    public Task ActivateCurrentProfileAsync(CancellationToken ct = default) =>
        CurrentBook is null
            ? Task.CompletedTask
            : _profileRuntime.ActivateForBookAsync(CurrentBook, ct);

    public async Task<bool> SyncOnOpenAsync(CancellationToken ct = default)
    {
        var book = CurrentBook;
        if (book == null || !await _readerAutoSyncCoordinator.ImportOnOpenAsync(book, ct))
            return false;

        var imported = await _novelLibraryService.GetNovelBookAsync(book.Id, ct);
        if (!imported.IsSuccess || imported.Value == null)
            return false;

        CurrentBook = imported.Value;
        OnPropertyChanged(nameof(ReaderTitle));
        return true;
    }

    public void SetChapter(int index, int count)
    {
        if (!CanAcceptReaderPositionMutation)
            return;

        _currentPositionRevision = ++_positionRevision;
        CurrentChapterIndex = index;
        ChapterCount = count;
        UpdateCharacterProgress();
        OnPropertyChanged(nameof(ChapterTitle));
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(ReaderProgressText));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnStatisticsTextChanged();
    }

    public void UpdateProgress(double progress)
    {
        if (!CanAcceptReaderPositionMutation)
            return;

        _currentPositionRevision = ++_positionRevision;
        Progress = Math.Clamp(progress, 0, 1);
        UpdateCharacterProgress();
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(ReaderProgressText));
        OnStatisticsTextChanged();
    }

    public void SetChapterCharacterCounts(IReadOnlyList<int> chapterCharacterCounts)
    {
        _currentPositionRevision = ++_positionRevision;
        _chapterCharacterCounts = chapterCharacterCounts;
        TotalCharacterCount = chapterCharacterCounts.Sum();
        UpdateCharacterProgress();
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(ReaderProgressText));
        OnStatisticsTextChanged();
    }

    public async Task LoadHighlightsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
        {
            Highlights = [];
            return;
        }

        Highlights = await _readerHighlightService.LoadAsync(CurrentBook.ExtractedPath, ct);
    }

    public async Task LoadStatisticsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
        {
            var empty = ReaderStatisticsMath.Empty(
                ReaderTitle,
                DateOnly.FromDateTime(DateTime.Now));
            ApplyStatisticsState(new ReaderStatisticsSessionState(
                false,
                false,
                empty,
                empty,
                empty,
                []));
            return;
        }

        await _statisticsSession.LoadAsync(
            CurrentBook.ExtractedPath,
            ReaderTitle,
            new ReaderStatisticsPosition(CurrentCharacterCount),
            ct);
    }

    public void StartStatisticsTracking(DateTimeOffset? now = null)
    {
        _ = now;
        _statisticsSession.Start(new ReaderStatisticsPosition(CurrentCharacterCount));
    }

    public void StartStatisticsForAutostart(StatisticsAutostartMode trigger)
    {
        var settings = _settingsService.Current.StatisticsSettings;
        if (!settings.EnableStatistics
            || IsStatisticsTracking
            || settings.AutostartMode != trigger)
        {
            return;
        }

        StartStatisticsTracking();
    }

    [RelayCommand]
    private async Task ToggleStatisticsTrackingAsync()
    {
        if (!_settingsService.Current.StatisticsSettings.EnableStatistics)
            return;

        if (IsStatisticsTracking)
            await StopStatisticsTrackingAsync();
        else
            StartStatisticsTracking();
    }

    public async Task<ReaderPageNavigationOutcome> HandleManualPageNavigationAsync(
        ReaderPageNavigationEvent readerEvent,
        CancellationToken ct = default)
    {
        if (!CanAcceptReaderPositionMutation)
            return ReaderPageNavigationOutcome.NoMovement;

        StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);

        if (ReaderStatisticsEventClassifier.IsActualPageMovement(readerEvent, Progress))
        {
            UpdateProgress(readerEvent.Progress);
            await SaveProgressAndCheckpointAsync(
                ReaderStatisticsCheckpointReason.ReadingMovement,
                ct);
            return ReaderPageNavigationOutcome.SameChapterMovement;
        }

        var adjacent = ReaderStatisticsEventClassifier.AdjacentChapterTarget(
            readerEvent,
            CurrentChapterIndex,
            ChapterCount);
        if (!adjacent.HasValue)
            return ReaderPageNavigationOutcome.NoMovement;

        UpdateProgress(readerEvent.Progress);
        await SaveProgressAndCheckpointAsync(
            ReaderStatisticsCheckpointReason.AdjacentChapter,
            ct);
        return ReaderPageNavigationOutcome.AdjacentChapter(
            adjacent.Value,
            readerEvent.Direction);
    }

    public Task StopStatisticsTrackingAsync(
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        _ = now;
        return EnqueueStatisticsMutationAsync(StatisticsMutation.Stop, ct);
    }

    public async Task FlushStatisticsAsync(
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        _ = now;
        await FlushStatisticsAtPositionAsync(CurrentCharacterCount, ct);
    }

    public Task CheckpointReadingAsync(
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct = default) =>
        CheckpointReadingAtPositionAsync(reason, CurrentCharacterCount, ct);

    public Task CheckpointProgrammaticDepartureAsync(CancellationToken ct = default)
    {
        Task operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            if (!CanAcceptReaderPositionMutation
                || !CanAdmitProgressWriter())
                return Task.CompletedTask;

            var position = CurrentCharacterCount;
            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunCheckpointWriterAsync(
                admission.Task,
                _writerTail,
                position,
                ReaderStatisticsCheckpointReason.ProgrammaticDeparture,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    private Task FlushStatisticsAtPositionAsync(
        int characterCount,
        CancellationToken ct) =>
        CheckpointReadingAtPositionAsync(
            ReaderStatisticsCheckpointReason.ReadingMovement,
            characterCount,
            ct);

    private Task CheckpointReadingAtPositionAsync(
        ReaderStatisticsCheckpointReason reason,
        int characterCount,
        CancellationToken ct) =>
        _statisticsSession.CheckpointAsync(
            new ReaderStatisticsPosition(characterCount),
            reason,
            ct);

    public Task PauseStatisticsAsync(CancellationToken ct = default) =>
        EnqueueStatisticsMutationAsync(StatisticsMutation.Pause, ct);

    public Task TickStatisticsAsync(CancellationToken ct = default) =>
        EnqueueStatisticsMutationAsync(StatisticsMutation.Tick, ct);

    public Task ResetStatisticsBaselineAsync(CancellationToken ct = default) =>
        EnqueueStatisticsMutationAsync(StatisticsMutation.ResetBaseline, ct);

    private Task EnqueueStatisticsMutationAsync(
        StatisticsMutation mutation,
        CancellationToken ct)
    {
        Task operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            if ((mutation == StatisticsMutation.Tick
                    && !CanAcceptReaderPositionMutation)
                || !CanAdmitProgressWriter())
                return Task.CompletedTask;

            if (mutation != StatisticsMutation.Tick
                && _navigationTransactions.BlocksPositionMutation)
            {
                var navigationSettlement = _navigationTransactions.WaitForSettlementAsync();
                admission = CreateWriterAdmission();
                operation = RunDeferredNavigationStatisticsMutationAsync(
                    admission.Task,
                    navigationSettlement,
                    mutation,
                    ct);
                _deferredNavigationStatisticsMutations.Add(operation);
                _ = RemoveDeferredNavigationStatisticsMutationAsync(operation);
                admission.TrySetResult();
                return operation;
            }

            var position = CurrentCharacterCount;
            admission = CreateWriterAdmission();
            operation = RunStatisticsMutationWriterAsync(
                admission.Task,
                _writerTail,
                position,
                mutation,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    private async Task RunDeferredNavigationStatisticsMutationAsync(
        Task admission,
        Task<ReaderNavigationSettlement?> navigationSettlement,
        StatisticsMutation mutation,
        CancellationToken ct)
    {
        await admission;
        var settlement = await navigationSettlement;
        ct.ThrowIfCancellationRequested();

        Task operation;
        TaskCompletionSource writerAdmission;
        lock (_saveGate)
        {
            var position = settlement?.Position.CharacterCount
                ?? CurrentCharacterCount;
            writerAdmission = CreateWriterAdmission();
            operation = RunStatisticsMutationWriterAsync(
                writerAdmission.Task,
                _writerTail,
                position,
                mutation,
                ct);
            _writerTail = operation;
        }

        writerAdmission.TrySetResult();
        await operation;
    }

    private async Task RemoveDeferredNavigationStatisticsMutationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_saveGate)
                _deferredNavigationStatisticsMutations.Remove(operation);
        }
    }

    private async Task RunStatisticsMutationWriterAsync(
        Task admission,
        Task previousWriter,
        int position,
        StatisticsMutation mutation,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        var readerPosition = new ReaderStatisticsPosition(position);
        switch (mutation)
        {
            case StatisticsMutation.Tick:
                _statisticsSession.Tick(readerPosition);
                break;
            case StatisticsMutation.Pause:
                await _statisticsSession.PauseAsync(readerPosition, ct);
                break;
            case StatisticsMutation.Stop:
                await _statisticsSession.StopAsync(readerPosition, ct);
                break;
            case StatisticsMutation.ResetBaseline:
                _statisticsSession.ResetBaseline(readerPosition);
                break;
        }
    }

    public Task PrepareForReaderLifecycleCloseAsync(CancellationToken ct = default)
    {
        Task operation;
        lock (_lifecycleCloseGate)
        {
            if (_lifecycleCloseTask == null)
            {
                operation = PrepareForReaderLifecycleCloseCoreAsync();
                _lifecycleCloseTask = operation;
                _ = ClearFailedLifecycleCloseTaskAsync(operation);
            }
            else
            {
                operation = _lifecycleCloseTask;
            }
        }

        return WaitForLifecycleCloseAsync(operation, ct);
    }

    private async Task WaitForLifecycleCloseAsync(Task operation, CancellationToken ct)
    {
        try
        {
            await operation.WaitAsync(ct);
        }
        catch
        {
            if (operation.IsCompleted && !operation.IsCompletedSuccessfully)
                ClearFailedLifecycleCloseTask(operation);
            throw;
        }
    }

    private async Task ClearFailedLifecycleCloseTaskAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            ClearFailedLifecycleCloseTask(operation);
        }
    }

    private void ClearFailedLifecycleCloseTask(Task operation)
    {
        lock (_lifecycleCloseGate)
        {
            if (ReferenceEquals(_lifecycleCloseTask, operation))
                _lifecycleCloseTask = null;
        }
    }

    private async Task PrepareForReaderLifecycleCloseCoreAsync()
    {
        await _lifecycleCheckpointLock.WaitAsync();
        try
        {
            if (_didCompleteLifecycleClose)
                return;

            Task<ReaderNavigationSettlement?> navigationSettlement;
            Task deferredStatistics;
            lock (_saveGate)
            {
                navigationSettlement = _navigationTransactions.HandleBridgeErrorAsync();
                _readerWriterClosed = true;
                _lifecycleWriterBarrier = true;
                _saveCts?.Cancel();
                deferredStatistics = Task.WhenAll(
                    _deferredNavigationStatisticsMutations.ToArray());
            }

            await navigationSettlement;
            await deferredStatistics;

            Task boundary;
            TaskCompletionSource admission;
            lock (_saveGate)
            {
                admission = CreateWriterAdmission();
                boundary = RunLifecycleWriterBoundaryAsync(
                    admission.Task,
                    _writerTail,
                    ReaderStatisticsCheckpointReason.Close,
                    cancelAfterFlush: true,
                    CancellationToken.None);
                _writerTail = boundary;
            }

            admission.TrySetResult();
            await boundary;
            _didCompleteLifecycleClose = true;
        }
        finally
        {
            _lifecycleCheckpointLock.Release();
        }
    }

    public async Task CheckpointAppBackgroundingAsync(CancellationToken ct = default)
    {
        await _lifecycleCheckpointLock.WaitAsync(ct);
        try
        {
            if (_didCompleteLifecycleClose)
                return;

            Task boundary;
            TaskCompletionSource admission;
            Task<ReaderNavigationSettlement?> navigationSettlement;
            Task deferredStatistics;
            lock (_saveGate)
            {
                if (_readerWriterClosed)
                    return;

                navigationSettlement = _navigationTransactions.HandleBridgeErrorAsync();
                _lifecycleWriterBarrier = true;
                _saveCts?.Cancel();
                deferredStatistics = Task.WhenAll(
                    _deferredNavigationStatisticsMutations.ToArray());
            }

            await navigationSettlement;
            await deferredStatistics;

            lock (_saveGate)
            {
                admission = CreateWriterAdmission();
                boundary = RunLifecycleWriterBoundaryAsync(
                    admission.Task,
                    _writerTail,
                    ReaderStatisticsCheckpointReason.Background,
                    cancelAfterFlush: false,
                    ct);
                _writerTail = boundary;
            }

            admission.TrySetResult();
            try
            {
                await boundary;
            }
            finally
            {
                lock (_saveGate)
                {
                    if (!_readerWriterClosed)
                        _lifecycleWriterBarrier = false;
                }
            }
        }
        finally
        {
            _lifecycleCheckpointLock.Release();
        }
    }

    public async Task SaveBookInfoSidecarAsync(
        IReadOnlyList<EpubChapter> chapters,
        string? containerDirectory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
            return;

        var bookInfo = _novelBookSidecarService.CreateBookInfo(
            chapters,
            _chapterCharacterCounts,
            containerDirectory);
        await _novelBookSidecarService.SaveBookInfoAsync(CurrentBook.ExtractedPath, bookInfo, ct);
    }

    public string? GetCurrentChapterHighlightsJson()
        => GetChapterHighlightsJson(CurrentChapterIndex);

    public string? GetChapterHighlightsJson(int chapterIndex)
    {
        var chapterHighlights = _readerHighlightService.GetChapterHighlights(
            Highlights,
            _chapterCharacterCounts,
            chapterIndex);
        return _readerHighlightService.SerializeForWebView(chapterHighlights);
    }

    public ReaderHighlightJumpTarget? ResolveHighlightJumpTarget(ReaderHighlight highlight) =>
        _readerHighlightService.ResolveJumpTarget(highlight, _chapterCharacterCounts);

    public IReadOnlyList<ReaderHighlightListItem> GetHighlightListItems(
        IReadOnlyList<string> chapterLabels)
    {
        ArgumentNullException.ThrowIfNull(chapterLabels);

        return Highlights
            .Select(highlight =>
            {
                var target = ResolveHighlightJumpTarget(highlight);
                if (target == null)
                    return null;

                var chapterLabel = target.ChapterIndex >= 0 && target.ChapterIndex < chapterLabels.Count
                    ? chapterLabels[target.ChapterIndex]
                    : $"Chapter {target.ChapterIndex + 1}";
                return new ReaderHighlightListItem(highlight, target, chapterLabel);
            })
            .OfType<ReaderHighlightListItem>()
            .OrderBy(item => item.Highlight.Character)
            .ThenBy(item => item.Highlight.CreatedAt)
            .ToList();
    }

    public async Task<bool> DeleteHighlightAsync(Guid id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
            return false;

        var remaining = Highlights
            .Where(highlight => highlight.Id != id)
            .ToList();
        if (remaining.Count == Highlights.Count)
            return false;

        await _readerHighlightService.SaveAsync(CurrentBook.ExtractedPath, remaining, ct);
        Highlights = remaining;
        return true;
    }

    private void UpdateCharacterProgress()
    {
        if (_chapterCharacterCounts.Count == 0)
        {
            CurrentCharacterCount = 0;
            return;
        }

        var safeIndex = Math.Clamp(CurrentChapterIndex, 0, _chapterCharacterCounts.Count - 1);
        var priorCount = 0;
        for (var i = 0; i < safeIndex; i++)
            priorCount += _chapterCharacterCounts[i];

        var chapterOffset = (int)(
            _chapterCharacterCounts[safeIndex] * Math.Clamp(Progress, 0, 1));
        CurrentCharacterCount = Math.Clamp(
            priorCount + chapterOffset,
            0,
            TotalCharacterCount);
    }

    public void SaveProgressDebounced()
    {
        lock (_saveGate)
        {
            var request = CaptureProgressSaveRequest();
            if (!CanAcceptReaderPositionMutation
                || !CanAdmitProgressWriter()
                || request == null)
                return;

            _saveCts?.Cancel();
            var owner = new CancellationTokenSource();
            _saveCts = owner;
            _saveTask = SaveProgressDebouncedCoreAsync(
                _writerTail,
                request,
                owner);
            _writerTail = _saveTask;
        }
    }

    private async Task SaveProgressDebouncedCoreAsync(
        Task previousWriter,
        ProgressSaveRequest request,
        CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            await Task.Delay(500, token);
            await AwaitPreviousWriterCompletionAsync(previousWriter);
            token.ThrowIfCancellationRequested();
            await SaveProgressCoreAsync(
                request,
                flushStatistics: true,
                scheduleAutoSync: true,
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_saveGate)
            {
                if (ReferenceEquals(_saveCts, owner))
                {
                    _saveCts = null;
                    _saveTask = null;
                }
            }

            owner.Dispose();
        }
    }

    private async Task<ReaderNavigationResolutionResult> RunNavigationCommitWriterAsync(
        Task admission,
        Task previousWriter,
        ReaderNavigationCommitLease lease,
        ProgressSaveRequest request)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);

        bool persisted;
        try
        {
            persisted = await SaveProgressBookmarkAsync(request, CancellationToken.None);
        }
        catch (Exception exception)
        {
            ReportNavigationFailure(exception, lease.Generation, "persist bookmark");
            return CompleteNavigationResolution(lease, committed: false);
        }

        if (!persisted)
            return CompleteNavigationResolution(lease, committed: false);

        RunNavigationPostSaveEffect(
            lease.Generation,
            "reset statistics baseline",
            () => _statisticsSession.ResetBaseline(
                new ReaderStatisticsPosition(lease.ResolvedDestination.CharacterCount)));
        ApplyResolvedChapterPosition(request, lease.Generation);
        RunNavigationPostSaveEffect(
            lease.Generation,
            "schedule export",
            () => _readerAutoSyncCoordinator.ScheduleExport(request.Book));
        RunNavigationPostSaveEffect(
            lease.Generation,
            "broadcast library change",
            () => _messenger.Send(new NovelLibraryChangedMessage()));
        return CompleteNavigationResolution(lease, committed: true);
    }

    private ReaderNavigationResolutionResult CompleteNavigationResolution(
        ReaderNavigationCommitLease lease,
        bool committed)
    {
        var settlement = _navigationTransactions.CompleteCommit(lease, committed)
            ?? throw new InvalidOperationException(
                $"Navigation generation {lease.Generation} lost its issued commit lease.");
        return ReaderNavigationResolutionResult.FromSettlement(settlement);
    }

    private void RunNavigationPostSaveEffect(
        long generation,
        string effect,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportNavigationFailure(exception, generation, effect);
        }
    }

    private void ReportNavigationFailure(
        Exception exception,
        long generation,
        string operation)
    {
        Serilog.Log.Error(
            exception,
            "[NovelReader] Navigation generation {Generation} failed to {Operation}",
            generation,
            operation);
        try
        {
            _notificationService.ShowError(exception.Message, "Reader navigation");
        }
        catch (Exception notificationException)
        {
            Serilog.Log.Error(
                notificationException,
                "[NovelReader] Failed to report navigation generation {Generation} error",
                generation);
        }
    }

    public Task SaveProgressNowAsync(
        bool flushStatistics = true,
        bool scheduleAutoSync = true,
        CancellationToken ct = default)
    {
        Task operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            var request = CaptureProgressSaveRequest();
            if (!CanAcceptReaderPositionMutation
                || !CanAdmitProgressWriter()
                || request == null)
                return Task.CompletedTask;

            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunProgressWriterAsync(
                admission.Task,
                _writerTail,
                request,
                flushStatistics,
                scheduleAutoSync,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    private async Task RunProgressWriterAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest request,
        bool flushStatistics,
        bool scheduleAutoSync,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        await SaveProgressCoreAsync(
            request,
            flushStatistics,
            scheduleAutoSync,
            ct);
    }

    private Task SaveProgressAndCheckpointAsync(
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct)
    {
        Task operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            var request = CaptureProgressSaveRequest();
            if (!CanAdmitProgressWriter() || request == null)
                return Task.CompletedTask;

            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunProgressWriterWithCheckpointAsync(
                admission.Task,
                _writerTail,
                request,
                reason,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    private async Task RunProgressWriterWithCheckpointAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest request,
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        var saved = await SaveProgressCoreAsync(
            request,
            flushStatistics: false,
            scheduleAutoSync: true,
            ct);
        if (!saved)
            return;
        await CheckpointReadingAtPositionAsync(
            reason,
            request.CurrentCharacterCount,
            ct);
    }

    private async Task RunCheckpointWriterAsync(
        Task admission,
        Task previousWriter,
        int position,
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        await CheckpointReadingAtPositionAsync(reason, position, ct);
    }

    public Task<bool> SaveProgressAndResetStatisticsBaselineAsync(
        CancellationToken ct = default)
    {
        Task<bool> operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            var request = CaptureProgressSaveRequest();
            if (!CanAcceptReaderPositionMutation
                || !CanAdmitProgressWriter()
                || request == null)
                return Task.FromResult(false);

            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunProgressWriterWithBaselineResetAsync(
                admission.Task,
                _writerTail,
                request,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    public Task<bool> CompleteAdjacentChapterNavigationAsync(
        int chapterIndex,
        double resolvedProgress,
        CancellationToken ct = default)
    {
        Task<bool> operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            if (!CanAcceptReaderPositionMutation
                || !CanAdmitProgressWriter()
                || CurrentBook == null
                || chapterIndex < 0
                || chapterIndex >= ChapterCount
                || !double.IsFinite(resolvedProgress))
            {
                return Task.FromResult(false);
            }

            var progress = Math.Clamp(resolvedProgress, 0, 1);
            var request = new ProgressSaveRequest(
                CurrentBook,
                chapterIndex,
                progress,
                CharacterCountAt(chapterIndex, progress),
                TotalCharacterCount,
                ++_positionRevision);
            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunAdjacentChapterCompletionWriterAsync(
                admission.Task,
                _writerTail,
                request,
                ct);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    private async Task<bool> RunAdjacentChapterCompletionWriterAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest request,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        lock (_saveGate)
        {
            if (request.PositionRevision < _currentPositionRevision)
                return false;
        }

        var saved = await SaveProgressCoreAsync(
            request,
            flushStatistics: false,
            scheduleAutoSync: true,
            ct);
        if (!saved)
            return false;

        _statisticsSession.ResetBaseline(
            new ReaderStatisticsPosition(request.CurrentCharacterCount));
        ApplyResolvedChapterPosition(request);
        return true;
    }

    private void ApplyResolvedChapterPosition(
        ProgressSaveRequest request,
        long? navigationGeneration = null)
    {
        var propertyNames = new[]
        {
            nameof(Progress),
            nameof(CurrentChapterIndex),
            nameof(CurrentCharacterCount),
        };
        foreach (var propertyName in propertyNames)
        {
            if (navigationGeneration is { } generation)
            {
                RunNavigationPostSaveEffect(
                    generation,
                    $"notify {propertyName} changing",
                    () => base.OnPropertyChanging(new PropertyChangingEventArgs(propertyName)));
            }
            else
            {
                base.OnPropertyChanging(new PropertyChangingEventArgs(propertyName));
            }
        }

        _suppressPositionNotifications = true;
        try
        {
            _positionRevision = Math.Max(_positionRevision, request.PositionRevision);
            _currentPositionRevision = request.PositionRevision;
            Progress = request.Progress;
            CurrentChapterIndex = request.ChapterIndex;
            CurrentCharacterCount = request.CurrentCharacterCount;
        }
        finally
        {
            _suppressPositionNotifications = false;
        }

        foreach (var propertyName in propertyNames)
        {
            if (navigationGeneration is { } generation)
            {
                RunNavigationPostSaveEffect(
                    generation,
                    $"notify {propertyName} changed",
                    () => base.OnPropertyChanged(new PropertyChangedEventArgs(propertyName)));
            }
            else
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
            }
        }

        void NotifyDerivedPositionProperties()
        {
            OnPropertyChanged(nameof(ChapterTitle));
            OnPropertyChanged(nameof(OverallProgress));
            OnPropertyChanged(nameof(OverallProgressText));
            OnPropertyChanged(nameof(ReaderProgressText));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanGoPrevious));
            OnStatisticsTextChanged();
        }

        if (navigationGeneration is { } derivedGeneration)
        {
            RunNavigationPostSaveEffect(
                derivedGeneration,
                "notify derived position properties",
                NotifyDerivedPositionProperties);
        }
        else
        {
            NotifyDerivedPositionProperties();
        }
    }

    public ReaderNavigationRenderRequest? TryBeginNavigation(
        int destinationChapterIndex,
        ReaderChapterRestoreTarget? restoreTarget,
        double? exactProgress)
    {
        lock (_saveGate)
            return TryBeginNavigationLocked(
                destinationChapterIndex,
                restoreTarget,
                exactProgress);
    }

    public ReaderProgrammaticNavigationReservation? TryReserveProgrammaticNavigation(
        int destinationChapterIndex,
        ReaderChapterRestoreTarget? restoreTarget,
        double? exactProgress,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ReaderNavigationRenderRequest? renderRequest;
        Task departureCheckpoint;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            if (!CanAdmitProgressWriter())
                return null;

            renderRequest = TryBeginNavigationLocked(
                destinationChapterIndex,
                restoreTarget,
                exactProgress);
            if (renderRequest == null)
                return null;

            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            departureCheckpoint = RunCheckpointWriterAsync(
                admission.Task,
                _writerTail,
                renderRequest.Source.CharacterCount,
                ReaderStatisticsCheckpointReason.ProgrammaticDeparture,
                CancellationToken.None);
            _writerTail = departureCheckpoint;
        }

        admission.TrySetResult();
        return new ReaderProgrammaticNavigationReservation(
            renderRequest,
            departureCheckpoint);
    }

    private ReaderNavigationRenderRequest? TryBeginNavigationLocked(
        int destinationChapterIndex,
        ReaderChapterRestoreTarget? restoreTarget,
        double? exactProgress)
    {
        if (!CanAcceptReaderPositionMutation
            || CurrentBook == null
            || destinationChapterIndex < 0
            || destinationChapterIndex >= ChapterCount
            || exactProgress is { } progress
                && (!double.IsFinite(progress) || progress is < 0 or > 1))
        {
            return null;
        }

        var source = new ReaderNavigationPositionSnapshot(
            CurrentBook.Id,
            CurrentChapterIndex,
            Progress,
            CurrentCharacterCount,
            TotalCharacterCount,
            _currentPositionRevision);
        var destination = new ReaderNavigationDestination(
            destinationChapterIndex,
            restoreTarget,
            exactProgress);
        return _navigationTransactions.TryBegin(source, destination);
    }

    public Task<ReaderNavigationResolutionResult> ResolveNavigationAsync(
        long generation,
        int destinationChapterIndex,
        double resolvedProgress,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Task<ReaderNavigationResolutionResult> operation;
        TaskCompletionSource admission;
        lock (_saveGate)
        {
            var render = _navigationTransactions.ActiveRenderRequest;
            if (!CanAdmitProgressWriter()
                || render == null
                || render.Generation != generation
                || render.Destination.ChapterIndex != destinationChapterIndex
                || CurrentBook == null
                || CurrentBook.Id != render.Source.BookId
                || destinationChapterIndex < 0
                || destinationChapterIndex >= ChapterCount
                || !double.IsFinite(resolvedProgress)
                || resolvedProgress is < 0 or > 1)
            {
                return Task.FromResult(ReaderNavigationResolutionResult.Ignored);
            }

            var candidateRevision = _positionRevision + 1;
            var destination = new ReaderNavigationPositionSnapshot(
                render.Source.BookId,
                destinationChapterIndex,
                resolvedProgress,
                CharacterCountAt(destinationChapterIndex, resolvedProgress),
                TotalCharacterCount,
                candidateRevision);
            var lease = _navigationTransactions.TryBeginCommit(generation, destination);
            if (lease == null)
                return Task.FromResult(ReaderNavigationResolutionResult.Ignored);
            _positionRevision = candidateRevision;

            var request = new ProgressSaveRequest(
                CurrentBook,
                lease.ResolvedDestination.ChapterIndex,
                lease.ResolvedDestination.Progress,
                lease.ResolvedDestination.CharacterCount,
                lease.ResolvedDestination.TotalCharacterCount,
                lease.ResolvedDestination.Revision);
            _saveCts?.Cancel();
            admission = CreateWriterAdmission();
            operation = RunNavigationCommitWriterAsync(
                admission.Task,
                _writerTail,
                lease,
                request);
            _writerTail = operation;
        }

        admission.TrySetResult();
        return operation;
    }

    public Task<ReaderNavigationSettlement?> HandleNavigationBridgeErrorAsync() =>
        _navigationTransactions.HandleBridgeErrorAsync();

    public Task<ReaderNavigationSettlement?> SettleNavigationForLifecycleAsync() =>
        _navigationTransactions.HandleBridgeErrorAsync();

    public bool AcknowledgeNavigationRendered(long generation) =>
        _navigationTransactions.AcknowledgeTerminalRender(generation);

    protected override void OnPropertyChanging(PropertyChangingEventArgs e)
    {
        if (!_suppressPositionNotifications)
            base.OnPropertyChanging(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressPositionNotifications)
            base.OnPropertyChanged(e);
    }

    private async Task<bool> RunProgressWriterWithBaselineResetAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest request,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        var saved = await SaveProgressCoreAsync(
            request,
            flushStatistics: false,
            scheduleAutoSync: true,
            ct);
        if (!saved)
            return false;
        _statisticsSession.ResetBaseline(
            new ReaderStatisticsPosition(request.CurrentCharacterCount));
        return true;
    }

    private async Task RunLifecycleWriterBoundaryAsync(
        Task admission,
        Task previousWriter,
        ReaderStatisticsCheckpointReason reason,
        bool cancelAfterFlush,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        ProgressSaveRequest? request;
        int statisticsCharacterCount;
        lock (_saveGate)
        {
            request = CaptureProgressSaveRequest();
            statisticsCharacterCount = request?.CurrentCharacterCount
                ?? CurrentCharacterCount;
        }
        var saved = request == null;
        if (request != null)
        {
            saved = await SaveProgressCoreAsync(
                request,
                flushStatistics: false,
                scheduleAutoSync: false,
                ct);
        }

        if (!saved)
            throw new InvalidOperationException("Reader bookmark could not be saved at the lifecycle boundary.");

        await CheckpointReadingAtPositionAsync(reason, statisticsCharacterCount, ct);
        if (request != null)
        {
            _readerAutoSyncCoordinator.ScheduleExport(request.Book);
            await _readerAutoSyncCoordinator.FlushAsync(request.Book, ct);
        }

        if (cancelAfterFlush)
            _readerAutoSyncCoordinator.Cancel();
    }

    private async Task<bool> SaveProgressCoreAsync(
        ProgressSaveRequest request,
        bool flushStatistics,
        bool scheduleAutoSync,
        CancellationToken ct)
    {
        if (!await SaveProgressBookmarkAsync(request, ct))
            return false;

        if (flushStatistics)
            await FlushStatisticsAtPositionAsync(request.CurrentCharacterCount, ct);
        ct.ThrowIfCancellationRequested();
        if (scheduleAutoSync)
            _readerAutoSyncCoordinator.ScheduleExport(request.Book);
        _messenger.Send(new NovelLibraryChangedMessage());
        return true;
    }

    private async Task<bool> SaveProgressBookmarkAsync(
        ProgressSaveRequest request,
        CancellationToken ct)
    {
        var result = await _novelLibraryService.SaveProgressAsync(
            request.Book.Id,
            request.ChapterIndex,
            request.Progress,
            request.CurrentCharacterCount,
            request.TotalCharacterCount,
            ct);
        ct.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return false;
        }
        return true;
    }

    private static async Task AwaitPreviousWriterCompletionAsync(Task previousWriter)
    {
        try
        {
            await previousWriter;
        }
        catch
        {
        }
    }

    private static TaskCompletionSource CreateWriterAdmission() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool CanAdmitProgressWriter() =>
        !_lifecycleWriterBarrier && !_readerWriterClosed;

    private ProgressSaveRequest? CaptureProgressSaveRequest() =>
        CurrentBook == null
            ? null
            : new ProgressSaveRequest(
                CurrentBook,
                CurrentChapterIndex,
                Progress,
                CurrentCharacterCount,
                TotalCharacterCount,
                _currentPositionRevision);

    private int CharacterCountAt(int chapterIndex, double progress)
    {
        if (_chapterCharacterCounts.Count == 0)
            return 0;

        var safeIndex = Math.Clamp(chapterIndex, 0, _chapterCharacterCounts.Count - 1);
        var priorCount = 0;
        for (var i = 0; i < safeIndex; i++)
            priorCount += _chapterCharacterCounts[i];

        var chapterOffset = (int)(
            _chapterCharacterCounts[safeIndex] * Math.Clamp(progress, 0, 1));
        return Math.Clamp(priorCount + chapterOffset, 0, TotalCharacterCount);
    }

    private sealed record ProgressSaveRequest(
        NovelBook Book,
        int ChapterIndex,
        double Progress,
        int CurrentCharacterCount,
        int TotalCharacterCount,
        long PositionRevision);

    private enum StatisticsMutation
    {
        Tick,
        Pause,
        Stop,
        ResetBaseline,
    }

    private int CurrentChapterRemainingCharacters
    {
        get
        {
            if (_chapterCharacterCounts.Count == 0)
                return 0;

            var safeIndex = Math.Clamp(CurrentChapterIndex, 0, _chapterCharacterCounts.Count - 1);
            var chapterStart = 0;
            for (var i = 0; i < safeIndex; i++)
                chapterStart += Math.Max(0, _chapterCharacterCounts[i]);

            var chapterCount = Math.Max(0, _chapterCharacterCounts[safeIndex]);
            var readInChapter = Math.Clamp(CurrentCharacterCount - chapterStart, 0, chapterCount);
            return Math.Max(0, chapterCount - readInChapter);
        }
    }

    private double EstimateRemainingSeconds(int remainingCharacters) =>
        SessionStatistics.LastReadingSpeed <= 0
            ? 0
            : Math.Max(0, remainingCharacters) / (SessionStatistics.LastReadingSpeed / 3600d);

    private void OnStatisticsSessionStateChanged(
        object? sender,
        ReaderStatisticsSessionState state) =>
        ApplyStatisticsState(state);

    private void ApplyStatisticsState(ReaderStatisticsSessionState state)
    {
        IsStatisticsTracking = state.IsTracking;
        IsStatisticsPaused = state.IsPaused;
        SessionStatistics = state.Session;
        TodaysStatistics = state.Today;
        AllTimeStatistics = state.AllTime;
        OnStatisticsTextChanged();
    }

    private static NovelReadingStatistic InitialStatistic() =>
        ReaderStatisticsMath.Empty(
            "Novel reader",
            DateOnly.FromDateTime(DateTime.Now));

    private static string FormatCount(int value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    private int DisplayStatisticsUnits(int rawCharacters) =>
        _profileRuntime.ActiveLanguage.DisplayUnitsFromRawCharacters(rawCharacters);

    private string FormatStatisticsCount(int rawCharacters) =>
        FormatCount(DisplayStatisticsUnits(rawCharacters));

    private string FormatStatisticsSpeed(int rawCharactersPerHour) =>
        $"{FormatCount(DisplayStatisticsUnits(rawCharactersPerHour))} / h";

    private static string FormatDuration(double seconds)
    {
        var totalSeconds = Math.Max((long)seconds, 0L);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var remainingSeconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}h {minutes}m {remainingSeconds}s"
            : minutes > 0
                ? $"{minutes}m {remainingSeconds}s"
                : $"{remainingSeconds}s";
    }

    private static string FormatChromeReadingTime(double seconds)
    {
        var totalMinutes = Math.Max((long)(seconds / 60d), 0L);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return $"{hours}:{minutes.ToString("00", CultureInfo.InvariantCulture)}";
    }

    private void OnStatisticsTextChanged()
    {
        OnPropertyChanged(nameof(StatisticsTrackingButtonText));
        OnPropertyChanged(nameof(IsEnglishStatisticsContent));
        OnPropertyChanged(nameof(StatisticsSessionCharactersText));
        OnPropertyChanged(nameof(StatisticsSessionSpeedText));
        OnPropertyChanged(nameof(StatisticsSessionTimeText));
        OnPropertyChanged(nameof(StatisticsSessionChromeTimeText));
        OnPropertyChanged(nameof(StatisticsBookRemainingTimeText));
        OnPropertyChanged(nameof(StatisticsChapterRemainingTimeText));
        OnPropertyChanged(nameof(StatisticsTodayCharactersText));
        OnPropertyChanged(nameof(StatisticsTodaySpeedText));
        OnPropertyChanged(nameof(StatisticsTodayTimeText));
        OnPropertyChanged(nameof(StatisticsAllTimeCharactersText));
        OnPropertyChanged(nameof(StatisticsAllTimeSpeedText));
        OnPropertyChanged(nameof(StatisticsAllTimeTimeText));
    }

    [RelayCommand]
    private void BackToLibrary() =>
        _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
}
