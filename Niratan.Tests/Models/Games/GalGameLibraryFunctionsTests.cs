using FluentAssertions;
using Niratan.Models.Games;

namespace Niratan.Tests.Models.Games;

public sealed class GalGameLibraryFunctionsTests
{
    [Fact]
    public void ParseLaunchArguments_PreservesQuotedSpacesAndWindowsBackslashes()
    {
        var result = GalGameLibraryFunctions.ParseLaunchArguments(
            "-windowed --save=\"C:\\Program Files\\save\" \"quoted value\" \\\"literal-quote");

        result.Should().Equal(
            "-windowed",
            "--save=C:\\Program Files\\save",
            "quoted value",
            "\"literal-quote");
    }

    [Fact]
    public void FilterNewExes_IsCaseInsensitiveAndDoesNotReorderInput()
    {
        var existing = new[]
        {
            GalGameLibraryFunctions.NewFromExe("D:/Games/Already.exe"),
        };

        var result = GalGameLibraryFunctions.FilterNewExes(existing, new[]
        {
            "D:\\Games\\already.EXE",
            "D:/Games/New.exe",
            "D:/Games/New.exe",
            "D:/Games/readme.txt",
        });

        result.Should().Equal("D:/Games/New.exe");
    }

    [Fact]
    public void NewFromExe_UsesExeStemAndContainingDirectory()
    {
        var result = GalGameLibraryFunctions.NewFromExe(
            "D:/Games/My Game/game.exe",
            new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));

        result.Name.Should().Be("game");
        result.ExePath.Should().EndWith("Games\\My Game\\game.exe");
        result.Workdir.Should().EndWith("Games\\My Game");
        result.DisplayName.Should().Be("game");
    }
}
