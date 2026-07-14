using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public interface IGoogleDriveCoverCacheService
{
    Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
