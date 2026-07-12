using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Novels;

public sealed class ReaderAdjacentNavigationCommitCoordinator(
    ReaderProgrammaticNavigationTracker tracker)
{
    public async Task<bool> CommitAsync(
        long generation,
        int chapterIndex,
        double resolvedProgress,
        Func<CancellationToken, Task<bool>> persistAsync,
        Func<Task> publishVisibleStateAsync,
        Func<Task> recoverAsync,
        CancellationToken ct = default,
        Func<Task>? prepareAsync = null)
    {
        ArgumentNullException.ThrowIfNull(persistAsync);
        ArgumentNullException.ThrowIfNull(publishVisibleStateAsync);
        ArgumentNullException.ThrowIfNull(recoverAsync);

        if (!tracker.TryBeginCompletion(generation, chapterIndex, resolvedProgress))
            return false;

        try
        {
            if (prepareAsync != null)
                await prepareAsync();

            if (!await persistAsync(ct))
            {
                tracker.AbortCommit(generation, chapterIndex);
                await recoverAsync();
                return false;
            }

            await publishVisibleStateAsync();
            if (tracker.CompleteCommit(generation, chapterIndex))
                return true;

            tracker.AbortCommit(generation, chapterIndex);
            await recoverAsync();
            return false;
        }
        catch
        {
            tracker.AbortCommit(generation, chapterIndex);
            await recoverAsync();
            throw;
        }
    }
}
