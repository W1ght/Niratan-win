using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.QBittorrent;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public interface IVideoDownloadImportService
{
    Task<Result<IReadOnlyList<VideoLibrarySource>>> GetCompatibleSourcesAsync(
        QbittorrentTorrent task,
        CancellationToken ct = default);

    Task<Result<VideoSourceRefreshResult>> ImportCompletedTaskAsync(
        QbittorrentTorrent task,
        string sourceId,
        CancellationToken ct = default);
}
