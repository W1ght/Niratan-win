using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Settings;
using NAudio.Wave;
using Serilog;

namespace Niratan.Services.Audio;

public sealed class AudioService : IAudioService, IDisposable
{
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private AudioSettings _settings = new();
    private static readonly HttpClient s_audioDownloadClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public AudioSettings Settings => _settings;

    public async Task PlayAsync(
        string url,
        AudioPlaybackMode mode,
        string? traceId = null,
        string? audioTraceId = null)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} play request received url='{Url}' mode={Mode}",
                traceId ?? "-", audioTraceId ?? "-", url, mode);

            // Phase 1: Download on background thread
            byte[] audioBytes;
            try
            {
                var downloadSw = System.Diagnostics.Stopwatch.StartNew();
                audioBytes = await LoadAudioBytesAsync(url);
                Log.Information(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} download completed in {Ms}ms total={TotalMs}ms bytes={Bytes}",
                    traceId ?? "-", audioTraceId ?? "-", downloadSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, audioBytes.Length);
            }
            catch (TaskCanceledException)
            {
                Log.Warning(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} download timeout total={TotalMs}ms url='{Url}'",
                    traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, url);
                Log.Warning("[Audio] Download timeout: '{Url}'", url);
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} download failed total={TotalMs}ms url='{Url}'",
                    traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, url);
                Log.Warning(ex, "[Audio] Download failed: '{Url}'", url);
                return;
            }

            // Phase 2: Validate — fail fast on obviously bad data
            if (audioBytes is not { Length: >= 1024 })
            {
                Log.Warning(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} validation failed too-small total={TotalMs}ms bytes={Bytes}",
                    traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, audioBytes?.Length ?? 0);
                Log.Warning("[Audio] Skipping — too small ({Bytes} bytes): '{Url}'",
                    audioBytes?.Length ?? 0, url);
                return;
            }

            if (LooksLikeErrorResponse(audioBytes))
            {
                Log.Warning(
                    "[AudioTrace] lookup={TraceId} audio={AudioTraceId} validation failed html-response total={TotalMs}ms bytes={Bytes}",
                    traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, audioBytes.Length);
                Log.Warning("[Audio] Skipping — looks like HTML error response ({Bytes} bytes): '{Url}'",
                    audioBytes.Length, url);
                return;
            }

            // Phase 3: Cancel previous playback (CancellationToken only — no NAudio dispose on UI thread)
            var cts = new CancellationTokenSource();
            CancellationTokenSource? oldCts;
            lock (_lock) { oldCts = _cts; _cts = cts; }
            if (oldCts != null)
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} previous playback cancelled total={TotalMs}ms hadPrevious={HadPrevious}",
                traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, oldCts != null);

            // Phase 4: CreateReader + Init + Play ALL on background thread.
            // StreamMediaFoundationReader calls COM interop which can hang — it must
            // never run on the UI thread.  Waiting with await keeps timing logs accurate.
            await Task.Run(() => PlayFromBytes(audioBytes, cts.Token, url, mode, traceId, audioTraceId), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback cancelled total={TotalMs}ms url='{Url}'",
                traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, url);
            Log.Information("[Audio] Playback cancelled: '{Url}'", url);
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} PlayAsync crashed total={TotalMs}ms url='{Url}'",
                traceId ?? "-", audioTraceId ?? "-", totalSw.ElapsedMilliseconds, url);
            Log.Error(ex, "[Audio] PlayAsync crashed for '{Url}'", url);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? oldCts;
        lock (_lock) { oldCts = _cts; _cts = null; }
        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
    }

    public void UpdateSettings(AudioSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Quick heuristic: audio files don't start with '&lt;' (after optional BOM / whitespace).
    /// Catches HTML error pages returned with HTTP 200.
    /// </summary>
    private static bool LooksLikeErrorResponse(byte[] bytes)
    {
        var span = bytes.AsSpan(0, Math.Min(bytes.Length, 256));
        // Skip UTF-8 BOM
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        // Skip ASCII whitespace
        while (span.Length > 0 && span[0] <= 32)
            span = span[1..];
        return span.Length > 0 && span[0] == (byte)'<';
    }

    /// <summary>
    /// Runs entirely on the calling thread (which must be a background thread).
    /// CreateReader → WaveOutEvent.Init → Play → wait for completion → dispose.
    /// </summary>
    private static void PlayFromBytes(
        byte[] audioBytes,
        CancellationToken ct,
        string url,
        AudioPlaybackMode mode,
        string? traceId = null,
        string? audioTraceId = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MemoryStream? ms = null;
        WaveStream? reader = null;
        WaveOutEvent? waveOut = null;
        try
        {
            Log.Information("[Audio] Playing '{Url}' ({Bytes} bytes) mode={Mode}", url, audioBytes.Length, mode);

            ms = new MemoryStream(audioBytes);
            var readerSw = System.Diagnostics.Stopwatch.StartNew();
            reader = CreateReader(ms);
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} reader created in {Ms}ms playThreadTotal={TotalMs}ms reader={ReaderType}",
                traceId ?? "-", audioTraceId ?? "-", readerSw.ElapsedMilliseconds, sw.ElapsedMilliseconds, reader.GetType().Name);
            var initSw = System.Diagnostics.Stopwatch.StartNew();
            waveOut = new WaveOutEvent();
            waveOut.Init(reader);
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} waveOut initialized in {Ms}ms playThreadTotal={TotalMs}ms",
                traceId ?? "-", audioTraceId ?? "-", initSw.ElapsedMilliseconds, sw.ElapsedMilliseconds);
            waveOut.Play();
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback started in {Ms}ms bytes={Bytes} mode={Mode}",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, audioBytes.Length, mode);

            while (waveOut.PlaybackState == PlaybackState.Playing)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }

            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback ended in {Ms}ms url='{Url}'",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url);
            Log.Information("[Audio] Playback ended: '{Url}'", url);
        }
        catch (OperationCanceledException)
        {
            Log.Information(
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback interrupted in {Ms}ms url='{Url}'",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url);
            Log.Information("[Audio] Playback interrupted: '{Url}'", url);
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback crashed in {Ms}ms url='{Url}'",
                traceId ?? "-", audioTraceId ?? "-", sw.ElapsedMilliseconds, url);
            Log.Error(ex, "[Audio] Playback crashed: '{Url}'", url);
        }
        finally
        {
            try { waveOut?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "[Audio] Error disposing waveOut"); }
            try { reader?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "[Audio] Error disposing reader"); }
            try { ms?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "[Audio] Error disposing stream"); }
        }
    }

    private static WaveStream CreateReader(Stream stream)
    {
        try
        {
            return new Mp3FileReader(stream);
        }
        catch (InvalidDataException)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return new StreamMediaFoundationReader(stream);
        }
    }

    private static async Task<byte[]> LoadAudioBytesAsync(string url)
    {
        if (TryGetLocalPath(url, out var path))
            return await File.ReadAllBytesAsync(path);

        return await Task.Run(() => s_audioDownloadClient.GetByteArrayAsync(url));
    }

    private static bool TryGetLocalPath(string url, out string path)
    {
        path = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _) && File.Exists(url))
        {
            path = url;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
