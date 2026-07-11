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

        Task WarmAsync(DictionaryPopupWarmLease lease)
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

        await coordinator.EnsureWarmAsync(_ => throw new InvalidOperationException());
        calls.Should().Be(1);
    }

    [Fact]
    public async Task FailedWarm_ResetsForRetry()
    {
        var coordinator = new DictionaryPopupWarmCoordinator();
        var calls = 0;

        Func<DictionaryPopupWarmLease, Task> warm = _ =>
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
        await coordinator.EnsureWarmAsync(_ => { calls++; return Task.CompletedTask; });

        coordinator.Reset();
        await coordinator.EnsureWarmAsync(_ => { calls++; return Task.CompletedTask; });

        calls.Should().Be(2);
        coordinator.IsWarm.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_FaultsInProgressCallerImmediately_AndLateDelegateSuccessIsIgnored()
    {
        var coordinator = new DictionaryPopupWarmCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var oldCaller = coordinator.EnsureWarmAsync(async lease =>
        {
            started.SetResult();
            await finishLate.Task; // deliberately ignores the cancelled lease
        });
        await started.Task;

        coordinator.Reset();
        Func<Task> awaitOldCaller = async () => await oldCaller;
        await awaitOldCaller.Should().ThrowAsync<OperationCanceledException>();

        finishLate.SetResult();
        await Task.Yield();
        coordinator.IsWarm.Should().BeFalse();

        await coordinator.EnsureWarmAsync(lease =>
        {
            lease.ThrowIfInvalid();
            return Task.CompletedTask;
        });
        coordinator.IsWarm.Should().BeTrue();
    }
}
