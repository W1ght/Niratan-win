using System;

namespace Hoshi.Services.Novels;

public static class ReaderStatisticsEventClassifier
{
    private const double ProgressTolerance = 0.000001;

    public static bool IsActualPageMovement(
        string? result,
        double previousProgress,
        double currentProgress) =>
        string.Equals(result, "moved", StringComparison.Ordinal)
        && HasProgressMovement(previousProgress, currentProgress);

    public static int? AdjacentChapterTarget(
        string? result,
        string? direction,
        int currentChapter,
        int chapterCount)
    {
        if (!string.Equals(result, "limit", StringComparison.Ordinal))
            return null;

        var target = direction switch
        {
            "forward" => currentChapter + 1,
            "backward" => currentChapter - 1,
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
