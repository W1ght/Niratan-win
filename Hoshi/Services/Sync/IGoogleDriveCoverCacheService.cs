using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public interface IGoogleDriveCoverCacheService
{
    Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
