using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Models.Profiles;
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Profiles;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;

namespace Hoshi.ViewModels.Pages;

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
    private bool _lifecycleWriterBarrier;
    private bool _readerWriterClosed;
    private IReadOnlyList<int> _chapterCharacterCounts = [];
    private readonly object _lifecycleCloseGate = new();
    private readonly SemaphoreSlim _lifecycleCheckpointLock = new(1, 1);
    private Task? _lifecycleCloseTask;
    private bool _didCompleteLifecycleClose;

    public NovelReaderPageViewModel(
        INovelLibraryService novelLibraryService,
        INotificationService notificationService,
        IMessenger messenger,
        IReaderHighlightService readerHighlightService,
        INovelBookSidecarService novelBookSidecarService,
        IReaderStatisticsSession statisticsSession,
        IProfileRuntimeService profileRuntime,
        ISettingsService settingsService,
        IReaderAutoSyncCoordinator readerAutoSyncCoordinator
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
            if (await _readerAutoSyncCoordinator.ImportOnOpenAsync(CurrentBook, ct))
            {
                var imported = await _novelLibraryService.GetNovelBookAsync(CurrentBook.Id, ct);
                if (imported.IsSuccess && imported.Value != null)
                    CurrentBook = imported.Value;
            }

            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id, ct);
        }

        OnPropertyChanged(nameof(ReaderTitle));
    }

    public void SetChapter(int index, int count)
    {
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
        Progress = Math.Clamp(progress, 0, 1);
        UpdateCharacterProgress();
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(OverallProgressText));
        OnPropertyChanged(nameof(ReaderProgressText));
        OnStatisticsTextChanged();
    }

    public void SetChapterCharacterCounts(IReadOnlyList<int> chapterCharacterCounts)
    {
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
        return ReaderPageNavigationOutcome.AdjacentChapter(adjacent.Value);
    }

    public async Task StopStatisticsTrackingAsync(
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        _ = now;
        await _statisticsSession.StopAsync(
            new ReaderStatisticsPosition(CurrentCharacterCount),
            ct);
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
        _statisticsSession.PauseAsync(
            new ReaderStatisticsPosition(CurrentCharacterCount),
            ct);

    public void TickStatistics() =>
        _statisticsSession.Tick(new ReaderStatisticsPosition(CurrentCharacterCount));

    public void ResetStatisticsBaseline() =>
        _statisticsSession.ResetBaseline(
            new ReaderStatisticsPosition(CurrentCharacterCount));

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
        CloseProgressWriterAdmission();
        await _lifecycleCheckpointLock.WaitAsync();
        try
        {
            if (_didCompleteLifecycleClose)
                return;

            Task boundary;
            TaskCompletionSource admission;
            lock (_saveGate)
            {
                var request = CaptureProgressSaveRequest();
                var statisticsCharacterCount = request?.CurrentCharacterCount
                    ?? CurrentCharacterCount;
                admission = CreateWriterAdmission();
                boundary = RunLifecycleWriterBoundaryAsync(
                    admission.Task,
                    _writerTail,
                    request,
                    statisticsCharacterCount,
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
            lock (_saveGate)
            {
                if (_readerWriterClosed)
                    return;

                _lifecycleWriterBarrier = true;
                _saveCts?.Cancel();
                var request = CaptureProgressSaveRequest();
                var statisticsCharacterCount = request?.CurrentCharacterCount
                    ?? CurrentCharacterCount;
                admission = CreateWriterAdmission();
                boundary = RunLifecycleWriterBoundaryAsync(
                    admission.Task,
                    _writerTail,
                    request,
                    statisticsCharacterCount,
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
    {
        var chapterHighlights = _readerHighlightService.GetChapterHighlights(
            Highlights,
            _chapterCharacterCounts,
            CurrentChapterIndex);
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
            if (!CanAdmitProgressWriter() || request == null)
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
            if (!CanAdmitProgressWriter() || request == null)
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
        await SaveProgressCoreAsync(
            request,
            flushStatistics: false,
            scheduleAutoSync: true,
            ct);
        await CheckpointReadingAtPositionAsync(
            reason,
            request.CurrentCharacterCount,
            ct);
    }

    public Task SaveProgressAndResetStatisticsBaselineAsync(
        CancellationToken ct = default)
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

    private async Task RunProgressWriterWithBaselineResetAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest request,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        await SaveProgressCoreAsync(
            request,
            flushStatistics: false,
            scheduleAutoSync: true,
            ct);
        _statisticsSession.ResetBaseline(
            new ReaderStatisticsPosition(request.CurrentCharacterCount));
    }

    private async Task RunLifecycleWriterBoundaryAsync(
        Task admission,
        Task previousWriter,
        ProgressSaveRequest? request,
        int statisticsCharacterCount,
        ReaderStatisticsCheckpointReason reason,
        bool cancelAfterFlush,
        CancellationToken ct)
    {
        await admission;
        await AwaitPreviousWriterCompletionAsync(previousWriter);
        ct.ThrowIfCancellationRequested();
        if (request != null)
        {
            await SaveProgressCoreAsync(
                request,
                flushStatistics: false,
                scheduleAutoSync: false,
                ct);
        }

        await CheckpointReadingAtPositionAsync(reason, statisticsCharacterCount, ct);
        if (request != null)
        {
            _readerAutoSyncCoordinator.ScheduleExport(request.Book);
            await _readerAutoSyncCoordinator.FlushAsync(request.Book, ct);
        }

        if (cancelAfterFlush)
            _readerAutoSyncCoordinator.Cancel();
    }

    private async Task SaveProgressCoreAsync(
        ProgressSaveRequest request,
        bool flushStatistics,
        bool scheduleAutoSync,
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
        if (flushStatistics)
            await FlushStatisticsAtPositionAsync(request.CurrentCharacterCount, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        if (scheduleAutoSync)
            _readerAutoSyncCoordinator.ScheduleExport(request.Book);
        _messenger.Send(new NovelLibraryChangedMessage());
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

    private void CloseProgressWriterAdmission()
    {
        lock (_saveGate)
        {
            _readerWriterClosed = true;
            _lifecycleWriterBarrier = true;
            _saveCts?.Cancel();
        }
    }

    private ProgressSaveRequest? CaptureProgressSaveRequest() =>
        CurrentBook == null
            ? null
            : new ProgressSaveRequest(
                CurrentBook,
                CurrentChapterIndex,
                Progress,
                CurrentCharacterCount,
                TotalCharacterCount);

    private sealed record ProgressSaveRequest(
        NovelBook Book,
        int ChapterIndex,
        double Progress,
        int CurrentCharacterCount,
        int TotalCharacterCount);

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
    private async Task BackToLibrary()
    {
        await PrepareForReaderLifecycleCloseAsync();
        _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
    }
}
