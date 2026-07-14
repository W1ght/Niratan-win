using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public enum ReaderNavigationInputKind
{
    InternalLink,
    SearchResult,
    TableOfContents,
    Highlight,
    CharacterJump,
    Sasayaki,
    HistoryBack,
    HistoryForward,
}

public sealed record ReaderProgrammaticNavigationReservation(
    ReaderNavigationRenderRequest RenderRequest,
    Task DepartureCheckpoint);

public sealed record ReaderNavigationInputReservation(
    ReaderNavigationInputKind Kind,
    ReaderNavigationRenderRequest RenderRequest,
    Task DepartureCheckpoint,
    string? Fragment);

public sealed class ReaderNavigationInputCoordinator
{
    private readonly object _gate = new();
    private readonly Func<bool> _canMutatePosition;
    private readonly Func<
        int,
        ReaderChapterRestoreTarget?,
        double?,
        CancellationToken,
        ReaderProgrammaticNavigationReservation?> _reserveNavigation;
    private readonly ReaderNavigationHistory _history = new();
    private PendingHistoryMutation? _pendingHistoryMutation;

    public ReaderNavigationInputCoordinator(
        Func<bool> canMutatePosition,
        Func<
            int,
            ReaderChapterRestoreTarget?,
            double?,
            CancellationToken,
            ReaderProgrammaticNavigationReservation?> reserveNavigation)
    {
        _canMutatePosition = canMutatePosition
            ?? throw new ArgumentNullException(nameof(canMutatePosition));
        _reserveNavigation = reserveNavigation
            ?? throw new ArgumentNullException(nameof(reserveNavigation));
    }

    public bool CanAcceptPositionMutation => _canMutatePosition();

    public ReaderNavigationPosition? BackTarget
    {
        get
        {
            lock (_gate)
                return _history.BackTarget;
        }
    }

    public ReaderNavigationPosition? ForwardTarget
    {
        get
        {
            lock (_gate)
                return _history.ForwardTarget;
        }
    }

    public ReaderNavigationInputReservation? TryNavigate(
        ReaderNavigationInputKind kind,
        int chapterIndex,
        double progress,
        string? fragment = null,
        bool recordHistory = true,
        CancellationToken ct = default)
    {
        progress = Math.Clamp(progress, 0, 1);
        fragment = string.IsNullOrWhiteSpace(fragment) ? null : fragment;
        lock (_gate)
        {
            var reservation = _reserveNavigation(
                chapterIndex,
                null,
                fragment == null ? progress : null,
                ct);
            if (reservation == null)
                return null;

            if (recordHistory)
            {
                _pendingHistoryMutation = new PendingHistoryMutation(
                    reservation.RenderRequest.Generation,
                    kind,
                    PositionOf(reservation.RenderRequest.Source),
                    Target: null);
            }
            return From(kind, reservation, fragment);
        }
    }

    public ReaderNavigationInputReservation? TryGoBack(
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var target = _history.BackTarget;
            if (!target.HasValue)
                return null;

            var reservation = ReserveHistoryTarget(
                ReaderNavigationInputKind.HistoryBack,
                target.Value,
                ct);
            if (reservation == null)
                return null;

            _pendingHistoryMutation = new PendingHistoryMutation(
                reservation.RenderRequest.Generation,
                ReaderNavigationInputKind.HistoryBack,
                PositionOf(reservation.RenderRequest.Source),
                target.Value);

            return reservation;
        }
    }

    public ReaderNavigationInputReservation? TryGoForward(
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var target = _history.ForwardTarget;
            if (!target.HasValue)
                return null;

            var reservation = ReserveHistoryTarget(
                ReaderNavigationInputKind.HistoryForward,
                target.Value,
                ct);
            if (reservation == null)
                return null;

            _pendingHistoryMutation = new PendingHistoryMutation(
                reservation.RenderRequest.Generation,
                ReaderNavigationInputKind.HistoryForward,
                PositionOf(reservation.RenderRequest.Source),
                target.Value);

            return reservation;
        }
    }

    public bool ApplyNavigationSettlement(ReaderNavigationSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        lock (_gate)
        {
            if (_pendingHistoryMutation is not { } pending
                || pending.Generation != settlement.Generation)
            {
                return false;
            }

            _pendingHistoryMutation = null;
            if (!settlement.ShouldRevealDestination)
                return true;

            switch (pending.Kind)
            {
                case ReaderNavigationInputKind.HistoryBack:
                    CommitBack(pending);
                    break;
                case ReaderNavigationInputKind.HistoryForward:
                    CommitForward(pending);
                    break;
                default:
                    _history.Record(pending.Source);
                    break;
            }

            return true;
        }
    }

    public void ClearForwardHistory()
    {
        lock (_gate)
            _history.ClearForward();
    }

    public async Task<bool> TryExecutePageTurnAsync(
        string direction,
        Func<string, Task> executeAuthorizedPageTurn)
    {
        ArgumentNullException.ThrowIfNull(executeAuthorizedPageTurn);
        if (direction is not ("forward" or "backward")
            || !CanAcceptPositionMutation)
        {
            return false;
        }

        await executeAuthorizedPageTurn(direction);
        return true;
    }

    public bool TryApplyPositionMutation(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!CanAcceptPositionMutation)
            return false;

        mutation();
        return true;
    }

    public void DispatchLiveSasayakiCue(
        bool sameChapter,
        bool autoScrollEnabled,
        Action<bool> highlightSameChapter,
        Action navigateChapter,
        Action clearHighlight)
    {
        ArgumentNullException.ThrowIfNull(highlightSameChapter);
        ArgumentNullException.ThrowIfNull(navigateChapter);
        ArgumentNullException.ThrowIfNull(clearHighlight);

        if (sameChapter)
        {
            highlightSameChapter(CanAcceptPositionMutation);
        }
        else if (autoScrollEnabled && CanAcceptPositionMutation)
        {
            navigateChapter();
        }
        else
        {
            clearHighlight();
        }
    }

    private ReaderNavigationInputReservation? ReserveHistoryTarget(
        ReaderNavigationInputKind kind,
        ReaderNavigationPosition target,
        CancellationToken ct)
    {
        var reservation = _reserveNavigation(
            target.ChapterIndex,
            null,
            target.Progress,
            ct);
        return reservation == null ? null : From(kind, reservation, fragment: null);
    }

    private static ReaderNavigationInputReservation From(
        ReaderNavigationInputKind kind,
        ReaderProgrammaticNavigationReservation reservation,
        string? fragment) =>
        new(
            kind,
            reservation.RenderRequest,
            reservation.DepartureCheckpoint,
            fragment);

    private static ReaderNavigationPosition PositionOf(
        ReaderNavigationPositionSnapshot snapshot) =>
        new(snapshot.ChapterIndex, snapshot.Progress);

    private void CommitBack(PendingHistoryMutation pending)
    {
        if (pending.Target is not { } expected
            || !_history.TryGoBack(pending.Source, out var target)
            || target != expected)
        {
            throw new InvalidOperationException(
                "Reader back history changed before destination settlement.");
        }
    }

    private void CommitForward(PendingHistoryMutation pending)
    {
        if (pending.Target is not { } expected
            || !_history.TryGoForward(pending.Source, out var target)
            || target != expected)
        {
            throw new InvalidOperationException(
                "Reader forward history changed before destination settlement.");
        }
    }

    private sealed record PendingHistoryMutation(
        long Generation,
        ReaderNavigationInputKind Kind,
        ReaderNavigationPosition Source,
        ReaderNavigationPosition? Target);
}
