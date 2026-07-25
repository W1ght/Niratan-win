using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public interface INyaaClient
{
    Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchAsync(
        NyaaSearchRequest request,
        CancellationToken ct = default);
}
