using System;
using System.Diagnostics;
using System.IO;

namespace Niratan.Services.Updates;

public sealed class SystemAppUpdateInstallerLauncher : IAppUpdateInstallerLauncher
{
    public void Launch(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        var fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath)
            || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("The downloaded update installer was not found.", fullPath);
        }

        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
        };
        startInfo.ArgumentList.Add("/SP-");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add("/RESTARTAPPLICATIONS");
        startInfo.ArgumentList.Add($"/DIR={installDirectory}");

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the update installer.");
    }
}
