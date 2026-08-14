using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelStatisticsMutationCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_MatchingActiveReaderOwnsTheWholeMutation()
    {
        var coordinator = new NovelStatisticsMutationCoordinator();
        var reader = new RecordingReader("book-a");
        coordinator.Register(reader);
        var mutationCalls = 0;

        await coordinator.ExecuteAsync(
            "book-a",
            _ =>
            {
                mutationCalls++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        reader.ExecutionCount.Should().Be(1);
        mutationCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_OtherBookRunsWithoutTouchingActiveReader()
    {
        var coordinator = new NovelStatisticsMutationCoordinator();
        var reader = new RecordingReader("book-a");
        coordinator.Register(reader);
        var mutationCalls = 0;

        await coordinator.ExecuteAsync(
            "book-b",
            _ =>
            {
                mutationCalls++;
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        reader.ExecutionCount.Should().Be(0);
        mutationCalls.Should().Be(1);
    }

    private sealed class RecordingReader(string bookId) : INovelStatisticsActiveReader
    {
        public string? ActiveStatisticsBookId => bookId;
        public int ExecutionCount { get; private set; }

        public async Task ExecuteExternalStatisticsMutationAsync(
            Func<CancellationToken, Task> mutation,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            await mutation(ct);
        }
    }
}
