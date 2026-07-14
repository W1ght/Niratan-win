namespace Niratan.Models.Novel;

public enum ReaderPageNavigationResult
{
    Scrolled,
    Limit,
}

public enum ReaderPageNavigationDirection
{
    Forward,
    Backward,
}

public enum ReaderChapterRestoreTarget
{
    Start,
    End,
}

public readonly record struct ReaderPageNavigationEvent(
    ReaderPageNavigationResult Result,
    ReaderPageNavigationDirection Direction,
    double Progress);

public readonly record struct ReaderPageNavigationOutcome(
    bool DidMove,
    int? AdjacentChapterIndex,
    ReaderChapterRestoreTarget? AdjacentChapterRestoreTarget)
{
    public static ReaderPageNavigationOutcome NoMovement => new(false, null, null);
    public static ReaderPageNavigationOutcome SameChapterMovement => new(true, null, null);

    public static ReaderPageNavigationOutcome AdjacentChapter(
        int index,
        ReaderPageNavigationDirection direction) =>
        new(
            true,
            index,
            direction == ReaderPageNavigationDirection.Backward
                ? ReaderChapterRestoreTarget.End
                : ReaderChapterRestoreTarget.Start);
}
