using FluentAssertions;
using Hoshi.Views.Dictionary;

namespace Hoshi.Tests.Views.Dictionary;

public sealed class DictionaryPopupCornerGuardTests
{
    [Theory]
    [InlineData(12, 4)]
    [InlineData(8, 3)]
    [InlineData(0, 0)]
    [InlineData(-4, 0)]
    public void CalculateInset_ContainsRectangularContentInsideRoundedShell(
        double radius,
        double expectedInset)
    {
        DictionaryPopupCornerGuard.CalculateInset(radius).Should().Be(expectedInset);
    }
}
