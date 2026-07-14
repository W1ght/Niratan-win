using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public interface IReaderStatisticsSession
{
    ReaderStatisticsSessionState State { get; }

    event EventHandler<ReaderStatisticsSessionState>? StateChanged;

    Task LoadAsync(
        string bookRoot,
        string title,
        ReaderStatisticsPosition position,
        CancellationToken ct = default);

    void Start(ReaderStatisticsPosition position);

    void Tick(ReaderStatisticsPosition position);

    Task CheckpointAsync(
        ReaderStatisticsPosition position,
        ReaderStatisticsCheckpointReason reason,
        CancellationToken ct = default);

    Task PauseAsync(
        ReaderStatisticsPosition position,
        CancellationToken ct = default);

    Task StopAsync(
        ReaderStatisticsPosition position,
        CancellationToken ct = default);

    void ResetBaseline(ReaderStatisticsPosition position);
}
