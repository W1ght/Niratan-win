using FluentAssertions;

namespace Hoshi.Tests.Build;

public sealed class DevelopmentScriptAssetTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void BuildProjects_PrepareNativeDllBeforeCopyingIt()
    {
        Project("Hoshi", "Hoshi.csproj")
            .Should().Contain("Ensure-NativeHoshidicts.ps1");
        Project("Hoshi.Tests", "Hoshi.Tests.csproj")
            .Should().Contain("Ensure-NativeHoshidicts.ps1");
    }

    [Fact]
    public void NativePreparation_UsesGitCommonDirectoryBeforeBuildingLocally()
    {
        var scriptPath = Path.Combine(
            RepositoryRoot,
            "scripts",
            "Ensure-NativeHoshidicts.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);
        script.Should().Contain("rev-parse --path-format=absolute --git-common-dir");
        script.Should().Contain("native\\out\\hoshidicts_c_api.dll");
        script.Should().Contain("build-native.ps1");
        script.Should().Contain("Push-Location $RepositoryRoot");
    }

    [Fact]
    public void BuildAndRun_AnchorsBuildAndLaunchToItsOwnWorktree()
    {
        var script = RootFile("build-and-run.ps1");

        script.Should().Contain("$PSScriptRoot");
        script.Should().Contain("Hoshi\\Hoshi.csproj");
        script.Should().Contain("Start-Process -FilePath $executable");
        script.Should().Contain("MainWindowHandle");
        script.Should().Contain("$process.Path");
    }

    private static string Project(string directory, string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, directory, fileName));

    private static string RootFile(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, fileName));
}
