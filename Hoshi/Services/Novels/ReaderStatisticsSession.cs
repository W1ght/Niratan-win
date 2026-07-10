using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class ReaderStatisticsSession : IReaderStatisticsSession
{
    private readonly INovelStatisticsSidecarService _sidecars;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyList<NovelReadingStatistic> _history = [];
    private string? _bookRoot;
    private string _title = "Novel reader";
    private DateTimeOffset _lastTimestamp;
    private int _lastRawCharacterCount;

    public ReaderStatisticsSessionState State { get; private set; }

    public event EventHandler<ReaderStatisticsSessionState>? StateChanged;

    public ReaderStatisticsSession(
        INovelStatisticsSidecarService sidecars,
        TimeProvider timeProvider)
    {
        _sidecars = sidecars ?? throw new ArgumentNullException(nameof(sidecars));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        var localDate = LocalDate(_timeProvider.GetUtcNow());
        var empty = ReaderStatisticsMath.Empty(_title, localDate);
        State = new ReaderStatisticsSessionState(
            IsTracking: false,
            IsPaused: false,
            empty,
            empty,
            empty,
            []);
        _lastTimestamp = _timeProvider.GetUtcNow();
    }

    public async Task LoadAsync(
        string bookRoot,
        string title,
        ReaderStatisticsPosition position,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var now = _timeProvider.GetUtcNow();
        var localDate = LocalDate(now);
        _bookRoot = bookRoot;
        _title = title;
        _history = ReaderStatisticsMath.Deduplicate(await _sidecars.LoadAsync(bookRoot, ct));
        var dateKey = ReaderStatisticsMath.Empty(title, localDate).DateKey;
        var today = _history.FirstOrDefault(item =>
            string.Equals(item.DateKey, dateKey, StringComparison.Ordinal))
            ?? ReaderStatisticsMath.Empty(title, localDate);
        var session = ReaderStatisticsMath.Empty(title, localDate);
        var allTime = ReaderStatisticsMath.Aggregate(title, localDate, _history);

        _lastTimestamp = now;
        _lastRawCharacterCount = position.RawCharacterCount;
        State = new ReaderStatisticsSessionState(
            IsTracking: false,
            IsPaused: false,
            session,
            today,
            allTime,
            _history);
        Publish();
    }

    public void Start(ReaderStatisticsPosition position)
    {
        var now = _timeProvider.GetUtcNow();
        _lastTimestamp = now;
        _lastRawCharacterCount = position.RawCharacterCount;
        State = State with
        {
            IsTracking = true,
            IsPaused = false,
        };
        Publish();
    }

    public void Tick(ReaderStatisticsPosition position)
    {
        if (!CanAccumulate())
            return;

        ApplyElapsed(position, _timeProvider.GetUtcNow());
        State = State with { History = HistoryWithToday() };
        Publish();
    }

    public async Task CheckpointAsync(
        ReaderStatisticsPosition position,
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct = default)
    {
        if (!CanAccumulate())
            return;

        ApplyElapsed(position, _timeProvider.GetUtcNow());
        await SaveHistoryAsync(ct);
        Publish();
    }

    public async Task PauseAsync(
        ReaderStatisticsPosition position,
        CancellationToken ct = default)
    {
        if (!CanAccumulate())
            return;

        ApplyElapsed(position, _timeProvider.GetUtcNow());
        await SaveHistoryAsync(ct);
        State = State with { IsPaused = true };
        Publish();
    }

    public async Task StopAsync(
        ReaderStatisticsPosition position,
        CancellationToken ct = default)
    {
        if (CanAccumulate())
        {
            ApplyElapsed(position, _timeProvider.GetUtcNow());
            await SaveHistoryAsync(ct);
        }

        State = State with
        {
            IsTracking = false,
            IsPaused = false,
        };
        Publish();
    }

    public void ResetBaseline(ReaderStatisticsPosition position)
    {
        _lastTimestamp = _timeProvider.GetUtcNow();
        _lastRawCharacterCount = position.RawCharacterCount;
    }

    private bool CanAccumulate() =>
        State.IsTracking && !State.IsPaused;

    private void ApplyElapsed(
        ReaderStatisticsPosition position,
        DateTimeOffset now)
    {
        RollOverTodayIfNeeded(now);

        var elapsedSeconds = (now - _lastTimestamp).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        var rawDifference = position.RawCharacterCount - _lastRawCharacterCount;
        var characterDifference = rawDifference < 0
            && Math.Abs((long)rawDifference) > State.Session.CharactersRead
                ? -State.Session.CharactersRead
                : rawDifference;
        var modifiedAt = now.ToUnixTimeMilliseconds();

        State = State with
        {
            Session = ReaderStatisticsMath.Update(
                State.Session,
                elapsedSeconds,
                characterDifference,
                modifiedAt),
            Today = ReaderStatisticsMath.Update(
                State.Today,
                elapsedSeconds,
                characterDifference,
                modifiedAt),
            AllTime = ReaderStatisticsMath.Update(
                State.AllTime,
                elapsedSeconds,
                characterDifference,
                modifiedAt),
        };
        _lastTimestamp = now;
        _lastRawCharacterCount = position.RawCharacterCount;
    }

    private void RollOverTodayIfNeeded(DateTimeOffset now)
    {
        var localDate = LocalDate(now);
        var currentDateKey = ReaderStatisticsMath.Empty(_title, localDate).DateKey;
        if (string.Equals(State.Today.DateKey, currentDateKey, StringComparison.Ordinal))
            return;

        _history = ReaderStatisticsMath.Deduplicate(_history.Append(State.Today));
        var today = _history.FirstOrDefault(item =>
            string.Equals(item.DateKey, currentDateKey, StringComparison.Ordinal))
            ?? ReaderStatisticsMath.Empty(_title, localDate);
        State = State with { Today = today };
    }

    private async Task SaveHistoryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_bookRoot))
            throw new InvalidOperationException("Reader statistics have not been loaded.");

        _history = HistoryWithToday();
        await _sidecars.SaveAsync(_bookRoot, _history, ct);
        State = State with { History = _history };
    }

    private IReadOnlyList<NovelReadingStatistic> HistoryWithToday() =>
        ReaderStatisticsMath.Deduplicate(_history.Append(State.Today));

    private DateOnly LocalDate(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(utcNow, _timeProvider.LocalTimeZone).DateTime);

    private void Publish() =>
        StateChanged?.Invoke(this, State);
}
