using System;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public static class ReaderStatisticsEventClassifier
{
    private const double ProgressTolerance = 0.000001;

    public static bool TryCreateEvent(
        string? result,
        string? direction,
        double progress,
        out ReaderPageNavigationEvent readerEvent)
    {
        readerEvent = default;
        if (!double.IsFinite(progress))
            return false;

        var parsedResult = result switch
        {
            "scrolled" => ReaderPageNavigationResult.Scrolled,
            "limit" => ReaderPageNavigationResult.Limit,
            _ => (ReaderPageNavigationResult?)null,
        };
        var parsedDirection = direction switch
        {
            "forward" => ReaderPageNavigationDirection.Forward,
            "backward" => ReaderPageNavigationDirection.Backward,
            _ => (ReaderPageNavigationDirection?)null,
        };
        if (parsedResult is null || parsedDirection is null)
            return false;

        readerEvent = new(
            parsedResult.Value,
            parsedDirection.Value,
            Math.Clamp(progress, 0, 1));
        return true;
    }

    public static bool IsActualPageMovement(
        ReaderPageNavigationEvent readerEvent,
        double previousProgress) =>
        readerEvent.Result == ReaderPageNavigationResult.Scrolled
        && HasProgressMovement(previousProgress, readerEvent.Progress);

    public static int? AdjacentChapterTarget(
        ReaderPageNavigationEvent readerEvent,
        int currentChapter,
        int chapterCount)
    {
        if (readerEvent.Result != ReaderPageNavigationResult.Limit)
            return null;

        var target = readerEvent.Direction switch
        {
            ReaderPageNavigationDirection.Forward => currentChapter + 1,
            ReaderPageNavigationDirection.Backward => currentChapter - 1,
            _ => -1,
        };
        return target >= 0 && target < chapterCount ? target : null;
    }

    public static bool HasProgressMovement(
        double previousProgress,
        double currentProgress) =>
        double.IsFinite(previousProgress)
        && double.IsFinite(currentProgress)
        && Math.Abs(currentProgress - previousProgress) > ProgressTolerance;
}
