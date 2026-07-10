using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupWarmCoordinatorTests
{
    [Fact]
    public async Task ConcurrentCallers_ShareOneWarmOperation()
    {
        var coordinator = new DictionaryPopupWarmCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task WarmAsync()
        {
            calls++;
            return release.Task;
        }

        var first = coordinator.EnsureWarmAsync(WarmAsync);
        var second = coordinator.EnsureWarmAsync(WarmAsync);

        calls.Should().Be(1);
        first.Should().BeSameAs(second);
        release.SetResult();
        await Task.WhenAll(first, second);
        coordinator.IsWarm.Should().BeTrue();

        await coordinator.EnsureWarmAsync(() => throw new InvalidOperationException());
        calls.Should().Be(1);
    }

    [Fact]
    public async Task FailedWarm_ResetsForRetry()
    {
        var coordinator = new DictionaryPopupWarmCoordinator();
        var calls = 0;

        Func<Task> warm = () =>
        {
            calls++;
            return calls == 1
                ? Task.FromException(new InvalidOperationException("cold failure"))
                : Task.CompletedTask;
        };

        await coordinator.Invoking(x => x.EnsureWarmAsync(warm)).Should().ThrowAsync<InvalidOperationException>();
        coordinator.IsWarm.Should().BeFalse();

        await coordinator.EnsureWarmAsync(warm);
        calls.Should().Be(2);
        coordinator.IsWarm.Should().BeTrue();
    }

    [Fact]
    public async Task ResetAfterProcessLoss_AllowsAnotherWarm()
    {
        var coordinator = new DictionaryPopupWarmCoordinator();
        var calls = 0;
        await coordinator.EnsureWarmAsync(() => { calls++; return Task.CompletedTask; });

        coordinator.Reset();
        await coordinator.EnsureWarmAsync(() => { calls++; return Task.CompletedTask; });

        calls.Should().Be(2);
        coordinator.IsWarm.Should().BeTrue();
    }
}
