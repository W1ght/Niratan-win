using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Hoshi.Services.Video;

internal sealed class LibMpvVideoMiningMediaExtractor : IVideoMiningMediaExtractor
{
    public Task<string?> CaptureScreenshotAsync(
        string videoPath,
        string outputPath,
        TimeSpan timestamp,
        CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> ExportAudioClipAsync(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct = default) =>
        Task.Run(() => ExportAudioClip(videoPath, outputPath, start, end, ct), ct);

    private static string? ExportAudioClip(
        string videoPath,
        string outputPath,
        TimeSpan start,
        TimeSpan end,
        CancellationToken ct)
    {
        if (end <= start || string.IsNullOrWhiteSpace(videoPath))
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

            status = MpvNative.Command(handle, "loadfile", videoPath);
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
