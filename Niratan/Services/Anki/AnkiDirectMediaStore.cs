using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Niratan.Services.Anki;

internal static class AnkiDirectMediaStore
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> s_inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<string?> WriteBytesAsync(
        string mediaDirectory,
        string filename,
        byte[] data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            return Task.FromResult<string?>(null);

        return GenerateAsync(
            mediaDirectory,
            filename,
            async (tempPath, producerToken) =>
            {
                await File.WriteAllBytesAsync(tempPath, data, producerToken).ConfigureAwait(false);
                return tempPath;
            },
            ct);
    }

    public static async Task<string?> GenerateAsync(
        string mediaDirectory,
        string filename,
        Func<string, CancellationToken, Task<string?>> producer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ct.ThrowIfCancellationRequested();

        if (!TryResolveDestination(mediaDirectory, filename, out var safeFilename, out var destination))
            return null;
        if (HasOutput(destination))
            return safeFilename;

        var candidate = new Lazy<Task<string?>>(
            () => ProduceAndPublishAsync(destination, safeFilename, producer),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = s_inFlight.GetOrAdd(destination, candidate);
        if (ReferenceEquals(candidate, pending))
            _ = RemoveCompletedGenerationAsync(destination, pending);

        return await pending.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ProduceAndPublishAsync(
        string destination,
        string safeFilename,
        Func<string, CancellationToken, Task<string?>> producer)
    {
        if (HasOutput(destination))
            return safeFilename;

        var directory = Path.GetDirectoryName(destination)!;
        var extension = Path.GetExtension(safeFilename);
        var stem = Path.GetFileNameWithoutExtension(safeFilename);
        var tempPath = Path.Combine(
            directory,
            $".{stem}.{Guid.NewGuid():N}.tmp{extension}");

        try
        {
            Directory.CreateDirectory(directory);
            var producedPath = await producer(tempPath, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(producedPath)
                || !PathEquals(producedPath, tempPath)
                || !HasOutput(tempPath))
            {
                return null;
            }

            if (HasOutput(destination))
                return safeFilename;

            try
            {
                if (File.Exists(destination))
                    File.Move(tempPath, destination, overwrite: true);
                else
                    File.Move(tempPath, destination);
            }
            catch (IOException) when (HasOutput(destination))
            {
                // Another process published the same deterministic media first.
            }

            return HasOutput(destination) ? safeFilename : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Anki] Failed to publish direct media {Filename}", safeFilename);
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task RemoveCompletedGenerationAsync(
        string destination,
        Lazy<Task<string?>> pending)
    {
        try
        {
            await pending.Value.ConfigureAwait(false);
        }
        catch
        {
            // The caller observes the task result; this continuation only owns cleanup.
        }
        finally
        {
            s_inFlight.TryRemove(
                new KeyValuePair<string, Lazy<Task<string?>>>(destination, pending));
        }
    }

    private static bool TryResolveDestination(
        string mediaDirectory,
        string filename,
        out string safeFilename,
        out string destination)
    {
        safeFilename = "";
        destination = "";
        if (string.IsNullOrWhiteSpace(mediaDirectory) || string.IsNullOrWhiteSpace(filename))
            return false;

        try
        {
            safeFilename = SanitizeFilename(filename);
            if (safeFilename.Length == 0)
                return false;

            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaDirectory));
            destination = Path.GetFullPath(Path.Combine(directory, safeFilename));
            return string.Equals(
                Path.GetDirectoryName(destination),
                directory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string SanitizeFilename(string filename)
    {
        var leaf = Path.GetFileName(filename.Trim());
        if (leaf is "" or "." or "..")
            return "";

        var sanitized = new string(leaf.Select(ch =>
            ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.'
                ? ch
                : '_').ToArray())
            .Trim('.', '_');
        if (sanitized.Length == 0)
            return "";

        var stem = Path.GetFileNameWithoutExtension(sanitized);
        return IsReservedWindowsDeviceName(stem)
            ? $"anki_{sanitized}"
            : sanitized;
    }

    private static bool IsReservedWindowsDeviceName(string stem)
    {
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasOutput(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
