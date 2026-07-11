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
using Hoshi.Models.Settings;
using Hoshi.Services.Novels;
using Hoshi.Services.Profiles;
using Hoshi.Services.Settings;
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
    public string StatisticsSessionCharactersText => FormatCount(SessionStatistics.CharactersRead);
    public string StatisticsSessionSpeedText => FormatSpeed(SessionStatistics.LastReadingSpeed);
    public string StatisticsSessionTimeText => FormatDuration(SessionStatistics.ReadingTime);
    public string StatisticsSessionChromeTimeText => FormatChromeReadingTime(SessionStatistics.ReadingTime);
    public string StatisticsBookRemainingTimeText => FormatDuration(
        EstimateRemainingSeconds(TotalCharacterCount - CurrentCharacterCount));
    public string StatisticsChapterRemainingTimeText => FormatDuration(
        EstimateRemainingSeconds(CurrentChapterRemainingCharacters));
    public string StatisticsTodayCharactersText => FormatCount(TodaysStatistics.CharactersRead);
    public string StatisticsTodaySpeedText => FormatSpeed(TodaysStatistics.LastReadingSpeed);
    public string StatisticsTodayTimeText => FormatDuration(TodaysStatistics.ReadingTime);
    public string StatisticsAllTimeCharactersText => FormatCount(AllTimeStatistics.CharactersRead);
    public string StatisticsAllTimeSpeedText => FormatSpeed(AllTimeStatistics.LastReadingSpeed);
    public string StatisticsAllTimeTimeText => FormatDuration(AllTimeStatistics.ReadingTime);

    private CancellationTokenSource? _saveCts;
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
        ISettingsService settingsService
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
    }

    public async Task InitializeAsync(NovelReaderNavigationArgs args)
    {
        var result = await _novelLibraryService.GetNovelBookAsync(args.BookId);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        CurrentBook = result.Value;
        OnPropertyChanged(nameof(ReaderTitle));

        if (CurrentBook != null)
        {
            await _profileRuntime.ActivateForBookAsync(CurrentBook);
            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id);
        }
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
            await SaveProgressNowAsync(flushStatistics: false, ct);
            await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.ReadingMovement, ct);
            return ReaderPageNavigationOutcome.SameChapterMovement;
        }

        var adjacent = ReaderStatisticsEventClassifier.AdjacentChapterTarget(
            readerEvent,
            CurrentChapterIndex,
            ChapterCount);
        if (!adjacent.HasValue)
            return ReaderPageNavigationOutcome.NoMovement;

        UpdateProgress(readerEvent.Progress);
        await SaveProgressNowAsync(flushStatistics: false, ct);
        await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.AdjacentChapter, ct);
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
        await CheckpointReadingAsync(
            ReaderStatisticsCheckpointReason.ReadingMovement,
            ct);
    }

    public Task CheckpointReadingAsync(
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct = default) =>
        _statisticsSession.CheckpointAsync(
            new ReaderStatisticsPosition(CurrentCharacterCount),
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
        lock (_lifecycleCloseGate)
        {
            return _lifecycleCloseTask ??= PrepareForReaderLifecycleCloseCoreAsync(ct);
        }
    }

    private async Task PrepareForReaderLifecycleCloseCoreAsync(CancellationToken ct)
    {
        await _lifecycleCheckpointLock.WaitAsync(ct);
        try
        {
            if (_didCompleteLifecycleClose)
                return;

            await SaveProgressNowAsync(flushStatistics: false, ct);
            await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.Close, ct);
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

            await SaveProgressNowAsync(flushStatistics: false, ct);
            await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.Background, ct);
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
        if (CurrentBook == null) return;

        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        var bookId = CurrentBook.Id;
        var chapterIndex = CurrentChapterIndex;
        var progress = Progress;
        var currentCharacterCount = CurrentCharacterCount;
        var totalCharacterCount = TotalCharacterCount;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                {
                    await _novelLibraryService.SaveProgressAsync(
                        bookId,
                        chapterIndex,
                        progress,
                        currentCharacterCount,
                        totalCharacterCount,
                        token);
                    await FlushStatisticsAsync(ct: token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async Task SaveProgressNowAsync(
        bool flushStatistics = true,
        CancellationToken ct = default)
    {
        if (CurrentBook == null) return;
        _saveCts?.Cancel();
        await _novelLibraryService.SaveProgressAsync(
            CurrentBook.Id,
            CurrentChapterIndex,
            Progress,
            CurrentCharacterCount,
            TotalCharacterCount,
            ct);
        if (flushStatistics)
            await FlushStatisticsAsync(ct: ct);
        _messenger.Send(new NovelLibraryChangedMessage());
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

    private static string FormatSpeed(int value) =>
        $"{FormatCount(value)} / h";

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
