using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Sync;

public interface IReaderAutoSyncCoordinator
{
    Task<bool> ImportOnOpenAsync(NovelBook book, CancellationToken ct = default);

    void ScheduleExport(NovelBook book);

    /// <summary>
    /// Cancels the debounce and drains pending work. Concurrent callers join the
    /// same in-flight flush and do not create duplicate exports. The first caller
    /// owns work cancellation; later callers can cancel only their own wait.
    /// </summary>
    Task FlushAsync(NovelBook book, CancellationToken ct = default);

    void Cancel();
}
