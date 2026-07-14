using FluentAssertions;
using Niratan.Views.Controls;

namespace Niratan.Tests.Views.Controls;

public sealed class NovelStatisticsDashboardLayoutTests
{
    [Theory]
    [InlineData(0, NovelStatisticsDashboardLayoutMode.Narrow)]
    [InlineData(839.99, NovelStatisticsDashboardLayoutMode.Narrow)]
    [InlineData(840, NovelStatisticsDashboardLayoutMode.Medium)]
    [InlineData(1259.99, NovelStatisticsDashboardLayoutMode.Medium)]
    [InlineData(1260, NovelStatisticsDashboardLayoutMode.Wide)]
    [InlineData(1920, NovelStatisticsDashboardLayoutMode.Wide)]
    public void Select_UsesStableDashboardBreakpoints(
        double width,
        NovelStatisticsDashboardLayoutMode expected)
    {
        NovelStatisticsDashboardLayout.Select(width).Should().Be(expected);
    }
}
