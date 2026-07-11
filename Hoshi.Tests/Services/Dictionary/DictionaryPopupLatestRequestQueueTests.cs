using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupLatestRequestQueueTests
{
    [Fact]
    public void Replace_ReturnsDisplacedRequestAndTryTakeReturnsLatest()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        queue.Replace("first").Should().BeNull();
        queue.Replace("latest").Should().Be("first");

        queue.TryTake(out var request).Should().BeTrue();
        request.Should().Be("latest");
        queue.TryTake(out _).Should().BeFalse();
    }

    [Fact]
    public void TryTake_ReturnsCancelledRequestForExplicitTerminalHandling()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        queue.Replace("cancelled");

        queue.TryTake(out var request).Should().BeTrue();
        request.Should().Be("cancelled");
    }

    [Fact]
    public void Clear_ReturnsQueuedRequestOnlyOnce()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        queue.Replace("later");

        queue.Clear().Should().Be("later");
        queue.Clear().Should().BeNull();
        queue.TryTake(out _).Should().BeFalse();
    }

    [Fact]
    public void AcceptedA_QueuedB_ReplacedByC_ReturnsBAsTheTerminalDrop()
    {
        var queue = new DictionaryPopupLatestRequestQueue<string>();
        const string accepted = "A";
        queue.Replace("B");

        var dropped = queue.Replace("C");

        accepted.Should().Be("A");
        dropped.Should().Be("B");
        queue.TryTake(out var latest).Should().BeTrue();
        latest.Should().Be("C");
    }
}
