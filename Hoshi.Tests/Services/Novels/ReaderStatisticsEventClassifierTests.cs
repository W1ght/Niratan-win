using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderStatisticsEventClassifierTests
{
    [Theory]
    [InlineData(ReaderPageNavigationResult.Scrolled, 0.2, 0.3, true)]
    [InlineData(ReaderPageNavigationResult.Scrolled, 0.2, 0.2, false)]
    [InlineData(ReaderPageNavigationResult.Limit, 0.2, 0.3, false)]
    public void IsActualPageMovement_RequiresScrolledResultAndChangedProgress(
        ReaderPageNavigationResult result,
        double previous,
        double current,
        bool expected)
    {
        ReaderStatisticsEventClassifier.IsActualPageMovement(
            new ReaderPageNavigationEvent(
                result,
                ReaderPageNavigationDirection.Forward,
                current),
            previous).Should().Be(expected);
    }

    [Theory]
    [InlineData(ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Forward, 0, 3, 1)]
    [InlineData(ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Backward, 2, 3, 1)]
    [InlineData(ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Backward, 0, 3, null)]
    [InlineData(ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Forward, 2, 3, null)]
    [InlineData(ReaderPageNavigationResult.Scrolled, ReaderPageNavigationDirection.Forward, 0, 3, null)]
    [InlineData(ReaderPageNavigationResult.Limit, (ReaderPageNavigationDirection)99, 1, 3, null)]
    public void AdjacentChapterTarget_OnlyReturnsInRangeNaturalBoundary(
        ReaderPageNavigationResult result,
        ReaderPageNavigationDirection direction,
        int currentChapter,
        int chapterCount,
        int? expected)
    {
        ReaderStatisticsEventClassifier.AdjacentChapterTarget(
            new ReaderPageNavigationEvent(result, direction, 0.5),
            currentChapter,
            chapterCount).Should().Be(expected);
    }

    [Theory]
    [InlineData("scrolled", "forward", 0.30, ReaderPageNavigationResult.Scrolled, ReaderPageNavigationDirection.Forward)]
    [InlineData("limit", "backward", 0.00, ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Backward)]
    public void TryCreateEvent_AcceptsBridgeVocabulary(
        string result,
        string direction,
        double progress,
        ReaderPageNavigationResult expectedResult,
        ReaderPageNavigationDirection expectedDirection)
    {
        ReaderStatisticsEventClassifier.TryCreateEvent(
            result, direction, progress, out var readerEvent).Should().BeTrue();
        readerEvent.Should().Be(new ReaderPageNavigationEvent(
            expectedResult, expectedDirection, progress));
    }

    [Theory]
    [InlineData("moved", "forward")]
    [InlineData("unknown", "forward")]
    [InlineData("scrolled", "sideways")]
    public void TryCreateEvent_RejectsUnknownVocabulary(string result, string direction)
    {
        ReaderStatisticsEventClassifier.TryCreateEvent(
            result, direction, 0.5, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TryCreateEvent_RejectsNonFiniteProgress(double progress)
    {
        ReaderStatisticsEventClassifier.TryCreateEvent(
            "scrolled", "forward", progress, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(-0.25, 0.0)]
    [InlineData(1.25, 1.0)]
    public void TryCreateEvent_ClampsProgressToBridgeRange(
        double progress,
        double expectedProgress)
    {
        ReaderStatisticsEventClassifier.TryCreateEvent(
            "scrolled", "forward", progress, out var readerEvent).Should().BeTrue();
        readerEvent.Progress.Should().Be(expectedProgress);
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
