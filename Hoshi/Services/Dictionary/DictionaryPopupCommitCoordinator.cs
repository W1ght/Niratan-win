using System;
using System.Threading.Tasks;

namespace Hoshi.Services.Dictionary;

internal enum DictionaryPopupCommitResolution
{
    Committed,
    Rejected,
    ReconciledCommitted,
    Aborted,
    RendererUnavailable,
}

internal static class DictionaryPopupCommitCoordinator
{
    public static async Task ObserveAsync(
        long generation,
        Func<Task<bool>> commitAsync,
        Func<Task<long?>> queryCommittedGenerationAsync,
        Func<Task> discardAsync,
        Action<DictionaryPopupCommitResolution> onResolved,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(commitAsync);
        ArgumentNullException.ThrowIfNull(queryCommittedGenerationAsync);
        ArgumentNullException.ThrowIfNull(discardAsync);
        ArgumentNullException.ThrowIfNull(onResolved);

        try
        {
            var committed = await commitAsync().WaitAsync(timeout);
            onResolved(committed
                ? DictionaryPopupCommitResolution.Committed
                : DictionaryPopupCommitResolution.Rejected);
            return;
        }
        catch (Exception)
        {
            // Reconcile below. A dead or unresponsive WebView must not leave
            // native ownership permanently commit-in-flight.
        }

        long? committedGeneration = null;
        var rendererAvailable = false;
        try
        {
            committedGeneration = await queryCommittedGenerationAsync().WaitAsync(timeout);
            rendererAvailable = true;
        }
        catch (Exception)
        {
            // Treat an unavailable renderer as not committed and abort native ownership.
        }

        if (committedGeneration == generation)
        {
            onResolved(DictionaryPopupCommitResolution.ReconciledCommitted);
            return;
        }

        try
        {
            await discardAsync().WaitAsync(timeout);
        }
        catch (Exception)
        {
            // Best effort only; exact native abort still releases the queue.
        }

        onResolved(rendererAvailable
            ? DictionaryPopupCommitResolution.Aborted
            : DictionaryPopupCommitResolution.RendererUnavailable);
    }
}
