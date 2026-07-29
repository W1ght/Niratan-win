using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Niratan.Services.Manga;

internal sealed class MangaOcrService : IMangaOcrService, IDisposable
{
    private const int MaximumImageDimension = 1500;
    private const int MaximumOriginalUploadBytes = 16 * 1024 * 1024;
    private const int MaximumResponseBytes = 12 * 1024 * 1024;
    private const int MaximumCachedPageBytes = 32 * 1024 * 1024;
    private const int MaximumRegionsPerPage = 100_000;
    private const int MaximumMemoryPages = 24;
    private const int CacheSchemaVersion = 4;
    private const string EngineSignature = "google-lens-v3-ja-niratan-layout";
    private const string ChromiumApiKey = "AIzaSyDr2UxVnv_U85AbhhY8XSHSIavUW0DC-sY";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly Uri Endpoint =
        new("https://lensfrontend-pa.googleapis.com/v1/crupload");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly ILogger<MangaOcrService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<MangaOcrCacheKey, IReadOnlyList<MangaTextRegion>> _memory = [];
    private readonly LinkedList<MangaOcrCacheKey> _memoryOrder = [];

    public MangaOcrService(ILogger<MangaOcrService> logger)
        : this(AppDataHelper.GetMangaOcrCachePath(), logger)
    {
    }

