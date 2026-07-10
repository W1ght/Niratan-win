using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryNativeExecutorTests
{
    [Fact]
    public async Task RunAsync_ReturnsBeforeWorkerOperationCompletes()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var releaseOperation = new ManualResetEventSlim(false);
        var invocationReturned = new TaskCompletionSource<Task<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            var work = DictionaryNativeExecutor.RunAsync(gate, () =>
            {
                releaseOperation.Wait();
                return Environment.CurrentManagedThreadId;
            });
            invocationReturned.SetResult(work);
        })
        {
            IsBackground = true,
        };

        caller.Start();
        var work = await invocationReturned.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        work.IsCompleted.Should().BeFalse();
        caller.Join(TimeSpan.FromSeconds(2)).Should().BeTrue();
        releaseOperation.Set();
        await work.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_SerializesOperationsSharingGate()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var concurrent = 0;
        var maximum = 0;

        async Task RunOne()
        {
            await DictionaryNativeExecutor.RunAsync(gate, () =>
            {
                var current = Interlocked.Increment(ref concurrent);
                InterlockedExtensions.Max(ref maximum, current);
                Thread.Sleep(20);
                Interlocked.Decrement(ref concurrent);
                return true;
            });
        }

        await Task.WhenAll(RunOne(), RunOne(), RunOne());

        maximum.Should().Be(1);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
