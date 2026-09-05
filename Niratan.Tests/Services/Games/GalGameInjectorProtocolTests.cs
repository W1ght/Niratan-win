using FluentAssertions;
using System.Diagnostics;
using Niratan.Models.Games;
using Niratan.Services.Games;

namespace Niratan.Tests.Services.Games;

public sealed class GalGameInjectorProtocolTests
{
    [Fact]
    public async Task ReadyProof_UsesOkHookedInsteadOfEarlyLaunchLine()
    {
        var protocol = new GalGameInjectorProtocol();

        protocol.ObserveStandardOutput("LAUNCH pid=4123 arch=x86");

        protocol.LaunchedProcessId.Should().Be(4123);
        protocol.HookedProcessId.IsCompleted.Should().BeFalse();

        protocol.ObserveStandardOutput(
            "OK hooked pid=4123 hooked=1 ring=1024 sr=0 ch=0 bits=0 float=0");

        (await protocol.HookedProcessId).Should().Be(4123);
    }

    [Fact]
    public void StructuredFailure_IsRetainedWithNativeEvidence()
    {
        var protocol = new GalGameInjectorProtocol();

        protocol.ObserveStandardError("OpenProcess(55) failed: 5");
        protocol.ObserveStandardError("ERR reason=accessDenied exit=1");

        protocol.FailureToken.Should().Be("accessDenied");
        protocol.DiagnosticTail.Should().Contain("OpenProcess(55)");
        protocol.DiagnosticTail.Should().EndWith("ERR reason=accessDenied exit=1");
    }

    [Fact]
    public void LaunchArguments_UseOneReadyBudgetAndExplicitFailClosedPolicy()
    {
        var game = GalGameLibraryFunctions.NewFromExe("D:/Games/demo.exe") with
        {
            LaunchArgs = "-windowed \"save folder\"",
        };

        var arguments = GalGameSessionService.BuildInjectorArguments(
            game.ExePath,
            game,
            null,
            "D:/runtime");

        arguments.Should().ContainInOrder("--launch", game.ExePath, "--hold");
        arguments.Should().ContainInOrder("--wait-ms", "30000");
        arguments.Should().ContainInOrder("--native-loopback-policy", "deny");
        arguments.Should().ContainInOrder("--arg", "-windowed");
        arguments.Should().ContainInOrder("--arg", "save folder");
    }

    [Fact]
    public void AttachArguments_KeepTheSameHandshakeContract()
    {
        var arguments = GalGameSessionService.BuildInjectorArguments(
            null,
            null,
            9876,
            null);

        arguments.Should().ContainInOrder("--pid", "9876", "--hold");
        arguments.Should().ContainInOrder("--wait-ms", "30000");
        arguments.Should().ContainInOrder("--native-loopback-policy", "deny");
    }

    [Fact]
    public async Task WaitForReady_ReturnsAsSoonAsHelperExits()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "exit", "7" },
        });
        process.Should().NotBeNull();
        var protocol = new GalGameInjectorProtocol();
        var stopwatch = Stopwatch.StartNew();

        var result = await GalGameInjectorProtocol.WaitForReadyAsync(
            process!,
            protocol,
            Task.CompletedTask,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result.Outcome.Should().Be(GalGameInjectorWaitOutcome.Exited);
        result.ExitCode.Should().Be(7);
    }
}
