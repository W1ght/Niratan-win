using System;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Updates;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken ct = default
    );

    Task<AppUpdatePackage> DownloadUpdateAsync(
        AppUpdateCheckResult update,
        string destinationDirectory,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken ct = default
    );
}

public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    Uri ReleasePageUri,
    AppUpdateAsset? InstallerAsset,
    bool IsUpdateAvailable
);

public sealed record AppUpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256
);

public sealed record AppUpdatePackage(
    string Version,
    string InstallerPath
);

public sealed record AppUpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes
)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}
