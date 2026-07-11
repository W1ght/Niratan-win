using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelStatisticsSidecarService
{
    Task<NovelStatisticsSidecarLoadResult> LoadWithStatusAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task<IReadOnlyList<NovelReadingStatistic>> LoadAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task SaveAsync(
        string bookRootPath,
        IReadOnlyList<NovelReadingStatistic> statistics,
        CancellationToken ct = default);
}
