using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public interface ISasayakiSidecarService
{
    const string MatchFileName = "sasayaki_match.json";
    const string PlaybackFileName = "sasayaki_playback.json";
    const string SourceFileName = "sasayaki_source.json";
    const string LegacyMatchFileName = "sasayaki.json";

    Task<SasayakiMatchData?> LoadMatchAsync(string bookRootPath, CancellationToken cancellationToken = default);
    Task SaveMatchAsync(string bookRootPath, SasayakiMatchData data, CancellationToken cancellationToken = default);
    Task<SasayakiSourceData?> LoadSourceAsync(string bookRootPath, CancellationToken cancellationToken = default);
    Task SaveSourceAsync(string bookRootPath, SasayakiSourceData data, CancellationToken cancellationToken = default);
    Task<SasayakiPlaybackData> LoadPlaybackAsync(string bookRootPath, CancellationToken cancellationToken = default);
    Task SavePlaybackAsync(string bookRootPath, SasayakiPlaybackData data, CancellationToken cancellationToken = default);
}
