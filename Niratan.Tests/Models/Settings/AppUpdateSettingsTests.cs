using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class AppUpdateSettingsTests
{
    [Fact]
    public void DefaultDownloadDirectory_UsesHoshiFolderUnderDownloads()
    {
        var result = new AppUpdateSettings().ResolveDownloadDirectory();

        result.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Hoshi"));
    }

    [Fact]
    public void ResolveDownloadDirectory_UsesConfiguredAbsolutePath()
    {
        var configured = Path.Combine(Path.GetTempPath(), "custom-update-folder");
        var settings = new AppUpdateSettings { DownloadDirectory = configured };

        settings.ResolveDownloadDirectory().Should().Be(Path.GetFullPath(configured));
    }
}
