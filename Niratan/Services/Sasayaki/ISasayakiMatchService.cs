using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public interface ISasayakiMatchService
{
    Task<SasayakiMatchData> MatchAsync(
        NovelBook book,
        string audiobookPath,
        string srtPath,
        int searchWindow,
        CancellationToken cancellationToken = default);
}
