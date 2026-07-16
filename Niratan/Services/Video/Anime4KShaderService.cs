using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Settings;

namespace Niratan.Services.Video;

internal sealed class Anime4KShaderService : IAnime4KShaderService
{
    internal const string ReleaseTag = "v4.0.1";
    private const int MaximumShaderBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly IReadOnlyDictionary<string, ShaderFileDefinition> Files =
        new Dictionary<string, ShaderFileDefinition>(StringComparer.Ordinal)
        {
            ["Anime4K_Clamp_Highlights.glsl"] = new(
                "glsl/Restore/Anime4K_Clamp_Highlights.glsl",
                "A2A9BF7FBC1D75D09660CA2E701E4D7FB0CF5457B94DA47E1825032FA2B3671A"),
            ["Anime4K_Restore_CNN_M.glsl"] = new(
                "glsl/Restore/Anime4K_Restore_CNN_M.glsl",
                "67EA3ED26539E8DE3B7D307688535D2FF17E8D147E11DDA0247DA7770DBECF41"),
            ["Anime4K_Upscale_CNN_x2_M.glsl"] = new(
                "glsl/Upscale/Anime4K_Upscale_CNN_x2_M.glsl",
                "716E02098A68F0D648761F2B96B4DD139E1CB09B174BB369FCA3AA34328FFF7E"),
            ["Anime4K_AutoDownscalePre_x2.glsl"] = new(
                "glsl/Upscale/Anime4K_AutoDownscalePre_x2.glsl",
                "8C58291740146BD766A4D73F132775A797FE80F7D07919B5D767E27A5DC85656"),
            ["Anime4K_AutoDownscalePre_x4.glsl"] = new(
                "glsl/Upscale/Anime4K_AutoDownscalePre_x4.glsl",
                "5AF62D8CD844916DC1126613E13BAD3BEAB195787F93A71200B47C6EC78F2E41"),
            ["Anime4K_Upscale_CNN_x2_S.glsl"] = new(
                "glsl/Upscale/Anime4K_Upscale_CNN_x2_S.glsl",
                "4C53EC2E287908F7EE7BCB266B0170421626D663576468B7D7DAFC62962649A4"),
            ["Anime4K_Restore_CNN_VL.glsl"] = new(
                "glsl/Restore/Anime4K_Restore_CNN_VL.glsl",
                "35036722733305CD4D4E57660B883BBE2569BA2914033C254327107D7B77E35E"),
            ["Anime4K_Upscale_CNN_x2_VL.glsl"] = new(
                "glsl/Upscale/Anime4K_Upscale_CNN_x2_VL.glsl",
                "5638FE31C37C151A3443FEA3451A3EF91AF073F4DBB9615F6C0D1E29DB11493D"),
        };

    private static readonly IReadOnlyDictionary<VideoShaderPreset, IReadOnlyList<string>> Presets =
        new Dictionary<VideoShaderPreset, IReadOnlyList<string>>
        {
            [VideoShaderPreset.Off] = Array.Empty<string>(),
            [VideoShaderPreset.Anime4KFast] =
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl",
            ],
            [VideoShaderPreset.Anime4KHighQuality] =
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
            ],
        };

    private readonly HttpClient _httpClient;
    private readonly string _shaderDirectory;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public Anime4KShaderService(HttpClient httpClient)
        : this(
            httpClient,
            Path.Combine(
                AppDataHelper.GetAppDataPath(),
                "VideoShaders",
                "Anime4K",
                ReleaseTag))
    {
    }

    internal Anime4KShaderService(HttpClient httpClient, string shaderDirectory)
    {
        _httpClient = httpClient;
        _shaderDirectory = Path.GetFullPath(shaderDirectory);
    }

    public async Task<Anime4KInstallResult> EnsurePresetAvailableAsync(
        VideoShaderPreset preset,
        IProgress<Anime4KDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var fileNames = GetPresetFileNames(preset);
        if (fileNames.Count == 0)
            return new Anime4KInstallResult(true, 0, 0);

        await _downloadGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_shaderDirectory);
            var downloaded = 0;
            for (var index = 0; index < fileNames.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = fileNames[index];
                var definition = Files[fileName];
                var destination = GetDestinationPath(fileName);
                if (!IsValidInstalledFile(destination, definition.Sha256))
                {
                    var error = await DownloadFileAsync(definition, destination, ct);
                    if (error != null)
                    {
                        return new Anime4KInstallResult(
                            false,
                            downloaded,
                            fileNames.Count,
                            error);
                    }

                    downloaded++;
                }

                progress?.Report(new Anime4KDownloadProgress(
                    index + 1,
                    fileNames.Count,
                    fileName));
            }

            return new Anime4KInstallResult(true, downloaded, fileNames.Count);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    public IReadOnlyList<string> GetInstalledShaderPaths(VideoShaderPreset preset)
    {
        var fileNames = GetPresetFileNames(preset);
        if (fileNames.Count == 0)
            return Array.Empty<string>();

        var paths = new List<string>(fileNames.Count);
        foreach (var fileName in fileNames)
        {
            var path = GetDestinationPath(fileName);
            if (!IsValidInstalledFile(path, Files[fileName].Sha256))
                return Array.Empty<string>();

            paths.Add(path);
        }

        return paths;
    }

    internal static IReadOnlyList<string> GetPresetFileNames(VideoShaderPreset preset) =>
        Presets.TryGetValue(preset, out var files) ? files : Array.Empty<string>();

    internal static IReadOnlyList<Uri> BuildDownloadUris(string repoPath) =>
    [
        new($"https://raw.githubusercontent.com/bloc97/Anime4K/{ReleaseTag}/{repoPath}"),
        new($"https://cdn.jsdelivr.net/gh/bloc97/Anime4K@{ReleaseTag}/{repoPath}"),
        new($"https://fastly.jsdelivr.net/gh/bloc97/Anime4K@{ReleaseTag}/{repoPath}"),
    ];

    private async Task<string?> DownloadFileAsync(
        ShaderFileDefinition definition,
        string destination,
        CancellationToken ct)
    {
        string? lastError = null;
        foreach (var uri in BuildDownloadUris(definition.RepoPath))
        {
            try
            {
                using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sourceCts.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Niratan/Anime4K-Downloader");
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    sourceCts.Token);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is > MaximumShaderBytes)
                    throw new InvalidDataException("The shader response is too large.");

                var bytes = await response.Content.ReadAsByteArrayAsync(sourceCts.Token);
                ValidateDownloadedShader(bytes, definition.Sha256);
                await WriteAtomicallyAsync(destination, bytes, ct);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"Timed out while downloading from {uri.Host}.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return $"Unable to download {Path.GetFileName(destination)}: {lastError}";
    }

    internal static void ValidateDownloadedShader(byte[] bytes, string expectedSha256)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumShaderBytes)
            throw new InvalidDataException("The shader response has an invalid size.");

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded shader failed its SHA-256 check.");

        var text = StrictUtf8.GetString(bytes);
        if (!text.Contains("//!HOOK", StringComparison.Ordinal)
            || !text.Contains("//!BIND", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded file is not an mpv GLSL hook.");
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        byte[] bytes,
        CancellationToken ct)
    {
        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsValidInstalledFile(string path, string expectedSha256)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var bytes = File.ReadAllBytes(path);
            ValidateDownloadedShader(bytes, expectedSha256);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetDestinationPath(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(_shaderDirectory, fileName));
        var root = _shaderDirectory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shader path escaped the managed directory.");

        return path;
    }

    private sealed record ShaderFileDefinition(string RepoPath, string Sha256);
}
