using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;

namespace Niratan.Services.Video;

internal sealed class VideoArtworkCache : IVideoArtworkCache
{
    private const long CapacityBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxArtworkBytes = 20L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VideoArtworkCache()
        : this(AppDataHelper.GetVideoMetadataArtworkCachePath())
    {
    }

    internal VideoArtworkCache(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<VideoArtworkCacheEntry?> GetAsync(string url, CancellationToken ct = default)
    {
        ValidateUrl(url);
        await _gate.WaitAsync(ct);
        try
        {
            var key = GetKey(url);
            var metadataPath = Path.Combine(_root, key + ".json");
            if (!File.Exists(metadataPath))
                return null;
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(
                await File.ReadAllTextAsync(metadataPath, ct),
                JsonOptions);
            if (metadata == null || metadata.Url != url || !File.Exists(metadata.LocalPath))
                return null;
            metadata = metadata with { LastAccessedAt = DateTimeOffset.UtcNow };
            await WriteMetadataAtomicAsync(metadataPath, metadata, ct);
            return metadata.ToEntry();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoArtworkCacheEntry> StoreAsync(
        string url,
        Stream content,
        string? contentType,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct = default)
    {
        ValidateUrl(url);
        ArgumentNullException.ThrowIfNull(content);
        await _gate.WaitAsync(ct);
        try
        {
            var key = GetKey(url);
            var tempPath = Path.Combine(_root, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(
                                 tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[16 * 1024];
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;
                        if (output.Length + read > MaxArtworkBytes)
                            throw new InvalidDataException("Artwork exceeds the 20 MiB cache limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await output.FlushAsync(ct);
                }
                var extension = DetectImageExtension(tempPath, contentType);
                var finalPath = Path.Combine(_root, key + extension);
                ReplaceAtomic(tempPath, finalPath);
                var info = new FileInfo(finalPath);
                var metadata = new CacheMetadata(
                    url, finalPath, etag, lastModified, info.Length, DateTimeOffset.UtcNow);
                await WriteMetadataAtomicAsync(Path.Combine(_root, key + ".json"), metadata, ct);
                await TrimCoreAsync(ct);
                return metadata.ToEntry();
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrimAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await TrimCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TrimCoreAsync(CancellationToken ct)
    {
        var entries = new List<(string MetadataPath, CacheMetadata Metadata)>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var metadata = JsonSerializer.Deserialize<CacheMetadata>(
                    await File.ReadAllTextAsync(path, ct), JsonOptions);
                if (metadata != null && File.Exists(metadata.LocalPath))
                    entries.Add((path, metadata));
            }
            catch (JsonException)
            {
            }
        }
        var total = entries.Sum(entry => entry.Metadata.Size);
        foreach (var entry in entries.OrderBy(entry => entry.Metadata.LastAccessedAt))
        {
            if (total <= CapacityBytes)
                break;
            if (File.Exists(entry.Metadata.LocalPath))
                File.Delete(entry.Metadata.LocalPath);
            if (File.Exists(entry.MetadataPath))
                File.Delete(entry.MetadataPath);
            total -= entry.Metadata.Size;
        }
    }

    private static string DetectImageExtension(string path, string? contentType)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff)
            return ".jpg";
        if (read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ".png";
        if (read >= 12
            && Encoding.ASCII.GetString(header[..4]) == "RIFF"
            && Encoding.ASCII.GetString(header[8..12]) == "WEBP")
            return ".webp";
        throw new InvalidDataException($"Artwork response is not a supported image ({contentType ?? "unknown"}).");
    }

    private static async Task WriteMetadataAtomicAsync(
        string path,
        CacheMetadata metadata,
        CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(metadata, JsonOptions), ct);
            ReplaceAtomic(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void ReplaceAtomic(string temp, string destination)
    {
        if (File.Exists(destination))
        {
            var backup = destination + "." + Guid.NewGuid().ToString("N") + ".backup.tmp";
            try
            {
                File.Replace(temp, destination, backup, true);
            }
            finally
            {
                if (File.Exists(backup))
                    File.Delete(backup);
            }
        }
        else
        {
            File.Move(temp, destination);
        }
    }

    private static string GetKey(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Artwork URL must be absolute HTTPS.", nameof(url));
    }

    private sealed record CacheMetadata(
        string Url,
        string LocalPath,
        string? ETag,
        DateTimeOffset? LastModified,
        long Size,
        DateTimeOffset LastAccessedAt)
    {
        public VideoArtworkCacheEntry ToEntry() =>
            new(LocalPath, Url, ETag, LastModified, Size, LastAccessedAt);
    }
}
