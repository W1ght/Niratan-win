using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Serilog;

namespace Niratan.Services.Video;

internal sealed class LibMpvVideoMiningMediaExtractor : IVideoMiningMediaExtractor
{
    private const int MpvEventIdPlaybackRestart = 21;

    public Task<string?> CaptureScreenshotAsync(
        string videoPath,
        string outputPath,
        TimeSpan timestamp,
        CancellationToken ct = default) =>
        Task.Run(() => CaptureScreenshot(videoPath, outputPath, timestamp, ct), ct);

    public Task<string?> ExportAudioClipAsync(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct = default) =>
        ExportAudioClipAsync(VideoMiningMediaSource.Local(videoPath), outputPath, start, end, ct);

    public Task<string?> ExportAudioClipAsync(
        VideoMiningMediaSource source,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct = default) =>
        Task.Run(() => ExportAudioClip(source, outputPath, start, end, ct), ct);

    private static string? CaptureScreenshot(
        string videoPath,
        string outputPath,
        TimeSpan timestamp,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        var handle = IntPtr.Zero;
        try
        {
            handle = MpvNative.Create();
            if (handle == IntPtr.Zero)
                return null;

            MpvNative.SetOptionStringChecked(handle, "config", "no");
            MpvNative.SetOptionStringChecked(handle, "vo", "null");
            MpvNative.SetOptionStringChecked(handle, "screenshot-sw", "yes");
            MpvNative.SetOptionStringChecked(handle, "sid", "no");
            MpvNative.SetOptionStringChecked(handle, "audio", "no");
            MpvNative.SetOptionStringChecked(handle, "start", MpvNative.FormatSeconds(timestamp));

            var status = MpvNative.Initialize(handle);
            if (status < 0)
            {
                Log.Warning("[VideoMining] libmpv thumbnail initialization failed: {Error}", MpvNative.ErrorString(status));
                return null;
            }

            status = MpvNative.Command(handle, "loadfile", videoPath);
            if (status < 0)
            {
                Log.Warning("[VideoMining] libmpv thumbnail could not load video: {Error}", MpvNative.ErrorString(status));
                return null;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var eventPtr = MpvNative.WaitEvent(handle, 0.25);
                if (eventPtr == IntPtr.Zero)
                    continue;

                var mpvEvent = Marshal.PtrToStructure<MpvNative.MpvEvent>(eventPtr);
                if (mpvEvent.EventId == MpvEventIdPlaybackRestart)
                {
                    status = MpvNative.Command(handle, "screenshot-to-file", outputPath, "video");
                    if (status < 0)
                    {
                        Log.Warning("[VideoMining] libmpv thumbnail screenshot failed: {Error}", MpvNative.ErrorString(status));
                        return null;
                    }

                    return HasOutput(outputPath) ? outputPath : null;
                }

                if (mpvEvent.EventId == MpvNative.MpvEventIdEndFile)
                {
                    Log.Warning("[VideoMining] libmpv thumbnail reached end of file before screenshot");
                    return null;
                }

                if (mpvEvent.EventId == MpvNative.MpvEventIdShutdown)
                    break;
            }

            Log.Warning("[VideoMining] libmpv thumbnail capture timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[VideoMining] libmpv thumbnail capture failed");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                MpvNative.TerminateDestroy(handle);

            if (!HasOutput(outputPath) && File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static string? ExportAudioClip(
        VideoMiningMediaSource source,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct)
    {
        if (end <= start || string.IsNullOrWhiteSpace(source.Source))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        var handle = IntPtr.Zero;
        try
        {
            handle = MpvNative.Create();
            if (handle == IntPtr.Zero)
                return null;

            MpvNative.SetOptionStringChecked(handle, "config", "no");
            MpvNative.SetOptionStringChecked(handle, "ytdl", "no");
            if (source.HttpHeaders.Count > 0)
            {
                MpvNative.SetOptionStringChecked(
                    handle,
                    "http-header-fields",
                    string.Join(',', source.HttpHeaders.Select(pair => $"{pair.Key}: {pair.Value}")));
            }
            MpvNative.SetOptionStringChecked(handle, "vid", "no");
            MpvNative.SetOptionStringChecked(handle, "sid", "no");
            MpvNative.SetOptionStringChecked(handle, "audio-channels", "mono");
            MpvNative.SetOptionStringChecked(handle, "start", MpvNative.FormatSeconds(start));
            MpvNative.SetOptionStringChecked(handle, "end", MpvNative.FormatSeconds(end));
            MpvNative.SetOptionStringChecked(handle, "o", outputPath);
            MpvNative.SetOptionStringChecked(handle, "oac", "aac");
            MpvNative.SetOptionStringChecked(handle, "of", "mp4");
            MpvNative.SetOptionStringChecked(handle, "oacopts", "b=64k");

            var status = MpvNative.Initialize(handle);
            if (status < 0)
            {
                Log.Warning("[VideoMining] libmpv encoder initialization failed: {Error}", MpvNative.ErrorString(status));
                return null;
            }

            status = MpvNative.Command(handle, "loadfile", source.Source);
            if (status < 0)
            {
                Log.Warning("[VideoMining] libmpv encoder could not load video: {Error}", MpvNative.ErrorString(status));
                return null;
            }

            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var eventPtr = MpvNative.WaitEvent(handle, 0.25);
                if (eventPtr == IntPtr.Zero)
                    continue;

                var mpvEvent = Marshal.PtrToStructure<MpvNative.MpvEvent>(eventPtr);
                if (mpvEvent.EventId == MpvNative.MpvEventIdEndFile)
                {
                    if (mpvEvent.Data != IntPtr.Zero)
                    {
                        var endFile = Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(mpvEvent.Data);
                        if (endFile.Error < 0)
                        {
                            Log.Warning("[VideoMining] libmpv audio export failed: {Error}", MpvNative.ErrorString(endFile.Error));
                            return null;
                        }
                    }

                    return HasOutput(outputPath) ? outputPath : null;
                }

                if (mpvEvent.EventId == MpvNative.MpvEventIdShutdown)
                    break;
            }

            Log.Warning("[VideoMining] libmpv audio export timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[VideoMining] libmpv audio export failed");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                MpvNative.TerminateDestroy(handle);

            if (!HasOutput(outputPath) && File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static bool HasOutput(string outputPath) =>
        File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
}
