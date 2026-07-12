using System;

namespace Hoshi.Models.Novel;

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
