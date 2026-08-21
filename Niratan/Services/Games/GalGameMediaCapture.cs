using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Niratan.Helpers;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

public sealed record GalGameMiningMedia(string? ScreenshotPath, string? AudioPath);

public sealed class GalGameMediaCapture
{
    private const uint PrintWindowClientArea = 2;
    private const int ResourcePairingWindowMs = 1500;
    private const int ResourceQuietPeriodMs = 240;
    private const int ResourceWaitTimeoutMs = 1500;

    public Task<GalGameMiningMedia> PrepareAsync(
        int processId,
        GalGameTextLine line,
        GalGameAudioCapture? audio,
        CancellationToken ct = default) =>
        Task.Run(() => PrepareCore(processId, line, audio, ct), ct);

    public async Task<GalGameMiningMedia> PrepareAsync(
        int processId,
        GalGameTextLine line,
        Func<CancellationToken, Task<GalGameAudioCapture?>> audioProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audioProvider);
        // Freeze the visible game frame/resource candidate immediately while
        // the loopback ring finishes the post-roll for this utterance.
        var mediaTask = Task.Run(() => PrepareCore(processId, line, null, ct), ct);
        var audioTask = audioProvider(ct);
        await Task.WhenAll(mediaTask, audioTask);
        var media = mediaTask.Result;
        var audio = audioTask.Result;
        if (!string.IsNullOrWhiteSpace(media.AudioPath) || audio is not { IsValid: true })
            return media;

