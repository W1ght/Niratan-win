using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;

namespace Niratan.Services.QBittorrent;

public interface IQbittorrentClient
{
    Task<Result<QbittorrentConnectionInfo>> TestConnectionAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<QbittorrentTorrent>>> GetTorrentsAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        CancellationToken ct = default);

    Task<Result<QbittorrentTorrentDetails>> GetTorrentDetailsAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default);

    Task<Result> AddTorrentAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        NyaaTorrentItem item,
        CancellationToken ct = default);

    Task<Result> PauseAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default);

    Task<Result> ResumeAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        CancellationToken ct = default);

    Task<Result> DeleteAsync(
        QbittorrentSettings settings,
        QbittorrentCredentials credentials,
        string hash,
        bool deleteFiles,
        CancellationToken ct = default);
}
