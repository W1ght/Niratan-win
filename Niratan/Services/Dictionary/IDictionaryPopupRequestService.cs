using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Anki;
using Niratan.Models.Dictionary;

namespace Niratan.Services.Dictionary;

public interface IDictionaryPopupRequestService
{
    Task<DictionaryPopupRequest?> CreateAsync(
        string query,
        AnkiMiningContext? miningContext = null,
        string? traceId = null,
        CancellationToken ct = default);
}
