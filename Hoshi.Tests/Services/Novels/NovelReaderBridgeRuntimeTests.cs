using System.Diagnostics;
using FluentAssertions;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelReaderBridgeRuntimeTests
{
    [Fact]
    public async Task BridgeRuntime_KeepsPaginationPrivateAndAcceptsOnlyTrustedTypedCommands()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var runtimeTest = Path.Combine(
            projectRoot,
            "Hoshi.Tests", "Web", "NovelReader", "reader-bridge.runtime.test.js");
        var bridge = Path.Combine(
            projectRoot,
            "Hoshi", "Web", "NovelReader", "reader-bridge.js");
        var node = FindNodeExecutable();

        node.Should().NotBeNull(
            "the runtime bridge regression requires Node.js; set HOSHI_NODE_PATH when node is not on PATH");

        var startInfo = new ProcessStartInfo(node!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runtimeTest);
        startInfo.ArgumentList.Add(bridge);

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        var standardOutput = process!.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await standardOutput;
        var error = await standardError;

        process.ExitCode.Should().Be(0, $"Node stdout:\n{output}\nNode stderr:\n{error}");
    }

    [Fact]
    public async Task SelectionRuntime_CachesNormalizedOffsetsUntilReaderContentChanges()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var runtimeTest = Path.Combine(
            projectRoot,
            "Hoshi.Tests", "Web", "NovelReader", "selection.runtime.test.js");
        var selection = Path.Combine(
            projectRoot,
            "Hoshi", "Web", "NovelReader", "selection.js");
        var node = FindNodeExecutable();

        node.Should().NotBeNull(
            "the selection runtime regression requires Node.js; set HOSHI_NODE_PATH when node is not on PATH");

        var startInfo = new ProcessStartInfo(node!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runtimeTest);
        startInfo.ArgumentList.Add(selection);

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        var standardOutput = process!.StandardOutput.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var output = await standardOutput;
        var error = await standardError;

        process.ExitCode.Should().Be(0, $"Node stdout:\n{output}\nNode stderr:\n{error}");
    }

    private static string? FindNodeExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HOSHI_NODE_PATH"),
            FindOnPath("node.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs", "node.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "codex-runtimes", "codex-primary-runtime",
                "dependencies", "node", "bin", "node.exe"),
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), fileName))
            .FirstOrDefault(File.Exists);
    }
}
