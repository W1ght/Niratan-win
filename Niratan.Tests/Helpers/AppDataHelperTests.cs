using FluentAssertions;
using Niratan.Helpers;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Helpers;

public sealed class AppDataHelperTests
{
    [Fact]
    public void MigrateLegacyAppData_MovesDirectoryAndRenamesDatabase()
    {
        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "Hoshi");
        var currentPath = Path.Combine(temp.Path, "Niratan");
        var legacyDataPath = Path.Combine(legacyPath, "Data");
        Directory.CreateDirectory(legacyDataPath);
        File.WriteAllText(Path.Combine(legacyPath, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(legacyDataPath, "hoshi.db"), "database");

        AppDataHelper.MigrateLegacyAppData(legacyPath, currentPath);

        Directory.Exists(legacyPath).Should().BeFalse();
        File.ReadAllText(Path.Combine(currentPath, "settings.json")).Should().Be("settings");
        File.ReadAllText(Path.Combine(currentPath, "Data", "niratan.db")).Should().Be("database");
        File.Exists(Path.Combine(currentPath, "Data", "hoshi.db")).Should().BeFalse();
    }

    [Fact]
    public void MigrateLegacyAppData_DoesNotMergeIntoExistingCurrentDirectory()
    {
        using var temp = new TempDirectory();
        var legacyPath = Path.Combine(temp.Path, "Hoshi");
        var currentPath = Path.Combine(temp.Path, "Niratan");
        Directory.CreateDirectory(legacyPath);
        Directory.CreateDirectory(currentPath);
        File.WriteAllText(Path.Combine(legacyPath, "legacy.txt"), "legacy");
        File.WriteAllText(Path.Combine(currentPath, "current.txt"), "current");

        AppDataHelper.MigrateLegacyAppData(legacyPath, currentPath);

        Directory.Exists(legacyPath).Should().BeTrue();
        File.Exists(Path.Combine(currentPath, "legacy.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(currentPath, "current.txt")).Should().Be("current");
    }
}
