using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelStatisticsDashboardService
{
    Task<NovelStatisticsDashboardSnapshot> LoadSnapshotAsync(
        IReadOnlyList<NovelBook> books,
        CancellationToken ct = default);
}
