using System;
using System.IO;

namespace Niratan.Models.Settings;

public sealed class AppUpdateSettings
{
    public string DownloadDirectory { get; set; } = GetDefaultDownloadDirectory();

    public static string GetDefaultDownloadDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads", "Hoshi");
    }

    public string ResolveDownloadDirectory() =>
        string.IsNullOrWhiteSpace(DownloadDirectory)
            ? GetDefaultDownloadDirectory()
            : Path.GetFullPath(DownloadDirectory);
}
