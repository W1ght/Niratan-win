using System;
using System.Threading.Tasks;

namespace Niratan.Services.Dictionary;

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
            // An unavailable renderer requires a fresh document before native
            // ownership can be released safely.
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
            // Best effort only. If the renderer query was unavailable, the
            // caller will force a fresh shell before releasing ownership.
        }

        onResolved(rendererAvailable
            ? DictionaryPopupCommitResolution.Aborted
            : DictionaryPopupCommitResolution.RendererUnavailable);
    }
}