    internal MangaOcrService(
        string cacheRoot,
        ILogger<MangaOcrService> logger,
        HttpMessageHandler? handler = null)
    {
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _logger = logger;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<IReadOnlyList<MangaTextRegion>?> GetCachedRegionsAsync(
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        CancellationToken ct = default)
    {
        if (!IsMatchingKey(key, pageIdentities))
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            if (_memory.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }

            var directory = await PrepareCacheAsync(key, pageIdentities, ct);
            var pagePath = Path.Combine(directory, $"{key.PageIndex:D6}.json");
            if (!File.Exists(pagePath)
                || new FileInfo(pagePath).Length > MaximumCachedPageBytes)
            {
                return null;
            }

            await using var stream = new FileStream(
                pagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                true);
            var regions = await JsonSerializer.DeserializeAsync<List<MangaTextRegion>>(
                stream,
                JsonOptions,
                ct);
            if (regions is null || !IsValid(regions, key.PageIndex))
                return null;
            var normalizedRegions = MangaOcrLayout.MergeAdjacentTextBlocks(regions);
            StoreMemory(key, normalizedRegions);
            return normalizedRegions;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MangaTextRegion>> RecognizeAsync(
        string imagePath,
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        CancellationToken ct = default)
    {
        var cached = await GetCachedRegionsAsync(key, pageIdentities, ct);
        if (cached is not null)
            return cached;

        var prepared = await PrepareImageAsync(imagePath, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", ChromiumApiKey);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Content = new ByteArrayContent(MangaGoogleLensProtocol.MakeRequest(
            prepared.Data,
            prepared.Width,
            prepared.Height));
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Google Lens text recognition returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Google Lens response exceeded the safe size limit.");

        var responseBytes = await ReadLimitedAsync(
            await response.Content.ReadAsStreamAsync(ct),
            MaximumResponseBytes,
            ct);
        var regions = MangaGoogleLensProtocol.DecodeResponse(
            responseBytes,
            key.PageIndex);
        if (!IsValid(regions, key.PageIndex))
            throw new InvalidDataException("Google Lens returned invalid text regions.");
        await StoreAsync(key, pageIdentities, regions, ct);
        return regions;
    }

    private async Task StoreAsync(
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        IReadOnlyList<MangaTextRegion> regions,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            StoreMemory(key, regions);
            var directory = await PrepareCacheAsync(key, pageIdentities, ct);
            var target = Path.Combine(directory, $"{key.PageIndex:D6}.json");
            var temp = target + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    true))
                {
                    await JsonSerializer.SerializeAsync(stream, regions, JsonOptions, ct);
                }
                if (new FileInfo(temp).Length <= MaximumCachedPageBytes)
                    File.Move(temp, target, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> PrepareCacheAsync(
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheRoot);
        var directory = Path.Combine(_cacheRoot, SafeCacheName(key.ItemId));
        var manifestPath = Path.Combine(directory, "manifest.json");
        var expected = new CacheManifest(
            CacheSchemaVersion,
            EngineSignature,
            key.ItemId,
            key.ModifiedAt,
            pageIdentities.ToArray());
        CacheManifest? existing = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                await using var input = File.OpenRead(manifestPath);
                existing = await JsonSerializer.DeserializeAsync<CacheManifest>(
                    input,
                    JsonOptions,
                    ct);
            }
            catch (JsonException)
            {
            }
        }

        if (!CacheManifestMatches(existing, expected))
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            foreach (var memoryKey in _memory.Keys
                         .Where(candidate => candidate.ItemId == key.ItemId)
                         .ToList())
            {
                _memory.Remove(memoryKey);
                _memoryOrder.Remove(memoryKey);
            }
        }

        Directory.CreateDirectory(directory);
        if (!CacheManifestMatches(existing, expected))
        {
            var temp = manifestPath + $".{Guid.NewGuid():N}.tmp";
            await using (var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                true))
            {
                await JsonSerializer.SerializeAsync(output, expected, JsonOptions, ct);
            }
            File.Move(temp, manifestPath, true);
        }
        return directory;
    }

    private static async Task<PreparedImage> PrepareImageAsync(
        string imagePath,
        CancellationToken ct)
    {
        var sourceBytes = await File.ReadAllBytesAsync(imagePath, ct);
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(sourceBytes);
            await writer.StoreAsync().AsTask(ct);
            writer.DetachStream();
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct);
        var scale = Math.Min(
            1d,
            (double)MaximumImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        if (scale >= 1
            && sourceBytes.Length <= MaximumOriginalUploadBytes)
        {
            return new PreparedImage(
                sourceBytes,
                checked((int)decoder.PixelWidth),
                checked((int)decoder.PixelHeight));
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(ct);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId,
            output).AsTask(ct);
        encoder.SetSoftwareBitmap(bitmap);
        encoder.BitmapTransform.ScaledWidth = width;
        encoder.BitmapTransform.ScaledHeight = height;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        encoder.IsThumbnailGenerated = false;
        await encoder.FlushAsync().AsTask(ct);
        output.Seek(0);
        var bytes = new byte[checked((int)output.Size)];
        using (var reader = new DataReader(output))
        {
            await reader.LoadAsync((uint)bytes.Length).AsTask(ct);
            reader.ReadBytes(bytes);
        }
        return new PreparedImage(bytes, checked((int)width), checked((int)height));
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Google Lens response exceeded the safe size limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool IsMatchingKey(
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities) =>
        key.PageIndex >= 0
        && key.PageIndex < pageIdentities.Count
        && pageIdentities[key.PageIndex] == key.PageIdentity;

    private static bool IsValid(
        IReadOnlyList<MangaTextRegion> regions,
        int pageIndex) =>
        regions.Count <= MaximumRegionsPerPage
        && regions.All(region =>
            region.PageIndex == pageIndex
            && region.Utf16Offset >= 0
            && region.Utf16Offset <= region.Sentence.Length
            && double.IsFinite(region.X)
            && double.IsFinite(region.Y)
            && double.IsFinite(region.Width)
            && double.IsFinite(region.Height)
            && region.Width >= 0
            && region.Height >= 0);

    private void StoreMemory(
        MangaOcrCacheKey key,
        IReadOnlyList<MangaTextRegion> regions)
    {
        _memory[key] = regions;
        Touch(key);
        while (_memoryOrder.Count > MaximumMemoryPages)
        {
            var oldest = _memoryOrder.First!.Value;
            _memoryOrder.RemoveFirst();
            _memory.Remove(oldest);
        }
    }

    private void Touch(MangaOcrCacheKey key)
    {
        _memoryOrder.Remove(key);
        _memoryOrder.AddLast(key);
    }

    private static string SafeCacheName(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool CacheManifestMatches(
        CacheManifest? actual,
        CacheManifest expected) =>
        actual is not null
        && actual.SchemaVersion == expected.SchemaVersion
        && actual.EngineSignature == expected.EngineSignature
        && actual.ItemId == expected.ItemId
        && Nullable.Equals(actual.ModifiedAt, expected.ModifiedAt)
        && actual.PageIdentities.SequenceEqual(expected.PageIdentities);

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
    }

    private sealed record CacheManifest(
        int SchemaVersion,
        string EngineSignature,
        string ItemId,
        DateTimeOffset? ModifiedAt,
        string[] PageIdentities);

    private sealed record PreparedImage(byte[] Data, int Width, int Height);
}
