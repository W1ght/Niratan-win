using System;
using System.IO;

namespace Niratan.Models.Settings;

public sealed class AppUpdateSettings
{
    public string DownloadDirectory { get; set; } = GetDefaultDownloadDirectory();

    public static string GetDefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "Niratan");
    }

    public string ResolveDownloadDirectory() =>
        string.IsNullOrWhiteSpace(DownloadDirectory)
            ? GetDefaultDownloadDirectory()
            : ResolveConfiguredDownloadDirectory(DownloadDirectory);

    private static string ResolveConfiguredDownloadDirectory(string configuredPath)
    {
        var resolved = Path.GetFullPath(configuredPath);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacyDefault = Path.Combine(userProfile, "Downloads", "Hoshi");
        return string.Equals(resolved, legacyDefault, StringComparison.OrdinalIgnoreCase)
            ? GetDefaultDownloadDirectory()
            : resolved;
    }
}
