using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Settings;

namespace Niratan.Services.Video;

internal sealed class MpvPlaybackEngine : IVideoPlaybackEngine
{
    private readonly IAnime4KShaderService _anime4KShaderService;
    private readonly object _syncRoot = new();
    private IntPtr _handle;
    private bool _disposed;

    public MpvPlaybackEngine(IAnime4KShaderService anime4KShaderService)
    {
        _anime4KShaderService = anime4KShaderService;
    }
    private CancellationTokenSource? _eventLoopCts;
    private Task? _eventLoopTask;

    public event EventHandler<VideoMediaLoadedEventArgs>? MediaLoaded;
    public event EventHandler<VideoMediaFailedEventArgs>? MediaFailed;

    public Task InitializeAsync(IntPtr hostHwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hostHwnd == IntPtr.Zero)
            throw new ArgumentException("Video host window is not ready.", nameof(hostHwnd));

        StopEventLoop();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            DestroyHandle();

            try
            {
                _handle = MpvNative.Create();
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(MpvNative.GetLoadDiagnostic(), ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException("The bundled libmpv architecture does not match the running Niratan process.", ex);
            }

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create the libmpv playback engine.");

            MpvNative.SetOptionStringChecked(_handle, "config", "no");
            MpvNative.SetOptionStringChecked(_handle, "osc", "no");
            MpvNative.SetOptionStringChecked(_handle, "input-default-bindings", "no");
            MpvNative.SetOptionStringChecked(_handle, "input-cursor", "no");
            MpvNative.SetOptionStringChecked(_handle, "sid", "no");
            MpvNative.SetOptionStringChecked(_handle, "sub-visibility", "no");
            MpvNative.SetOptionStringChecked(_handle, "keep-open", "yes");
            MpvNative.SetOptionStringChecked(_handle, "force-window", "yes");
            MpvNative.SetOptionStringChecked(_handle, "panscan", "0.0");
            MpvNative.SetOptionStringChecked(_handle, "hwdec", "auto-safe");
            MpvNative.SetOptionStringChecked(_handle, "ytdl", "no");
            MpvNative.SetOptionStringChecked(_handle, "wid", hostHwnd.ToInt64().ToString());

            var status = MpvNative.Initialize(_handle);
            if (status < 0)
            {
                var message = MpvNative.ErrorString(status);
                DestroyHandle();
                throw new InvalidOperationException($"Unable to initialize libmpv: {message}");
            }

            StartEventLoop();
        }

