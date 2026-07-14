using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public sealed partial class GoogleDriveCoverCacheService : IGoogleDriveCoverCacheService
{
    private readonly HttpClient _httpClient;
    private readonly IGoogleDriveAuthService _authService;
    private readonly string _cacheRoot;

    public GoogleDriveCoverCacheService(
        HttpClient httpClient,
        IGoogleDriveAuthService authService)
        : this(httpClient, authService, AppDataHelper.GetGoogleDriveCoverCachePath())
    {
    }

    internal GoogleDriveCoverCacheService(
        HttpClient httpClient,
        IGoogleDriveAuthService authService,
        string cacheRoot)
    {
        _httpClient = httpClient;
        _authService = authService;
        _cacheRoot = cacheRoot;
    }

    public async Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (cover == null
            || string.IsNullOrWhiteSpace(cover.Id)
            || string.IsNullOrWhiteSpace(cover.ThumbnailLink)
            || !Uri.TryCreate(NormalizeThumbnailLink(cover.ThumbnailLink), UriKind.Absolute, out var uri))
        {
            return null;
        }

        Directory.CreateDirectory(_cacheRoot);
        var targetPath = Path.Combine(_cacheRoot, HashFileName(cover.Id));
        if (IsRecognizedImage(targetPath))
            return targetPath;

        TryDelete(targetPath);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var token = await _authService.GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }

            if (!IsRecognizedImage(tempPath))
                return null;

            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public Task ClearAsync(CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(_cacheRoot))
                    Directory.Delete(_cacheRoot, recursive: true);
            },
            ct);

    private static string NormalizeThumbnailLink(string link) =>
        ThumbnailSizeRegex().Replace(link, "=s768");

    private static string HashFileName(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".img";

    private static bool IsRecognizedImage(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            Span<byte> header = stackalloc byte[12];
            using var stream = File.OpenRead(path);
            var count = stream.Read(header);
            return count >= 2 && header[0] == 0xFF && header[1] == 0xD8
                || count >= 8
                    && header[0] == 0x89
                    && header[1] == 0x50
                    && header[2] == 0x4E
                    && header[3] == 0x47
                    && header[4] == 0x0D
                    && header[5] == 0x0A
                    && header[6] == 0x1A
                    && header[7] == 0x0A
                || count >= 4 && header[..4].SequenceEqual("GIF8"u8)
                || count >= 2 && header[0] == 0x42 && header[1] == 0x4D
                || count >= 12
                    && header[..4].SequenceEqual("RIFF"u8)
                    && header[8..12].SequenceEqual("WEBP"u8);
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

    [GeneratedRegex("=s\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbnailSizeRegex();
}
