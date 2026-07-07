using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Dictionary;

public interface IGlobalLookupWindowService
{
    Task OpenAsync(string? initialQuery = null, CancellationToken ct = default);
}
