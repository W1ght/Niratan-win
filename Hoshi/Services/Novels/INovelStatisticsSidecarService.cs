using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelStatisticsSidecarService
{
    Task<IReadOnlyList<NovelReadingStatistic>> LoadAsync(
        string bookRootPath,
        CancellationToken ct = default);

    Task SaveAsync(
        string bookRootPath,
        IReadOnlyList<NovelReadingStatistic> statistics,
        CancellationToken ct = default);
}
