using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Serilog;

namespace Niratan.Services.Video;

internal sealed class FfmpegVideoSubtitleTranscriptExtractor : IVideoSubtitleTranscriptExtractor
{
    private static readonly HashSet<string> SubtitleFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass",
        ".ssa",
        ".srt",
        ".vtt",
    };

    private readonly SubtitleParserService _subtitleParserService;
    private readonly Func<string?> _resolveFfmpegPath;

    public FfmpegVideoSubtitleTranscriptExtractor(SubtitleParserService subtitleParserService)
        : this(subtitleParserService, ResolveFfmpegPath)
    {
    }

    internal FfmpegVideoSubtitleTranscriptExtractor(
        SubtitleParserService subtitleParserService,
        Func<string?> resolveFfmpegPath)
    {
        _subtitleParserService = subtitleParserService;
        _resolveFfmpegPath = resolveFfmpegPath;
    }

    public async Task<IReadOnlyList<VideoSubtitleCue>> ExtractAsync(
        string videoPath,
        VideoTrackInfo track,
        CancellationToken ct = default)
    {
        if (!SupportsTextSubtitleTrack(track))
            return [];

        var externalPath = NormalizePath(track.ExternalFilename);
        if (externalPath != null && SubtitleFileExtensions.Contains(Path.GetExtension(externalPath)))
            return await ParseSubtitleFileAsync(externalPath, ct);

        var sourcePath = externalPath ?? NormalizePath(videoPath);
        if (sourcePath == null || track.FfIndex == null)
            return [];

        var ffmpegPath = _resolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return [];

        return await ExtractWithFfmpegAsync(ffmpegPath, sourcePath, track.FfIndex.Value, ct);
    }

    private async Task<IReadOnlyList<VideoSubtitleCue>> ParseSubtitleFileAsync(
        string subtitlePath,
        CancellationToken ct)
    {
        try
        {
            var document = await _subtitleParserService.ParseFileAsync(subtitlePath, ct);
            return document.Cues;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[VideoSubtitles] Could not parse subtitle track file {SubtitlePath}", subtitlePath);
            return [];
        }
    }

    private async Task<IReadOnlyList<VideoSubtitleCue>> ExtractWithFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        int streamIndex,
        CancellationToken ct)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "niratan-video-subtitles", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(tempDirectory, "track.srt");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add(FormattableString.Invariant($"0:{streamIndex}"));
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("srt");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            if (process == null)
                return [];

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                Log.Warning("[VideoSubtitles] ffmpeg subtitle extraction timed out for {SourcePath}", sourcePath);
                return [];
            }

            var stderr = await stderrTask;
            _ = await stdoutTask;
            if (process.ExitCode != 0)
            {
                Log.Warning(
                    "[VideoSubtitles] ffmpeg subtitle extraction failed for {SourcePath}: {Error}",
                    sourcePath,
                    stderr.Trim());
                return [];
            }

            return await ParseSubtitleFileAsync(outputPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[VideoSubtitles] ffmpeg subtitle extraction failed for {SourcePath}", sourcePath);
            return [];
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static bool SupportsTextSubtitleTrack(VideoTrackInfo track)
    {
        if (track.Type != VideoTrackType.Subtitle || track.IsImage)
            return false;

        var codec = track.Codec?.Trim().ToLowerInvariant() ?? "";
        return codec.Length == 0
            || !codec.Contains("pgs", StringComparison.Ordinal)
            && !codec.Contains("dvd", StringComparison.Ordinal)
            && !codec.Contains("dvb", StringComparison.Ordinal)
            && !codec.Contains("xsub", StringComparison.Ordinal);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var normalized = Path.GetFullPath(path);
            return File.Exists(normalized) ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveFfmpegPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NIRATAN_FFMPEG_PATH")
            ?? Environment.GetEnvironmentVariable("HOSHI_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        foreach (var candidate in GetBundledFfmpegCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator).Select(part => part.Trim('"')))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            try
            {
                var candidate = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> GetBundledFfmpegCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "app", "bin", "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "bin", "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "ffmpeg", "win-x64", "ffmpeg.exe");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
