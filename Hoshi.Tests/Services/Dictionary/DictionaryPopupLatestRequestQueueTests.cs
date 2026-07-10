using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupLatestRequestQueueTests
{
    [Fact]
    public void TryTake_ReturnsOnlyLatestEligibleRequest()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        queue.Replace("first");
        queue.Replace("latest");

        queue.TryTake(value => value != "cancelled", out var request).Should().BeTrue();
        request.Should().Be("latest");
        queue.TryTake(_ => true, out _).Should().BeFalse();
    }

    [Fact]
    public void TryTake_DropsCancelledLatestRequest_AndClearDropsQueuedWork()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        queue.Replace("cancelled");

        queue.TryTake(value => value != "cancelled", out _).Should().BeFalse();

        queue.Replace("later");
        queue.Clear();
        queue.TryTake(_ => true, out _).Should().BeFalse();
    }
}
