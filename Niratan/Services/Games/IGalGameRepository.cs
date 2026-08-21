using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

public sealed record GalGameRepositoryReadResult(
    IReadOnlyList<GalGameEntry> Games,
    string? Error = null);

public interface IGalGameRepository
{
    Task<GalGameRepositoryReadResult> LoadAsync(CancellationToken ct = default);
    Task AddAsync(GalGameEntry entry, CancellationToken ct = default);
    Task UpdateAsync(GalGameEntry entry, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
}
