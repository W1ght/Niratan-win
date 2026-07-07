using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public interface IDictionaryPopupRequestService
{
    Task<DictionaryPopupRequest?> CreateAsync(
        string query,
        AnkiMiningContext? miningContext = null,
        string? traceId = null,
        CancellationToken ct = default);
}
