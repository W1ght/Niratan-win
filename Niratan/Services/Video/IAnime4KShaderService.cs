using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Settings;

namespace Niratan.Services.Video;

public sealed record Anime4KDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    string FileName);

public sealed record Anime4KInstallResult(
    bool Success,
    int DownloadedFiles,
    int TotalFiles,
    string? ErrorMessage = null);

public interface IAnime4KShaderService
{
    Task<Anime4KInstallResult> EnsurePresetAvailableAsync(
        VideoShaderPreset preset,
        IProgress<Anime4KDownloadProgress>? progress = null,
        CancellationToken ct = default);

    IReadOnlyList<string> GetInstalledShaderPaths(VideoShaderPreset preset);
}
