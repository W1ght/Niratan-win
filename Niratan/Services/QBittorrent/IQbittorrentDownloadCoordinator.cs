using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;

namespace Niratan.Services.QBittorrent;

public interface IQbittorrentDownloadCoordinator
{
    event EventHandler? TasksChanged;

    IReadOnlyList<QbittorrentTorrent> GetTasks();

    Task<Result<IReadOnlyList<QbittorrentTorrent>>> RefreshAsync(CancellationToken ct = default);

    Task<Result<QbittorrentTorrentDetails>> GetDetailsAsync(
        string hash,
        CancellationToken ct = default);

    Task<Result> AddAsync(NyaaTorrentItem item, CancellationToken ct = default);

    Task<Result> PauseAsync(string hash, CancellationToken ct = default);

    Task<Result> ResumeAsync(string hash, CancellationToken ct = default);

    Task<Result> DeleteAsync(string hash, bool deleteFiles, CancellationToken ct = default);
}
