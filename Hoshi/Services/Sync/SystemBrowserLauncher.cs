using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Hoshi.Services.Sync;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public Task LaunchAsync(Uri uri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ct.ThrowIfCancellationRequested();

        Process.Start(new ProcessStartInfo(uri.ToString())
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}
