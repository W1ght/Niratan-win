using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

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
