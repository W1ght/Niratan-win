using FluentAssertions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderStatisticsEventClassifierTests
{
    [Theory]
    [InlineData("moved", 0.2, 0.3, true)]
    [InlineData("moved", 0.2, 0.2, false)]
    [InlineData("limit", 0.2, 0.3, false)]
    [InlineData("unknown", 0.2, 0.3, false)]
    public void IsActualPageMovement_RequiresMovedResultAndChangedProgress(
        string result,
        double previous,
        double current,
        bool expected)
    {
        ReaderStatisticsEventClassifier.IsActualPageMovement(
            result,
            previous,
            current).Should().Be(expected);
    }

    [Theory]
    [InlineData("limit", "forward", 0, 3, 1)]
    [InlineData("limit", "backward", 2, 3, 1)]
    [InlineData("limit", "backward", 0, 3, null)]
    [InlineData("limit", "forward", 2, 3, null)]
    [InlineData("moved", "forward", 0, 3, null)]
    public void AdjacentChapterTarget_OnlyReturnsInRangeNaturalBoundary(
        string result,
        string direction,
        int currentChapter,
        int chapterCount,
        int? expected)
    {
        ReaderStatisticsEventClassifier.AdjacentChapterTarget(
            result,
            direction,
            currentChapter,
            chapterCount).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.2, 0.3, true)]
    [InlineData(0.2, 0.2, false)]
    [InlineData(0.2, 0.20000001, false)]
    public void HasProgressMovement_UsesStableTolerance(
        double previous,
        double current,
        bool expected)
    {
        ReaderStatisticsEventClassifier.HasProgressMovement(previous, current)
            .Should().Be(expected);
    }
}
