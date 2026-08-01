using FluentAssertions;

namespace Niratan.Tests.Build;

public sealed class AppUpdatePackagingContractTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Installer_UsesNiratanDirectoryAndRootIcon()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "Packaging", "Niratan.iss"));

        script.Should().Contain(@"DefaultDirName={autopf}\Niratan");
        script.Should().Contain("UsePreviousAppDir=no");
        script.Should().Contain(@"SetupIconFile=..\Niratan.ico");

        var rootIcon = File.ReadAllBytes(Path.Combine(RepositoryRoot, "Niratan.ico"));
        var appIcon = File.ReadAllBytes(Path.Combine(
            RepositoryRoot,
            "Niratan",
            "Assets",
            "AppIcon.ico"));
        appIcon.Should().Equal(rootIcon);
    }

    [Fact]
    public void AboutSettings_OffersInAppInstallAndDownloadLocation()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Niratan",
            "Views",
            "Pages",
            "AboutSettingsPage.xaml"));

        xaml.Should().Contain("AppUpdateInstallButton");
        xaml.Should().Contain("AppUpdateChooseDownloadDirectoryButton");
        xaml.Should().Contain("UpdateDownloadProgress");
        xaml.Should().Contain("ms-appx:///Assets/AppIcon.png");
        xaml.Should().Contain("ShowAsMonochrome=\"False\"");
        xaml.Should().NotContain("AppUpdateOpenButton");
    }

    [Fact]
    public void UpdateInstaller_InstallsOverCurrentAppDirectory()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Niratan",
            "Services",
            "Updates",
            "SystemAppUpdateInstallerLauncher.cs"));

        source.Should().Contain("AppContext.BaseDirectory");
        source.Should().Contain("/CLOSEAPPLICATIONS");
        source.Should().Contain("/RESTARTAPPLICATIONS");
        source.Should().Contain("/DIR=");
    }
}
