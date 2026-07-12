namespace Hoshi.Models.Novel;

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

public readonly record struct ReaderPageNavigationEvent(
    ReaderPageNavigationResult Result,
    ReaderPageNavigationDirection Direction,
    double Progress);

public readonly record struct ReaderPageNavigationOutcome(
    bool DidMove,
    int? AdjacentChapterIndex,
    double? AdjacentChapterProgress)
{
    public static ReaderPageNavigationOutcome NoMovement => new(false, null, null);
    public static ReaderPageNavigationOutcome SameChapterMovement => new(true, null, null);

    public static ReaderPageNavigationOutcome AdjacentChapter(
        int index,
        ReaderPageNavigationDirection direction) =>
        new(
            true,
            index,
            direction == ReaderPageNavigationDirection.Backward ? 1 : 0);
}
