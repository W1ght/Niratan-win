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
using Hoshi.Services.Novels;
using Hoshi.Services.UI;

namespace Hoshi.ViewModels.Pages;

public partial class NovelReaderPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;
    private readonly IReaderHighlightService _readerHighlightService;
    private readonly INovelBookSidecarService _novelBookSidecarService;
    private readonly INovelStatisticsSidecarService _novelStatisticsSidecarService;

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

    private IReadOnlyList<ReaderHighlight> _highlights = [];
    private IReadOnlyList<NovelReadingStatistic> _statistics = [];
    private DateTimeOffset? _lastStatisticsTimestamp;
    private int _lastStatisticsCharacterCount;

    public IReadOnlyList<ReaderHighlight> Highlights
    {
        get => _highlights;
        private set => SetProperty(ref _highlights, value);
    }

    private NovelReadingStatistic _sessionStatistics = DefaultStatistic(
        "Novel reader",
        DateTimeOffset.UtcNow);

    public NovelReadingStatistic SessionStatistics
    {
        get => _sessionStatistics;
        private set => SetProperty(ref _sessionStatistics, value);
    }

    private NovelReadingStatistic _todaysStatistics = DefaultStatistic(
        "Novel reader",
        DateTimeOffset.UtcNow);

    public NovelReadingStatistic TodaysStatistics
    {
        get => _todaysStatistics;
        private set => SetProperty(ref _todaysStatistics, value);
    }

    private NovelReadingStatistic _allTimeStatistics = DefaultStatistic(
        "Novel reader",
        DateTimeOffset.UtcNow);

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

    public NovelReaderPageViewModel(
        INovelLibraryService novelLibraryService,
        INotificationService notificationService,
        IMessenger messenger,
        IReaderHighlightService readerHighlightService,
        INovelBookSidecarService novelBookSidecarService,
        INovelStatisticsSidecarService novelStatisticsSidecarService
    )
    {
        _novelLibraryService = novelLibraryService;
        _notificationService = notificationService;
        _messenger = messenger;
        _readerHighlightService = readerHighlightService;
        _novelBookSidecarService = novelBookSidecarService;
        _novelStatisticsSidecarService = novelStatisticsSidecarService;
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
            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id);
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
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
        {
            _statistics = [];
        }
        else
        {
            _statistics = await _novelStatisticsSidecarService.LoadAsync(
                CurrentBook.ExtractedPath,
                ct);
        }

        SessionStatistics = DefaultStatistic(ReaderTitle, now);
        TodaysStatistics = _statistics.FirstOrDefault(s => s.DateKey == FormatDateKey(now))
            ?? DefaultStatistic(ReaderTitle, now);
        AllTimeStatistics = AggregateAllTime(ReaderTitle, now, _statistics);
        OnStatisticsTextChanged();
    }

    public void StartStatisticsTracking(DateTimeOffset? now = null)
    {
        IsStatisticsTracking = true;
        _lastStatisticsTimestamp = now ?? DateTimeOffset.UtcNow;
        _lastStatisticsCharacterCount = CurrentCharacterCount;
        OnPropertyChanged(nameof(StatisticsTrackingButtonText));
    }

    public async Task StopStatisticsTrackingAsync(
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        await FlushStatisticsAsync(now, ct);
        IsStatisticsTracking = false;
        _lastStatisticsTimestamp = null;
        OnPropertyChanged(nameof(StatisticsTrackingButtonText));
    }

    public async Task FlushStatisticsAsync(
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        if (!IsStatisticsTracking || _lastStatisticsTimestamp == null)
            return;

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var timeDiff = Math.Max(0, (timestamp - _lastStatisticsTimestamp.Value).TotalSeconds);
        if (timeDiff <= 0)
            return;

        var characterDiff = CurrentCharacterCount - _lastStatisticsCharacterCount;
        var finalCharacterDiff = characterDiff < 0
            && Math.Abs(characterDiff) > SessionStatistics.CharactersRead
                ? -SessionStatistics.CharactersRead
                : characterDiff;
        var lastStatisticModified = timestamp.ToUnixTimeMilliseconds();

        SessionStatistics = UpdateStatistic(
            SessionStatistics,
            timeDiff,
            finalCharacterDiff,
            lastStatisticModified);
        TodaysStatistics = UpdateStatistic(
            EnsureTodayStatistic(timestamp),
            timeDiff,
            finalCharacterDiff,
            lastStatisticModified);
        AllTimeStatistics = UpdateStatistic(
            AllTimeStatistics,
            timeDiff,
            finalCharacterDiff,
            lastStatisticModified);
        _lastStatisticsTimestamp = timestamp;
        _lastStatisticsCharacterCount = CurrentCharacterCount;

        UpsertTodayStatistic();
        if (!string.IsNullOrWhiteSpace(CurrentBook?.ExtractedPath))
            await _novelStatisticsSidecarService.SaveAsync(CurrentBook.ExtractedPath, _statistics, ct);

        OnStatisticsTextChanged();
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

        CurrentCharacterCount = Math.Clamp(
            priorCount + (int)Math.Round(_chapterCharacterCounts[safeIndex] * Math.Clamp(Progress, 0, 1)),
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
        var bookRootPath = CurrentBook.ExtractedPath;

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
                    await SaveBookmarkSidecarAsync(
                        bookRootPath,
                        chapterIndex,
                        progress,
                        currentCharacterCount,
                        token);
                    await FlushStatisticsAsync(ct: token);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async Task SaveProgressNowAsync()
    {
        if (CurrentBook == null) return;
        _saveCts?.Cancel();
        await _novelLibraryService.SaveProgressAsync(
            CurrentBook.Id,
            CurrentChapterIndex,
            Progress,
            CurrentCharacterCount,
            TotalCharacterCount);
        await SaveBookmarkSidecarAsync(
            CurrentBook.ExtractedPath,
            CurrentChapterIndex,
            Progress,
            CurrentCharacterCount,
            CancellationToken.None);
        await FlushStatisticsAsync();
        _messenger.Send(new NovelLibraryChangedMessage());
    }

    private async Task SaveBookmarkSidecarAsync(
        string? bookRootPath,
        int chapterIndex,
        double progress,
        int currentCharacterCount,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bookRootPath))
            return;

        await _novelBookSidecarService.SaveBookmarkAsync(
            bookRootPath,
            new NovelBookmark(
                chapterIndex,
                Math.Clamp(progress, 0, 1),
                currentCharacterCount,
                DateTimeOffset.UtcNow),
            ct);
    }

    private NovelReadingStatistic EnsureTodayStatistic(DateTimeOffset now)
    {
        var dateKey = FormatDateKey(now);
        return TodaysStatistics.DateKey == dateKey
            ? TodaysStatistics
            : _statistics.FirstOrDefault(s => s.DateKey == dateKey)
                ?? DefaultStatistic(ReaderTitle, now);
    }

    private void UpsertTodayStatistic()
    {
        var remaining = _statistics
            .Where(s => s.DateKey != TodaysStatistics.DateKey)
            .ToList();
        remaining.Add(TodaysStatistics);
        _statistics = remaining;
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

    private static NovelReadingStatistic UpdateStatistic(
        NovelReadingStatistic statistic,
        double timeDiff,
        int characterDiff,
        long lastStatisticModified)
    {
        var readingTime = statistic.ReadingTime + timeDiff;
        var charactersRead = Math.Max(statistic.CharactersRead + characterDiff, 0);
        var lastReadingSpeed = readingTime > 0
            ? (int)((charactersRead / readingTime) * 3600d)
            : 0;
        var minReadingSpeed = statistic.MinReadingSpeed != 0
            ? Math.Min(statistic.MinReadingSpeed, lastReadingSpeed)
            : lastReadingSpeed;
        var altMinReadingSpeed = characterDiff != 0
            ? statistic.AltMinReadingSpeed != 0
                ? Math.Min(statistic.AltMinReadingSpeed, lastReadingSpeed)
                : lastReadingSpeed
            : statistic.AltMinReadingSpeed;

        return statistic with
        {
            CharactersRead = charactersRead,
            ReadingTime = readingTime,
            MinReadingSpeed = minReadingSpeed,
            AltMinReadingSpeed = altMinReadingSpeed,
            LastReadingSpeed = lastReadingSpeed,
            MaxReadingSpeed = Math.Max(statistic.MaxReadingSpeed, lastReadingSpeed),
            LastStatisticModified = lastStatisticModified,
        };
    }

    private static NovelReadingStatistic AggregateAllTime(
        string title,
        DateTimeOffset now,
        IReadOnlyList<NovelReadingStatistic> statistics)
    {
        var allTime = DefaultStatistic(title, now);
        foreach (var statistic in statistics)
        {
            var readingTime = allTime.ReadingTime + statistic.ReadingTime;
            var charactersRead = allTime.CharactersRead + statistic.CharactersRead;
            allTime = allTime with
            {
                ReadingTime = readingTime,
                CharactersRead = charactersRead,
                LastReadingSpeed = readingTime > 0
                    ? (int)((charactersRead / readingTime) * 3600d)
                    : 0,
                MaxReadingSpeed = Math.Max(allTime.MaxReadingSpeed, statistic.MaxReadingSpeed),
                LastStatisticModified = Math.Max(
                    allTime.LastStatisticModified,
                    statistic.LastStatisticModified),
            };
        }

        return allTime;
    }

    private static NovelReadingStatistic DefaultStatistic(string title, DateTimeOffset now) =>
        new(
            title,
            FormatDateKey(now),
            CharactersRead: 0,
            ReadingTime: 0,
            MinReadingSpeed: 0,
            AltMinReadingSpeed: 0,
            LastReadingSpeed: 0,
            MaxReadingSpeed: 0,
            LastStatisticModified: 0);

    private static string FormatDateKey(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatCount(int value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatSpeed(int value) =>
        $"{FormatCount(value)} / h";

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 24
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private void OnStatisticsTextChanged()
    {
        OnPropertyChanged(nameof(StatisticsTrackingButtonText));
        OnPropertyChanged(nameof(StatisticsSessionCharactersText));
        OnPropertyChanged(nameof(StatisticsSessionSpeedText));
        OnPropertyChanged(nameof(StatisticsSessionTimeText));
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
        await SaveProgressNowAsync();
        _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
    }
}
