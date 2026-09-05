using System;
using System.Collections.Generic;
using System.IO;

namespace Niratan.Models.Settings;

public sealed class MonoTorrentSettings
{
    public const int MaximumAdditionalTrackerCount = 32;
    public const int MaximumTrackerUrlLength = 2048;

    /// <summary>
    /// User-selected root for new built-in downloads. An empty value keeps the
    /// application default under roaming app data for backward compatibility.
    /// </summary>
    public string DownloadRootPath { get; set; } = "";
    public List<string> AdditionalTrackers { get; set; } = [];
    public int ListenPort { get; set; }
    public bool EnablePortForwarding { get; set; } = true;
    public bool EnableDht { get; set; } = true;
    public bool EnablePeerExchange { get; set; } = true;
    public bool EnableLocalPeerDiscovery { get; set; } = true;
    public int MaximumConnections { get; set; } = 120;
    public int MaximumConnectionsPerTorrent { get; set; } = 80;
    public int MaximumHalfOpenConnections { get; set; } = 20;
    public int MaximumOpenFiles { get; set; } = 96;
    public int DownloadRateLimitKiB { get; set; }
    public int UploadRateLimitKiB { get; set; } = 2048;
    public int UploadSlotsPerTorrent { get; set; } = 8;

    public MonoTorrentSettings Normalize()
    {
        var maximumConnections = Math.Clamp(MaximumConnections, 1, 2000);
        return new MonoTorrentSettings
        {
            DownloadRootPath = NormalizeDownloadRootPath(DownloadRootPath),
            AdditionalTrackers = NormalizeAdditionalTrackers(AdditionalTrackers),
            ListenPort = Math.Clamp(ListenPort, 0, 65535),
            EnablePortForwarding = EnablePortForwarding,
            EnableDht = EnableDht,
            EnablePeerExchange = EnablePeerExchange,
            EnableLocalPeerDiscovery = EnableLocalPeerDiscovery,
            MaximumConnections = maximumConnections,
            MaximumConnectionsPerTorrent = Math.Clamp(
                MaximumConnectionsPerTorrent,
                1,
                maximumConnections),
            MaximumHalfOpenConnections = Math.Clamp(MaximumHalfOpenConnections, 1, 256),
            MaximumOpenFiles = Math.Clamp(MaximumOpenFiles, 1, 1024),
            DownloadRateLimitKiB = Math.Clamp(DownloadRateLimitKiB, 0, 1_000_000),
            UploadRateLimitKiB = Math.Clamp(UploadRateLimitKiB, 0, 1_000_000),
            UploadSlotsPerTorrent = Math.Clamp(UploadSlotsPerTorrent, 1, 128),
        };
    }

    public static bool TryNormalizeDownloadRootPath(string? value, out string normalized)
    {
        normalized = "";
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return true;

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
                return false;

            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryNormalizeTrackerUrl(string? value, out string normalized)
    {
        normalized = "";
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaximumTrackerUrlLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !IsSupportedTrackerScheme(uri.Scheme)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static List<string> NormalizeAdditionalTrackers(IEnumerable<string>? trackers)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (trackers is null)
            return normalized;

        foreach (var tracker in trackers)
        {
            if (normalized.Count >= MaximumAdditionalTrackerCount)
                break;
            if (TryNormalizeTrackerUrl(tracker, out var value) && seen.Add(value))
                normalized.Add(value);
        }

        return normalized;
    }

    private static string NormalizeDownloadRootPath(string? value) =>
        TryNormalizeDownloadRootPath(value, out var normalized) ? normalized : "";

    private static bool IsSupportedTrackerScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("udp", StringComparison.OrdinalIgnoreCase);
}
