using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public interface ITtuSyncService
{
    Task<TtuSyncResult> SyncBookAsync(
        NovelBook book,
        TtuSyncOptions options,
        CancellationToken ct = default);
}
