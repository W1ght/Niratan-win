using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupBatchPlannerTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 1, 1 })]
    [InlineData(4, new[] { 1, 3 })]
    [InlineData(9, new[] { 1, 3, 3, 2 })]
    public void Create_PreservesAllResultsWithOneInitialResult(int resultCount, int[] counts)
    {
        var ranges = DictionaryPopupBatchPlanner.Create(resultCount);

        ranges.Select(range => range.Count).Should().Equal(counts);
        ranges.Sum(range => range.Count).Should().Be(resultCount);

        var expectedOffsets = new List<int>();
        var offset = 0;
        foreach (var count in counts)
        {
            expectedOffsets.Add(offset);
            offset += count;
        }

        ranges.Select(range => range.Offset).Should().Equal(expectedOffsets);
    }
}
