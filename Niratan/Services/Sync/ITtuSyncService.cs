using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public interface ITtuSyncService
{
    Task<TtuSyncResult> SyncBookAsync(
        NovelBook book,
        TtuSyncOptions options,
        CancellationToken ct = default);
}
