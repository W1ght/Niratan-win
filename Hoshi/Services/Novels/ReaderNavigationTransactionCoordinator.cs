using System;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class ReaderNavigationTransactionCoordinator
{
    private readonly object _gate = new();
    private long _generation;
    private Transaction? _active;

    public bool BlocksPositionMutation
    {
        get
        {
            lock (_gate)
                return _active is not null;
        }
    }

    public ReaderNavigationRenderRequest? ActiveRenderRequest
    {
        get
        {
            lock (_gate)
                return _active?.RenderRequest;
        }
    }

    public ReaderNavigationRenderRequest? TryBegin(
        ReaderNavigationPositionSnapshot source,
        ReaderNavigationDestination destination)
    {
        lock (_gate)
        {
            if (_active is not null)
                return null;

            var renderRequest = new ReaderNavigationRenderRequest(
                ++_generation,
                source,
                destination);
            _active = new Transaction(renderRequest);
            return renderRequest;
        }
    }

    public ReaderNavigationCommitLease? TryBeginCommit(
        long generation,
        ReaderNavigationPositionSnapshot resolvedDestination)
    {
        lock (_gate)
        {
            if (_active is not { Phase: TransactionPhase.Rendering } active
                || active.RenderRequest.Generation != generation
                || !IsValidResolvedDestination(active.RenderRequest, resolvedDestination))
            {
                return null;
            }

            var lease = new ReaderNavigationCommitLease(
                generation,
                active.RenderRequest.Source,
                resolvedDestination);
            active.CommitLease = lease;
            active.Phase = TransactionPhase.Committing;
            return lease;
        }
    }

    public ReaderNavigationSettlement? TryCancelRendering(long generation)
    {
        lock (_gate)
        {
            if (_active is not { Phase: TransactionPhase.Rendering } active
                || active.RenderRequest.Generation != generation)
            {
                return null;
            }

            return Settle(active, active.RenderRequest.Source, shouldRevealDestination: false);
        }
    }

    public Task<ReaderNavigationSettlement?> HandleBridgeErrorAsync()
    {
        lock (_gate)
        {
            if (_active is null)
                return Task.FromResult<ReaderNavigationSettlement?>(null);

            if (_active.Phase == TransactionPhase.Rendering)
            {
                Settle(
                    _active,
                    _active.RenderRequest.Source,
                    shouldRevealDestination: false);
            }

            return AsNullableSettlementTask(_active.Settlement.Task);
        }
    }

    public ReaderNavigationSettlement? CompleteCommit(
        ReaderNavigationCommitLease lease,
        bool committed)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (_gate)
        {
            if (_active is not { Phase: TransactionPhase.Committing } active
                || active.CommitLease != lease)
            {
                return null;
            }

            return Settle(
                active,
                committed ? lease.ResolvedDestination : lease.Source,
                shouldRevealDestination: committed);
        }
    }

    public Task<ReaderNavigationSettlement?> WaitForSettlementAsync()
    {
        lock (_gate)
        {
            return _active is null
                ? Task.FromResult<ReaderNavigationSettlement?>(null)
                : AsNullableSettlementTask(_active.Settlement.Task);
        }
    }

    public bool AcknowledgeTerminalRender(long generation)
    {
        lock (_gate)
        {
            if (_active is not { Phase: TransactionPhase.Settled } active
                || active.RenderRequest.Generation != generation)
            {
                return false;
            }

            _active = null;
            return true;
        }
    }

    private static bool IsValidResolvedDestination(
        ReaderNavigationRenderRequest renderRequest,
        ReaderNavigationPositionSnapshot resolvedDestination) =>
        resolvedDestination.BookId == renderRequest.Source.BookId
        && resolvedDestination.ChapterIndex == renderRequest.Destination.ChapterIndex
        && double.IsFinite(resolvedDestination.Progress)
        && resolvedDestination.Progress is >= 0 and <= 1
        && resolvedDestination.CharacterCount >= 0
        && resolvedDestination.TotalCharacterCount >= 0
        && resolvedDestination.CharacterCount <= resolvedDestination.TotalCharacterCount
        && resolvedDestination.Revision > renderRequest.Source.Revision;

    private static ReaderNavigationSettlement Settle(
        Transaction transaction,
        ReaderNavigationPositionSnapshot position,
        bool shouldRevealDestination)
    {
        var settlement = new ReaderNavigationSettlement(
            transaction.RenderRequest.Generation,
            position,
            shouldRevealDestination);
        transaction.Phase = TransactionPhase.Settled;
        transaction.Settlement.SetResult(settlement);
        return settlement;
    }

    private static Task<ReaderNavigationSettlement?> AsNullableSettlementTask(
        Task<ReaderNavigationSettlement> task) =>
        (Task<ReaderNavigationSettlement?>)(object)task;

    private sealed class Transaction(ReaderNavigationRenderRequest renderRequest)
    {
        public ReaderNavigationRenderRequest RenderRequest { get; } = renderRequest;
        public TransactionPhase Phase { get; set; } = TransactionPhase.Rendering;
        public ReaderNavigationCommitLease? CommitLease { get; set; }
        public TaskCompletionSource<ReaderNavigationSettlement> Settlement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum TransactionPhase
    {
        Rendering,
        Committing,
        Settled,
    }
}
