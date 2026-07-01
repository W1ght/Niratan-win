using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Sasayaki;

namespace Hoshi.Services.Sasayaki;

public interface ISasayakiMatchService
{
    Task<SasayakiMatchData> MatchAsync(
        NovelBook book,
        string audiobookPath,
        string srtPath,
        int searchWindow,
        CancellationToken cancellationToken = default);
}
