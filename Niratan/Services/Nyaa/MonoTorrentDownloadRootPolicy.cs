using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Settings;

namespace Niratan.Services.Nyaa;

public enum MonoTorrentDownloadRootIssue
{
    NotAbsolute,
    CreateFailed,
    NotWritable,
}

/// <summary>
/// Resolves and validates the root used by new built-in torrent tasks. The
/// default stays byte-for-byte compatible with existing installations, while
/// a configured root is captured by each task and never relocates old data.
/// </summary>
public static class MonoTorrentDownloadRootPolicy
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Niratan",
        "Data",
        "TorrentDownloads");

    public static string Resolve(MonoTorrentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MonoTorrentSettings.TryNormalizeDownloadRootPath(
                   settings.DownloadRootPath,
                   out var configured)
               && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : DefaultPath;
    }

    public static bool PathsEqual(string left, string right)
    {
        if (!MonoTorrentSettings.TryNormalizeDownloadRootPath(left, out var normalizedLeft)
            || !MonoTorrentSettings.TryNormalizeDownloadRootPath(right, out var normalizedRight))
        {
            return false;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<MonoTorrentDownloadRootIssue?> CheckWritableAsync(
        string path,
        CancellationToken ct = default)
    {
        if (!MonoTorrentSettings.TryNormalizeDownloadRootPath(path, out var normalized)
            || string.IsNullOrWhiteSpace(normalized))
        {
            return MonoTorrentDownloadRootIssue.NotAbsolute;
        }

        try
        {
            Directory.CreateDirectory(normalized);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return MonoTorrentDownloadRootIssue.CreateFailed;
        }

        var probePath = Path.Combine(normalized, $".niratan-write-probe-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probePath, "niratan", ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return MonoTorrentDownloadRootIssue.NotWritable;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                // A failed probe cleanup must not turn an otherwise valid root
                // into a settings failure. The uniquely named file is harmless.
            }
        }
    }
}
