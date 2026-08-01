using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class AppUpdateSettingsTests
{
    [Fact]
    public void DefaultDownloadDirectory_UsesNiratanFolderUnderDownloads()
    {
        var result = new AppUpdateSettings().ResolveDownloadDirectory();

        result.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Niratan"));
    }

    [Fact]
    public void ResolveDownloadDirectory_UsesConfiguredAbsolutePath()
    {
        var configured = Path.Combine(Path.GetTempPath(), "custom-update-folder");
        var settings = new AppUpdateSettings { DownloadDirectory = configured };

        settings.ResolveDownloadDirectory().Should().Be(Path.GetFullPath(configured));
    }

    [Fact]
    public void ResolveDownloadDirectory_MigratesLegacyDefaultFolder()
    {
        var settings = new AppUpdateSettings
        {
            DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "Hoshi"),
        };

        settings.ResolveDownloadDirectory().Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Niratan"));
    }
}
