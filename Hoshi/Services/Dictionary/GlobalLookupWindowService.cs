using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Views.Dictionary;
using Serilog;

namespace Hoshi.Services.Dictionary;

internal sealed class GlobalLookupWindowService : IGlobalLookupWindowService
{
    private GlobalLookupWindow? _window;

    public async Task OpenAsync(string? initialQuery = null, CancellationToken ct = default)
    {
        Log.Information("[GlobalLookup] Manual lookup window requested.");
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is { HasThreadAccess: false })
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await OpenOnUiThreadAsync(initialQuery, ct);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            if (!queued)
                throw new InvalidOperationException("Unable to schedule global lookup window.");

            await completion.Task;
            return;
        }

        await OpenOnUiThreadAsync(initialQuery, ct);
    }

    private async Task OpenOnUiThreadAsync(string? initialQuery, CancellationToken ct)
    {
        if (_window == null)
        {
            _window = new GlobalLookupWindow();
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();
        await _window.OpenAsync(initialQuery, ct);
    }
}
