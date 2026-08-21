using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Niratan.Helpers;

namespace Niratan.Services.Games;

internal sealed class GalGameHookRuntimeStage
{
    public const string InstallDirectoryName = "voice_hook";
    public const string StageRootName = "voice_hook_runtime";

    private static readonly IReadOnlyDictionary<string, string[]> FilesByArchitecture =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x86"] =
            [
                "fushi_voice_injector.exe",
                "fushi_voice_hook.dll",
                "LunaHook32.dll",
                "LunaHost32.dll",
                "LoaderDll.dll",
                "LocaleEmulator.dll",
            ],
            ["x64"] =
            [
                "fushi_voice_injector.exe",
                "fushi_voice_hook.dll",
                "LunaHook64.dll",
                "LunaHost64.dll",
            ],
        };

    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> EnsureStagedAsync(string architecture) =>
        _inFlight.GetOrAdd(architecture, StageCoreAsync);

    public string? GetUnityRuntimeDirectory(string architecture)
    {
        var path = Path.Combine(SourceDirectory(architecture), "unity_audio_runtime");
        return Directory.Exists(path) ? path : null;
    }

    private async Task<string?> StageCoreAsync(string architecture)
    {
        try
        {
            if (!OperatingSystem.IsWindows()
                || !FilesByArchitecture.TryGetValue(architecture, out var files))
                return null;

            var sourceDirectory = SourceDirectory(architecture);
            var sourceFiles = files.Select(file => new FileInfo(Path.Combine(sourceDirectory, file))).ToArray();
            if (sourceFiles.Any(file => !file.Exists))
                return null;

            var version = await ContentVersionAsync(sourceFiles);
            var stageRoot = Path.Combine(AppDataHelper.GetGameDataPath(), StageRootName);
            var target = Path.Combine(stageRoot, version, architecture);
            Directory.CreateDirectory(target);

            for (var i = 0; i < files.Length; i++)
            {
                var destination = new FileInfo(Path.Combine(target, files[i]));
                if (!destination.Exists || destination.Length != sourceFiles[i].Length)
                    sourceFiles[i].CopyTo(destination.FullName, true);
            }

            _ = Task.Run(() => PruneStaleVersions(stageRoot, Path.Combine(stageRoot, version)));
            var injector = Path.Combine(target, "fushi_voice_injector.exe");
            return File.Exists(injector) ? injector : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(architecture, out _);
        }
    }

    private static string SourceDirectory(string architecture) =>
        Path.Combine(AppContext.BaseDirectory, InstallDirectoryName, architecture);

    private static async Task<string> ContentVersionAsync(IEnumerable<FileInfo> files)
    {
        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            await using var stream = file.OpenRead();
            var hash = await SHA256.HashDataAsync(stream);
            combined.AppendData(hash);
        }
        return Convert.ToHexString(combined.GetHashAndReset())[..16].ToLowerInvariant();
    }

    private static void PruneStaleVersions(string root, string keep)
    {
        try
        {
            if (!Directory.Exists(root))
                return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(directory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
