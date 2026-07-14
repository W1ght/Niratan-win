using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public interface INovelStatisticsDashboardService
{
    event EventHandler<NovelStatisticsDashboardSnapshot>? SnapshotRefreshed;

    Task<NovelStatisticsDashboardSnapshot> LoadSnapshotAsync(
        IReadOnlyList<NovelBook> books,
        CancellationToken ct = default);
}
