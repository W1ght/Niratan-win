using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.QBittorrent;

namespace Niratan.Services.QBittorrent;

public interface IQbittorrentCredentialStore
{
    bool HasCredentials { get; }

    Task<QbittorrentCredentials?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(QbittorrentCredentials credentials, CancellationToken ct = default);

    Task DeleteAsync(CancellationToken ct = default);
}
