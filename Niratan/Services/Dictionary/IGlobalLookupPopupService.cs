using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Dictionary;

public interface IGlobalLookupPopupService
{
    Task ShowAsync(string query, CancellationToken ct = default);
}