        return Task.CompletedTask;
    }

    public Task OpenAsync(
        string filePath,
        string? subtitlePath = null,
        TimeSpan? startPosition = null,
        CancellationToken ct = default)
        => OpenAsync(VideoPlaybackRequest.Local(filePath, subtitlePath, startPosition), ct);

    public Task OpenAsync(VideoPlaybackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.PrimarySource))
            throw new ArgumentException("Video source is required.", nameof(request));

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            var headers = string.Join(",", request.HttpHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
            var headerStatus = MpvNative.SetPropertyString(_handle, "http-header-fields", headers);
            if (headerStatus < 0)
                throw new InvalidOperationException($"Unable to configure video request headers: {MpvNative.ErrorString(headerStatus)}");

            var status = MpvNative.Command(_handle, BuildLoadRequestCommandArgs(request));
            if (status < 0)
                throw new InvalidOperationException($"Unable to load video: {MpvNative.ErrorString(status)}");

            if (!string.IsNullOrWhiteSpace(request.SubtitlePath) && File.Exists(request.SubtitlePath))
            {
                status = MpvNative.Command(_handle, "sub-add", request.SubtitlePath, "select");
                if (status >= 0)
                {
                    var hidden = 0;
                    _ = MpvNative.SetPropertyFlag(_handle, "sub-visibility", MpvNative.MpvFormatFlag, ref hidden);
                }
            }
        }

        return Task.CompletedTask;
    }

    internal static string[] BuildLoadFileCommandArgs(string filePath, TimeSpan? startPosition)
    {
        if (startPosition == null || startPosition <= TimeSpan.Zero)
            return ["loadfile", filePath, "replace"];

        return
        [
            "loadfile",
            filePath,
            "replace",
            "-1",
            $"start={MpvNative.FormatSeconds(startPosition.Value)},pause=yes",
        ];
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = paused ? 1 : 0;
            var status = MpvNative.SetPropertyFlag(_handle, "pause", MpvNative.MpvFormatFlag, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update playback state: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.Command(
                _handle,
                "seek",
                MpvNative.FormatSeconds(position),
                "absolute+exact");
            if (status < 0)
                throw new InvalidOperationException($"Unable to seek video: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Clamp(volume, 0, 130);
            var status = MpvNative.SetPropertyDouble(_handle, "volume", MpvNative.MpvFormatDouble, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update volume: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetPlaybackSpeedAsync(double speed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Clamp(speed, 0.25, 5);
            var status = MpvNative.SetPropertyDouble(_handle, "speed", MpvNative.MpvFormatDouble, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update playback speed: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetAudioDelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Clamp(delay.TotalSeconds, -30, 30);
            var status = MpvNative.SetPropertyDouble(_handle, "audio-delay", MpvNative.MpvFormatDouble, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update audio delay: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetSubtitleDelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Clamp(delay.TotalSeconds, -10, 10);
            var status = MpvNative.SetPropertyDouble(_handle, "sub-delay", MpvNative.MpvFormatDouble, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update subtitle delay: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetFileLoopEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.SetPropertyString(_handle, "loop-file", enabled ? "inf" : "no");
            if (status < 0)
                throw new InvalidOperationException($"Unable to update loop mode: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetABLoopAsync(VideoABLoop? loop, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var startValue = loop == null ? "no" : MpvNative.FormatSeconds(loop.Start);
            var endValue = loop == null ? "no" : MpvNative.FormatSeconds(loop.End);
            var startStatus = MpvNative.SetPropertyString(_handle, "ab-loop-a", startValue);
            if (startStatus < 0)
                throw new InvalidOperationException($"Unable to update A-B loop start: {MpvNative.ErrorString(startStatus)}");

            var endStatus = MpvNative.SetPropertyString(_handle, "ab-loop-b", endValue);
            if (endStatus < 0)
                throw new InvalidOperationException($"Unable to update A-B loop end: {MpvNative.ErrorString(endStatus)}");
        }

        return Task.CompletedTask;
    }

    public Task SetAspectRatioAsync(string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.SetPropertyString(_handle, "video-aspect-override", value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update aspect ratio: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetVideoRotationAsync(int degrees, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = ((degrees % 360) + 360) % 360;
            var status = MpvNative.SetPropertyString(_handle, "video-rotate", value.ToString(CultureInfo.InvariantCulture));
            if (status < 0)
                throw new InvalidOperationException($"Unable to update video rotation: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetHardwareDecodingAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var status = MpvNative.SetPropertyString(_handle, "hwdec", enabled ? "auto-safe" : "no");
            if (status < 0)
                throw new InvalidOperationException($"Unable to update hardware decoding: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetDeinterlaceAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = enabled ? 1 : 0;
            var status = MpvNative.SetPropertyFlag(_handle, "deinterlace", MpvNative.MpvFormatFlag, ref value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to update deinterlace: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task SetHDREnhancementAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var peakStatus = MpvNative.SetPropertyString(_handle, "hdr-compute-peak", enabled ? "yes" : "no");
            if (peakStatus < 0)
                throw new InvalidOperationException($"Unable to update HDR peak handling: {MpvNative.ErrorString(peakStatus)}");

            var toneStatus = MpvNative.SetPropertyString(_handle, "tone-mapping", "auto");
            if (toneStatus < 0)
                throw new InvalidOperationException($"Unable to update HDR tone mapping: {MpvNative.ErrorString(toneStatus)}");
        }

        return Task.CompletedTask;
    }

    public Task SetVideoShaderPresetAsync(
        VideoShaderPreset preset,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var paths = _anime4KShaderService.GetInstalledShaderPaths(preset);
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            foreach (var command in BuildShaderChangeListCommands(paths))
            {
                var status = MpvNative.Command(_handle, command);
                if (status < 0)
                {
                    throw new InvalidOperationException(
                        $"Unable to update video shaders: {MpvNative.ErrorString(status)}");
                }
            }
        }

        return Task.CompletedTask;
    }

    internal static IReadOnlyList<string[]> BuildShaderChangeListCommands(
        IReadOnlyList<string> absolutePaths)
    {
        var commands = new List<string[]>(absolutePaths.Count + 1)
        {
            new[] { "change-list", "glsl-shaders", "clr", "" },
        };
        commands.AddRange(absolutePaths.Select(path =>
            new[] { "change-list", "glsl-shaders", "append", path }));
        return commands;
    }

    public Task SetVideoEqualizerAsync(string adjustment, double value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var property = NormalizeVideoEqualizerAdjustment(adjustment);
            if (property.Length == 0)
                return Task.CompletedTask;

            var normalized = double.IsFinite(value) ? Math.Clamp(Math.Round(value), -100, 100) : 0;
            var status = MpvNative.SetPropertyString(
                _handle,
                property,
                normalized.ToString("0", CultureInfo.InvariantCulture));
            if (status < 0)
                throw new InvalidOperationException($"Unable to update video {property}: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    internal static string[] BuildLoadRequestCommandArgs(VideoPlaybackRequest request)
    {
        var options = new List<string>();
        if (request.StartPosition.HasValue && request.StartPosition.Value > TimeSpan.Zero)
        {
            options.Add($"start={MpvNative.FormatSeconds(request.StartPosition.Value)}");
            options.Add("pause=yes");
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalAudioSource))
            options.Add($"audio-file={EscapeMpvOption(request.ExternalAudioSource)}");

        return options.Count == 0
            ? ["loadfile", request.PrimarySource, "replace"]
            : ["loadfile", request.PrimarySource, "replace", "-1", string.Join(',', options)];
    }

    private static string EscapeMpvOption(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private void StartEventLoop()
    {
        _eventLoopCts?.Cancel();
        _eventLoopCts?.Dispose();
        _eventLoopCts = new CancellationTokenSource();
        var token = _eventLoopCts.Token;
        _eventLoopTask = Task.Run(() => RunEventLoop(token), token);
    }

    private void RunEventLoop(CancellationToken token)
    {
        IntPtr handle;
        lock (_syncRoot)
        {
            handle = _handle;
        }

        while (!token.IsCancellationRequested)
        {
            var eventPointer = MpvNative.WaitEvent(handle, -1);
            if (eventPointer == IntPtr.Zero)
                continue;

            var mpvEvent = Marshal.PtrToStructure<MpvNative.MpvEvent>(eventPointer);
            if (mpvEvent.EventId == MpvNative.MpvEventIdNone)
                continue;

            if (mpvEvent.EventId == MpvNative.MpvEventIdFileLoaded)
            {
                MediaLoaded?.Invoke(this, new VideoMediaLoadedEventArgs());
            }
            else if (mpvEvent.EventId == MpvNative.MpvEventIdEndFile && mpvEvent.Data != IntPtr.Zero)
            {
                var endFile = Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(mpvEvent.Data);
                if (endFile.Error < 0)
                {
                    MediaFailed?.Invoke(
                        this,
                        new VideoMediaFailedEventArgs("Unable to load the remote video source."));
                }
            }
            else if (mpvEvent.EventId == MpvNative.MpvEventIdShutdown)
            {
                return;
            }
        }
    }

    private void StopEventLoop()
    {
        var cts = _eventLoopCts;
        var task = _eventLoopTask;
        if (cts == null && task == null)
            return;

        cts?.Cancel();
        IntPtr handle;
        lock (_syncRoot)
        {
            handle = _handle;
        }

        if (handle != IntPtr.Zero)
            MpvNative.Wakeup(handle);

        if (task != null && task.Id != Task.CurrentId)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
        _eventLoopCts = null;
        _eventLoopTask = null;
    }

    public Task<VideoViewportGeometry?> GetVideoViewportGeometryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.FromResult<VideoViewportGeometry?>(null);

            var geometry = new VideoViewportGeometry(
                TryGetDoubleProperty("osd-dimensions/w") ?? 0,
                TryGetDoubleProperty("osd-dimensions/h") ?? 0,
                TryGetDoubleProperty("osd-dimensions/mt") ?? 0,
                TryGetDoubleProperty("osd-dimensions/mb") ?? 0,
                TryGetDoubleProperty("osd-dimensions/ml") ?? 0,
                TryGetDoubleProperty("osd-dimensions/mr") ?? 0);

            return Task.FromResult<VideoViewportGeometry?>(geometry.IsValid ? geometry : null);
        }
    }

    public Task<VideoDisplayInfo?> GetVideoDisplayInfoAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.FromResult<VideoDisplayInfo?>(null);

            var displayInfo = new VideoDisplayInfo(
                TryGetIntProperty("dwidth") ?? TryGetIntProperty("video-out-params/dw") ?? 0,
                TryGetIntProperty("dheight") ?? TryGetIntProperty("video-out-params/dh") ?? 0,
                TryGetIntProperty("video-params/rotate") ?? 0);
            return Task.FromResult<VideoDisplayInfo?>(displayInfo.IsValid ? displayInfo : null);
        }
    }

    private static string NormalizeVideoEqualizerAdjustment(string adjustment) =>
        adjustment.Trim().ToLowerInvariant() switch
        {
            "brightness" => "brightness",
            "contrast" => "contrast",
            "saturation" => "saturation",
            "gamma" => "gamma",
            "hue" => "hue",
            _ => "",
        };

    public Task<IReadOnlyList<VideoTrackInfo>> GetTracksAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.FromResult<IReadOnlyList<VideoTrackInfo>>([]);

            var countStatus = MpvNative.GetPropertyInt64(
                _handle,
                "track-list/count",
                MpvNative.MpvFormatInt64,
                out var count);
            if (countStatus < 0 || count <= 0)
                return Task.FromResult<IReadOnlyList<VideoTrackInfo>>([]);

            var tracks = new List<VideoTrackInfo>();
            for (var index = 0; index < count; index++)
            {
                var prefix = FormattableString.Invariant($"track-list/{index}");
                if (!TryGetTrackType(prefix, out var type))
                    continue;

                var id = TryGetIntProperty($"{prefix}/id") ?? 0;
                if (id <= 0)
                    continue;

                var title = TryGetStringProperty($"{prefix}/title");
                var language = TryGetStringProperty($"{prefix}/lang");
                var codec = TryGetStringProperty($"{prefix}/codec");
                var externalFilename = TryGetStringProperty($"{prefix}/external-filename");
                var ffIndex = TryGetIntProperty($"{prefix}/ff-index");
                var isImage = TryGetFlagProperty($"{prefix}/image");
                var isSelected = TryGetFlagProperty($"{prefix}/selected");

                tracks.Add(new VideoTrackInfo(
                    id,
                    type,
                    string.IsNullOrWhiteSpace(title) ? VideoTrackInfo.DefaultTitle(type, id) : title,
                    language,
                    codec,
                    ffIndex,
                    externalFilename,
                    isImage,
                    isSelected));
            }

            return Task.FromResult<IReadOnlyList<VideoTrackInfo>>(tracks);
        }
    }

    public Task SelectTrackAsync(VideoTrackType type, int? trackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var property = type switch
            {
                VideoTrackType.Video => "vid",
                VideoTrackType.Audio => "aid",
                VideoTrackType.Subtitle => "sid",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };
            var value = trackId.HasValue
                ? trackId.Value.ToString(CultureInfo.InvariantCulture)
                : "no";
            var status = MpvNative.SetPropertyString(_handle, property, value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to select {type} track: {MpvNative.ErrorString(status)}");

            if (type == VideoTrackType.Subtitle)
            {
                var visibility = 0;
                _ = MpvNative.SetPropertyFlag(
                    _handle,
                    "sub-visibility",
                    MpvNative.MpvFormatFlag,
                    ref visibility);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VideoChapter>> GetChaptersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.FromResult<IReadOnlyList<VideoChapter>>([]);

            var countStatus = MpvNative.GetPropertyInt64(
                _handle,
                "chapter-list/count",
                MpvNative.MpvFormatInt64,
                out var count);
            if (countStatus < 0 || count <= 0)
                return Task.FromResult<IReadOnlyList<VideoChapter>>([]);

            var chapters = new List<VideoChapter>();
            for (var index = 0; index < count; index++)
            {
                var prefix = FormattableString.Invariant($"chapter-list/{index}");
                var time = TryGetDoubleProperty($"{prefix}/time");
                if (!time.HasValue || time.Value < 0)
                    continue;

                var title = TryGetStringProperty($"{prefix}/title");
                chapters.Add(new VideoChapter(
                    index,
                    string.IsNullOrWhiteSpace(title) ? $"Chapter {index + 1}" : title,
                    TimeSpan.FromSeconds(time.Value)));
            }

            return Task.FromResult<IReadOnlyList<VideoChapter>>(chapters);
        }
    }

    public Task SeekChapterAsync(int chapterId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.CompletedTask;

            var value = Math.Max(0, chapterId).ToString(CultureInfo.InvariantCulture);
            var status = MpvNative.SetPropertyString(_handle, "chapter", value);
            if (status < 0)
                throw new InvalidOperationException($"Unable to seek chapter: {MpvNative.ErrorString(status)}");
        }

        return Task.CompletedTask;
    }

    public Task<VideoSubtitleCue?> GetCurrentSubtitleCueAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return Task.FromResult<VideoSubtitleCue?>(null);

            var textStatus = MpvNative.GetPropertyString(_handle, "sub-text", out var text);
            if (textStatus < 0 || string.IsNullOrWhiteSpace(text))
                return Task.FromResult<VideoSubtitleCue?>(null);

            var start = TryGetDoubleProperty("sub-start") ?? GetDoubleTimeProperty("time-pos").TotalSeconds;
            var end = TryGetDoubleProperty("sub-end") ?? start + 10;
            if (!double.IsFinite(end) || end <= start)
                end = start + 10;

            return Task.FromResult<VideoSubtitleCue?>(new VideoSubtitleCue(
                0,
                TimeSpan.FromSeconds(Math.Max(0, start)),
                TimeSpan.FromSeconds(Math.Max(0, end)),
                text));
        }
    }

    public Task<TimeSpan> GetPositionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetDoubleTimeProperty("time-pos"));
    }

    public Task<TimeSpan> GetDurationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetDoubleTimeProperty("duration"));
    }

    public async Task<string?> CaptureScreenshotAsync(string outputPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_handle == IntPtr.Zero)
                return null;

            var status = MpvNative.Command(_handle, "screenshot-to-file", outputPath, "video");
            if (status < 0)
                return null;
        }

        for (var i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (HasOutput(outputPath))
                return outputPath;

            await Task.Delay(50, ct);
        }

        return HasOutput(outputPath) ? outputPath : null;
    }

    private static bool HasOutput(string outputPath) =>
        File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;

    public void Dispose()
    {
        StopEventLoop();
        lock (_syncRoot)
        {
            if (_disposed)
                return;

            DestroyHandle();
            _disposed = true;
        }

    }

    private TimeSpan GetDoubleTimeProperty(string name)
    {
        lock (_syncRoot)
        {
            if (_disposed || _handle == IntPtr.Zero)
                return TimeSpan.Zero;

            var status = MpvNative.GetPropertyDouble(_handle, name, MpvNative.MpvFormatDouble, out var seconds);
            if (status < 0 || !double.IsFinite(seconds) || seconds < 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds(seconds);
        }
    }

    private bool TryGetTrackType(string prefix, out VideoTrackType type)
    {
        type = default;
        var rawType = TryGetStringProperty($"{prefix}/type");
        if (string.IsNullOrWhiteSpace(rawType))
            return false;

        switch (rawType.Trim().ToLowerInvariant())
        {
            case "video":
                type = VideoTrackType.Video;
                return true;
            case "audio":
                type = VideoTrackType.Audio;
                return true;
            case "sub":
            case "subtitle":
                type = VideoTrackType.Subtitle;
                return true;
            default:
                return false;
        }
    }

    private string? TryGetStringProperty(string name)
    {
        var status = MpvNative.GetPropertyString(_handle, name, out var value);
        return status >= 0 && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private int? TryGetIntProperty(string name)
    {
        var status = MpvNative.GetPropertyInt64(_handle, name, MpvNative.MpvFormatInt64, out var value);
        if (status < 0)
            return null;

        if (value < int.MinValue || value > int.MaxValue)
            return null;

        return (int)value;
    }

    private bool TryGetFlagProperty(string name)
    {
        var status = MpvNative.GetPropertyFlag(_handle, name, MpvNative.MpvFormatFlag, out var value);
        return status >= 0 && value != 0;
    }

    private double? TryGetDoubleProperty(string name)
    {
        var status = MpvNative.GetPropertyDouble(_handle, name, MpvNative.MpvFormatDouble, out var value);
        return status >= 0 && double.IsFinite(value) ? value : null;
    }

    private void EnsureInitialized()
    {
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Video playback is not initialized.");
    }

    private void DestroyHandle()
    {
        if (_handle == IntPtr.Zero)
            return;

        MpvNative.TerminateDestroy(_handle);
        _handle = IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