        try
        {
            var root = !string.IsNullOrWhiteSpace(media.ScreenshotPath)
                ? Path.GetDirectoryName(media.ScreenshotPath)!
                : CreateCaptureRoot();
            var audioPath = WriteWave(audio, Path.Combine(root, $"{Guid.NewGuid():N}.wav"));
            return media with { AudioPath = audioPath };
        }
        catch
        {
            return media;
        }
    }

    private static GalGameMiningMedia PrepareCore(
        int processId,
        GalGameTextLine line,
        GalGameAudioCapture? audio,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = CreateCaptureRoot();

        string? screenshot = null;
        try
        {
            screenshot = CaptureWindow(processId, Path.Combine(root, $"{Guid.NewGuid():N}.jpg"));
        }
        catch
        {
            // A minimized or protected game may reject PrintWindow. Audio-only
            // mining remains useful and the popup will show the missing-media state.
        }

        ct.ThrowIfCancellationRequested();
        // Siglus/anemoi can expose the original OVK voice without producing a
        // usable PCM clip. Prefer the hook's resource dump when it is paired
        // with this text line, then fall back to the shared PCM ring.
        var audioPath = TryCopyPairedResourceAudio(line, root, ct);
        if (audioPath is null && audio is { IsValid: true })
        {
            try
            {
                audioPath = WriteWave(audio, Path.Combine(root, $"{Guid.NewGuid():N}.wav"));
            }
            catch
            {
                audioPath = null;
            }
        }

        return new GalGameMiningMedia(screenshot, audioPath);
    }

    private static string CreateCaptureRoot()
    {
        var root = Path.Combine(
            AppDataHelper.GetGameDataPath(),
            "capture-media",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? TryCopyPairedResourceAudio(
        GalGameTextLine line,
        string destinationDirectory,
        CancellationToken ct)
    {
        if (line.TimestampMs == 0)
            return null;

        var dumpDirectory = Path.Combine(Path.GetTempPath(), "fushi_gal_voice");
        if (!Directory.Exists(dumpDirectory))
            return null;

        var deadline = Environment.TickCount64 + ResourceWaitTimeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // The source implementation gives Unity WAV priority, followed by
            // Siglus/KiriKiri OGG. Keep the same ordering here.
            var candidate = FindPairedResourceAudio(dumpDirectory, line, ".wav")
                ?? FindPairedResourceAudio(dumpDirectory, line, ".ogg");
            if (candidate is not null && WaitForStableFile(candidate.Value.Path, ct))
            {
                try
                {
                    var extension = Path.GetExtension(candidate.Value.Path);
                    var destination = Path.Combine(
                        destinationDirectory,
                        $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
                    File.Copy(candidate.Value.Path, destination, overwrite: false);
                    return new FileInfo(destination).Length > 0 ? destination : null;
                }
                catch (IOException)
                {
                    // The hook may still have the resource open. The next
                    // scan will retry without touching the source file.
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            if (Environment.TickCount64 >= deadline)
                return null;

            Thread.Sleep(60);
        }
    }

    private static ResourceAudioCandidate? FindPairedResourceAudio(
        string dumpDirectory,
        GalGameTextLine line,
        string extension)
    {
        var textTimestamp = line.TimestampMs > long.MaxValue
            ? long.MaxValue
            : (long)line.TimestampMs;
        ResourceAudioCandidate? best = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dumpDirectory, $"*{extension}"))
            {
                if (!TryParseResourceAudioName(path, out var tick, out var eventId, out var basename))
                    continue;
                if (IsNonVoiceBasename(basename))
                    continue;

                var candidateTimestamp = tick > long.MaxValue ? long.MaxValue : (long)tick;
                var distance = Math.Abs(candidateTimestamp - textTimestamp);
                int rank;
                if (eventId.HasValue)
                {
                    // An explicit sequence is authoritative; never guess a
                    // different line from a nearby marked resource.
                    if (line.Sequence == 0 || eventId.Value != line.Sequence || distance > ResourcePairingWindowMs)
                        continue;
                    rank = 0;
                }
                else if (distance == 0)
                {
                    rank = 1;
                }
                else if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                         && candidateTimestamp >= textTimestamp - 1000
                         && candidateTimestamp <= textTimestamp + 500)
                {
                    rank = 2;
                }
                else if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                         && candidateTimestamp >= textTimestamp - 330
                         && candidateTimestamp <= textTimestamp - 130)
                {
                    rank = 2;
                    distance = Math.Abs(candidateTimestamp - (textTimestamp - 220));
                }
                else
                {
                    continue;
                }

                var candidate = new ResourceAudioCandidate(path, rank, distance);
                if (best is null
                    || candidate.Rank < best.Value.Rank
                    || (candidate.Rank == best.Value.Rank && candidate.Distance < best.Value.Distance)
                    || (candidate.Rank == best.Value.Rank
                        && candidate.Distance == best.Value.Distance
                        && string.CompareOrdinal(candidate.Path, best.Value.Path) < 0))
                {
                    best = candidate;
                }
            }
        }
        catch (IOException)
        {
            return best;
        }
        catch (UnauthorizedAccessException)
        {
            return best;
        }

        return best;
    }

    private static bool TryParseResourceAudioName(
        string path,
        out ulong tick,
        out ulong? eventId,
        out string basename)
    {
        tick = 0;
        eventId = null;
        basename = string.Empty;

        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.IndexOf('_');
        if (separator <= 0
            || !ulong.TryParse(name[..separator], out tick))
        {
            return false;
        }

        var suffix = name[(separator + 1)..];
        const string marker = "fushi_textseq";
        if (suffix.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            var markerEnd = suffix.IndexOf('_', marker.Length);
            if (markerEnd <= marker.Length
                || !ulong.TryParse(suffix[marker.Length..markerEnd], out var parsedEventId)
                || parsedEventId == 0)
            {
                return false;
            }

            eventId = parsedEventId;
            suffix = suffix[(markerEnd + 1)..];
        }

        basename = suffix;
        return basename.Length > 0;
    }

    private static bool IsNonVoiceBasename(string basename) =>
        basename.StartsWith("bgm", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("se", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("sys", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("amb", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("env", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("title", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("logo", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("movie", StringComparison.OrdinalIgnoreCase)
        || basename.StartsWith("jingle", StringComparison.OrdinalIgnoreCase);

    private static bool WaitForStableFile(string path, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        long previousSize = -1;
        var lastChange = started;

        while (Environment.TickCount64 - started < ResourceWaitTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return false;
                if (info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc >= TimeSpan.FromSeconds(2))
                    return true;
                if (info.Length != previousSize)
                {
                    previousSize = info.Length;
                    lastChange = Environment.TickCount64;
                }
                else if (info.Length > 0 && Environment.TickCount64 - lastChange >= ResourceQuietPeriodMs)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            Thread.Sleep(60);
        }

        // Never make mining fail solely because a hook file was still growing.
        // A final existence check lets Anki read the best available bytes.
        return File.Exists(path);
    }

    private readonly record struct ResourceAudioCandidate(string Path, int Rank, long Distance);

    private static string? CaptureWindow(int processId, string outputPath)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        var hwnd = process.MainWindowHandle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, PrintWindowClientArea))
            {
                graphics.ReleaseHdc(hdc);
                hdc = IntPtr.Zero;
                graphics.CopyFromScreen(
                    rect.Left,
                    rect.Top,
                    0,
                    0,
                    new Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }
        }
        finally
        {
            if (hdc != IntPtr.Zero)
            {
                try { graphics.ReleaseHdc(hdc); }
                catch (ArgumentException) { }
            }
        }

        bitmap.Save(outputPath, ImageFormat.Jpeg);
        return File.Exists(outputPath) ? outputPath : null;
    }

    private static string WriteWave(GalGameAudioCapture audio, string outputPath)
    {
        var format = audio.IsFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels)
            : new WaveFormat(audio.SampleRate, audio.BitsPerSample, audio.Channels);
        using (var writer = new WaveFileWriter(outputPath, format))
            writer.Write(audio.Pcm, 0, audio.Pcm.Length);
        return outputPath;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
