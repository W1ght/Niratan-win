using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public interface ITorrentDownloadService
{
    Task<Result<TorrentDownloadResult>> DownloadAsync(
        string taskId,
        NyaaTorrentItem item,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken ct = default);

    Task<Result> PauseAsync(string taskId);

    Task<Result> ResumeAsync(string taskId);
}
