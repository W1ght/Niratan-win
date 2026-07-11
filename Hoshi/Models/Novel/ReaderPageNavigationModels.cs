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
    int? AdjacentChapterIndex)
{
    public static ReaderPageNavigationOutcome NoMovement => new(false, null);
    public static ReaderPageNavigationOutcome SameChapterMovement => new(true, null);
    public static ReaderPageNavigationOutcome AdjacentChapter(int index) => new(true, index);
}
