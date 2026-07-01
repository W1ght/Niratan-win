using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Sasayaki;

namespace Hoshi.Services.Sasayaki;

public interface ISasayakiSidecarService
{
    const string MatchFileName = "sasayaki_match.json";
    const string PlaybackFileName = "sasayaki_playback.json";
    const string LegacyMatchFileName = "sasayaki.json";

    Task<SasayakiMatchData?> LoadMatchAsync(string bookRootPath, CancellationToken cancellationToken = default);
    Task SaveMatchAsync(string bookRootPath, SasayakiMatchData data, CancellationToken cancellationToken = default);
    Task<SasayakiPlaybackData> LoadPlaybackAsync(string bookRootPath, CancellationToken cancellationToken = default);
    Task SavePlaybackAsync(string bookRootPath, SasayakiPlaybackData data, CancellationToken cancellationToken = default);
}
