using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Sync;

public interface IReaderAutoSyncCoordinator
{
    Task<bool> ImportOnOpenAsync(NovelBook book, CancellationToken ct = default);

    void ScheduleExport(NovelBook book);

    Task FlushAsync(NovelBook book, CancellationToken ct = default);

    void Cancel();
}
