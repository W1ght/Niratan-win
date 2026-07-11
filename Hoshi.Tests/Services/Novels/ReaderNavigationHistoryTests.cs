using FluentAssertions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderNavigationHistoryTests
{
    [Fact]
    public void BackAndForward_PreserveCurrentPositionOnOppositeStack()
    {
        var history = new ReaderNavigationHistory();
        var origin = new ReaderNavigationPosition(0, 0.2);
        var destination = new ReaderNavigationPosition(2, 0.7);

        history.Record(origin);

        history.TryGoBack(destination, out var back).Should().BeTrue();
        back.Should().Be(origin);
        history.ForwardTarget.Should().Be(destination);

        history.TryGoForward(origin, out var forward).Should().BeTrue();
        forward.Should().Be(destination);
        history.BackTarget.Should().Be(origin);
    }

    [Fact]
    public void Record_ClearsForwardAndAvoidsAdjacentDuplicates()
    {
        var history = new ReaderNavigationHistory();
        var origin = new ReaderNavigationPosition(0, 0.2);
        var destination = new ReaderNavigationPosition(1, 0.4);

        history.Record(origin);
        history.TryGoBack(destination, out _).Should().BeTrue();
        history.Record(origin);
        history.Record(origin);

        history.ForwardTarget.Should().BeNull();
        history.BackTarget.Should().Be(origin);
    }

    [Fact]
    public void ClearForward_InvalidatesForwardNavigationAfterManualMovement()
    {
        var history = new ReaderNavigationHistory();
        var origin = new ReaderNavigationPosition(0, 0.2);
        var destination = new ReaderNavigationPosition(1, 0.4);

        history.Record(origin);
        history.TryGoBack(destination, out _).Should().BeTrue();

        history.ClearForward();

        history.ForwardTarget.Should().BeNull();
        history.TryGoForward(origin, out _).Should().BeFalse();
    }
}
