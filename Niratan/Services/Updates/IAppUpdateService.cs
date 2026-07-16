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
}

public sealed record AppUpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    Uri ReleasePageUri,
    bool IsUpdateAvailable
);
