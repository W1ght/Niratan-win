using System;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Views.Pages;

internal enum NovelReaderRenderAttemptKind
{
    Ordinary,
    Destination,
    Recovery,
}

internal enum NovelReaderChapterReadyDisposition
{
    Rejected,
    Ordinary,
    HiddenInitial,
    HiddenTerminal,
}

internal sealed record NovelReaderRenderAttempt(
    string Uri,
    int ChapterIndex,
    double? Progress,
    ReaderChapterRestoreTarget? RestoreTarget,
    long? NavigationGeneration,
    NovelReaderRenderAttemptKind Kind);

internal readonly record struct NovelReaderRenderRelease(long Generation);

internal sealed class NovelReaderRenderState
{
    private ReaderNavigationRenderRequest? _navigationRequest;
    private ReaderNavigationSettlement? _pendingSettlement;
    private NovelReaderRenderAttempt? _currentAttempt;
    private TaskCompletionSource? _terminalReady;
    private bool _hiddenChapterReady;
    private bool _deferredOrdinaryReload;
    private bool _terminalReleaseReserved;

    public bool HasActiveNavigation => _navigationRequest != null;
    public bool HasDeferredOrdinaryReload => _deferredOrdinaryReload;
    public bool HiddenChapterReady => _hiddenChapterReady;
    public ReaderNavigationRenderRequest? NavigationRequest => _navigationRequest;
    public ReaderNavigationSettlement? PendingSettlement => _pendingSettlement;
    public NovelReaderRenderAttempt? CurrentAttempt => _currentAttempt;

    public void BeginNavigation(
        ReaderNavigationRenderRequest request,
        string destinationUri,
        bool waitsForFragment)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationUri);
        if (_navigationRequest != null)
            throw new InvalidOperationException("A reader navigation render is already active.");

        _navigationRequest = request;
        _pendingSettlement = null;
        _hiddenChapterReady = false;
        _terminalReleaseReserved = false;
        _terminalReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _currentAttempt = new NovelReaderRenderAttempt(
            destinationUri,
            request.Destination.ChapterIndex,
            request.Destination.ExactProgress,
            request.Destination.RestoreTarget,
            waitsForFragment ? null : request.Generation,
            NovelReaderRenderAttemptKind.Destination);
    }

    public bool TryBeginOrdinary(string uri, int chapterIndex, double progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        if (_navigationRequest != null)
        {
            _deferredOrdinaryReload = true;
            return false;
        }

        _currentAttempt = new NovelReaderRenderAttempt(
            uri,
            chapterIndex,
            progress,
            null,
            null,
            NovelReaderRenderAttemptKind.Ordinary);
        return true;
    }

    public bool TryGetDomAttempt(string uri, out NovelReaderRenderAttempt attempt)
    {
        if (_currentAttempt is { } current
            && string.Equals(current.Uri, uri, StringComparison.Ordinal))
        {
            attempt = current;
            return true;
        }

        attempt = null!;
        return false;
    }

    public bool TryApplySettlement(
        ReaderNavigationSettlement settlement,
        string recoveryUri)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (_navigationRequest?.Generation != settlement.Generation
            || _pendingSettlement != null
            || _terminalReleaseReserved)
        {
            return false;
        }

        _pendingSettlement = settlement;
        if (settlement.ShouldRevealDestination)
            return true;

        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryUri);
        _hiddenChapterReady = false;
        _currentAttempt = new NovelReaderRenderAttempt(
            recoveryUri,
            settlement.Position.ChapterIndex,
            settlement.Position.Progress,
            null,
            settlement.Generation,
            NovelReaderRenderAttemptKind.Recovery);
        return true;
    }

    public NovelReaderChapterReadyDisposition AcceptChapterReady(
        int chapterIndex,
        long? navigationGeneration)
    {
        if (_currentAttempt is not { } attempt
            || attempt.ChapterIndex != chapterIndex)
        {
            return NovelReaderChapterReadyDisposition.Rejected;
        }

        if (_navigationRequest is not { } request)
        {
            return navigationGeneration == null
                ? NovelReaderChapterReadyDisposition.Ordinary
                : NovelReaderChapterReadyDisposition.Rejected;
        }

        if (navigationGeneration == request.Generation)
        {
            _hiddenChapterReady = true;
            return NovelReaderChapterReadyDisposition.HiddenTerminal;
        }

        return navigationGeneration == null
            && attempt.Kind == NovelReaderRenderAttemptKind.Destination
            && attempt.NavigationGeneration == null
                ? NovelReaderChapterReadyDisposition.HiddenInitial
                : NovelReaderChapterReadyDisposition.Rejected;
    }

    public Task WaitForTerminalAsync(long generation) =>
        _navigationRequest?.Generation == generation
            ? _terminalReady?.Task ?? Task.CompletedTask
            : Task.CompletedTask;

    public bool TryPrepareCompletion(out NovelReaderRenderRelease release)
    {
        if (_navigationRequest is not { } request
            || _pendingSettlement is not { } settlement
            || !_hiddenChapterReady
            || _terminalReleaseReserved
            || settlement.Generation != request.Generation
            || _currentAttempt?.ChapterIndex != settlement.Position.ChapterIndex)
        {
            release = default;
            return false;
        }

        _terminalReleaseReserved = true;
        release = new NovelReaderRenderRelease(request.Generation);
        return true;
    }

    public NovelReaderRenderRelease? TryPrepareFailure()
    {
        if (_navigationRequest is not { } request || _terminalReleaseReserved)
            return null;

        _terminalReleaseReserved = true;
        return new NovelReaderRenderRelease(request.Generation);
    }

    public bool CompleteSuccess(NovelReaderRenderRelease release)
    {
        if (!CanCompletePreparedRelease(release)
            || _pendingSettlement is not { } settlement)
        {
            return false;
        }

        CompleteSuccessfully(release.Generation, settlement.Position.Progress);
        return true;
    }

    public bool CompleteFailure(NovelReaderRenderRelease release)
    {
        if (!CanCompletePreparedRelease(release))
            return false;

        CompleteAndClear(release.Generation);
        return true;
    }

    public bool TryTakeDeferredOrdinaryReload()
    {
        if (!_deferredOrdinaryReload)
            return false;

        _deferredOrdinaryReload = false;
        return true;
    }

    public void DiscardDeferredOrdinaryReload() =>
        _deferredOrdinaryReload = false;

    private NovelReaderRenderRelease CompleteAndClear(long generation)
    {
        var terminalReady = _terminalReady;
        _navigationRequest = null;
        _pendingSettlement = null;
        _currentAttempt = null;
        _terminalReady = null;
        _hiddenChapterReady = false;
        _terminalReleaseReserved = false;
        terminalReady?.TrySetResult();
        return new NovelReaderRenderRelease(generation);
    }

    private NovelReaderRenderRelease CompleteSuccessfully(
        long generation,
        double settledProgress)
    {
        var completedAttempt = _currentAttempt is { } attempt
            ? attempt with
            {
                Progress = settledProgress,
                RestoreTarget = null,
                NavigationGeneration = null,
                Kind = NovelReaderRenderAttemptKind.Ordinary,
            }
            : null;
        var release = CompleteAndClear(generation);
        _currentAttempt = completedAttempt;
        return release;
    }

    private bool CanCompletePreparedRelease(NovelReaderRenderRelease release) =>
        _terminalReleaseReserved
        && _navigationRequest?.Generation == release.Generation;
}
