using System;

namespace Niratan.Models.Novel;

public readonly record struct ReaderNavigationPositionSnapshot(
    string BookId,
    int ChapterIndex,
    double Progress,
    int CharacterCount,
    int TotalCharacterCount,
    long Revision);

public readonly record struct ReaderNavigationDestination(
    int ChapterIndex,
    ReaderChapterRestoreTarget? RestoreTarget,
    double? ExactProgress)
{
    public static ReaderNavigationDestination AtChapterStart(int chapterIndex) =>
        new(chapterIndex, ReaderChapterRestoreTarget.Start, null);

    public static ReaderNavigationDestination AtChapterEnd(int chapterIndex) =>
        new(chapterIndex, ReaderChapterRestoreTarget.End, null);

    public static ReaderNavigationDestination AtProgress(int chapterIndex, double progress) =>
        new(chapterIndex, null, Math.Clamp(progress, 0, 1));
}

public sealed record ReaderNavigationRenderRequest(
    long Generation,
    ReaderNavigationPositionSnapshot Source,
    ReaderNavigationDestination Destination);

public sealed record ReaderNavigationCommitLease(
    long Generation,
    ReaderNavigationPositionSnapshot Source,
    ReaderNavigationPositionSnapshot ResolvedDestination);

public sealed record ReaderNavigationSettlement(
    long Generation,
    ReaderNavigationPositionSnapshot Position,
    bool ShouldRevealDestination);

public enum ReaderNavigationResolutionDisposition
{
    Ignored,
    Settled,
}

public sealed record ReaderNavigationResolutionResult(
    ReaderNavigationResolutionDisposition Disposition,
    ReaderNavigationSettlement? Settlement)
{
    public static ReaderNavigationResolutionResult Ignored { get; } =
        new(ReaderNavigationResolutionDisposition.Ignored, null);

    public static ReaderNavigationResolutionResult FromSettlement(
        ReaderNavigationSettlement settlement) =>
        new(ReaderNavigationResolutionDisposition.Settled, settlement);

    public ReaderNavigationPositionSnapshot Position =>
        Settlement?.Position
        ?? throw new InvalidOperationException("Ignored navigation resolutions do not have a position.");

    public bool ShouldRevealDestination =>
        Settlement?.ShouldRevealDestination
        ?? throw new InvalidOperationException("Ignored navigation resolutions do not have a settlement.");
}
