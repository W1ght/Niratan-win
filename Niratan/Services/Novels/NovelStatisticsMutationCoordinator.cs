using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public interface INovelStatisticsActiveReader
{
    string? ActiveStatisticsBookId { get; }

    Task ExecuteExternalStatisticsMutationAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default);
}

public interface INovelStatisticsMutationCoordinator
{
    void Register(INovelStatisticsActiveReader reader);

    void Unregister(INovelStatisticsActiveReader reader);

    Task ExecuteAsync(
        string bookId,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default);
}

public interface IExternalMutableReaderStatisticsSession
{
    Task ReloadAfterExternalMutationAsync(
        ReaderStatisticsPosition position,
        CancellationToken ct = default);
}

public sealed class NovelStatisticsMutationCoordinator
    : INovelStatisticsMutationCoordinator
{
    private readonly object _gate = new();
    private WeakReference<INovelStatisticsActiveReader>? _activeReader;

    public void Register(INovelStatisticsActiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
            _activeReader = new WeakReference<INovelStatisticsActiveReader>(reader);
    }

    public void Unregister(INovelStatisticsActiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_gate)
        {
            if (_activeReader?.TryGetTarget(out var active) == true
                && ReferenceEquals(active, reader))
            {
                _activeReader = null;
            }
        }
    }

    public Task ExecuteAsync(
        string bookId,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookId);
        ArgumentNullException.ThrowIfNull(mutation);

        INovelStatisticsActiveReader? reader = null;
        lock (_gate)
        {
            if (_activeReader?.TryGetTarget(out var candidate) == true)
                reader = candidate;
            else
                _activeReader = null;
        }

        return reader != null
            && string.Equals(
                reader.ActiveStatisticsBookId,
                bookId,
                StringComparison.Ordinal)
                ? reader.ExecuteExternalStatisticsMutationAsync(mutation, ct)
                : mutation(ct);
    }
}
