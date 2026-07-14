using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Hoshi.Services.Sasayaki;

public sealed class SasayakiPlayer : IDisposable
{
    private MediaPlayer? _player;
    private Timer? _positionTimer;
    private bool _isDisposed;
    private bool _isPaused;
    private bool _isMediaOpened;
    private double? _pendingSeekSeconds;

    public event EventHandler<double>? PositionChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler<string>? MediaFailed;

    public double DurationSeconds => _player?.NaturalDuration.TotalSeconds ?? 0;
    public double PositionSeconds => _player?.Position.TotalSeconds ?? 0;
    public bool IsPlaying => _player?.CurrentState == MediaPlayerState.Playing;
    public bool IsPaused => _isPaused;
    public double PlaybackRate
    {
        get => _player?.PlaybackRate ?? 1.0;
        set
        {
            if (_player != null)
                _player.PlaybackRate = Math.Clamp(value, 0.25, 4.0);
        }
    }

    public async Task LoadAsync(string filePath)
    {
        StopInternal();

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
            };

            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;

            _player = player;
            player.Source = MediaSource.CreateFromStorageFile(file);
            Log.Information("[Sasayaki] Loaded '{Path}'", filePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to load '{Path}'", filePath);
            MediaFailed?.Invoke(this, ex.Message);
        }
    }

    public void Play()
    {
        if (_player == null)
            return;

        try
        {
            if (_isMediaOpened)
                ApplyPendingSeek(_player);

            _player.Play();
            _isPaused = false;
            StartPositionTimer();
            Log.Information("[Sasayaki] Playback started");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to start playback");
        }
    }

    public void Pause()
    {
        if (_player == null || !IsPlaying)
            return;

        try
        {
            _player.Pause();
            _isPaused = true;
            StopPositionTimer();
            Log.Information("[Sasayaki] Playback paused");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to pause playback");
        }
    }

    public void Resume()
    {
        if (_player == null || !_isPaused)
            return;

        Play();
    }

    public void Stop()
    {
        StopInternal();
    }

    public void Seek(double seconds)
    {
        if (_player == null)
            return;

        _pendingSeekSeconds = NormalizeSeekSeconds(seconds, DurationSeconds);
        if (!_isMediaOpened)
        {
            Log.Information(
                "[Sasayaki] Queued seek to {Seconds:F1}s until media opens",
                _pendingSeekSeconds);
            return;
        }

        ApplyPendingSeek(_player);
    }

    internal static double NormalizeSeekSeconds(double requestedSeconds, double durationSeconds)
    {
        if (!double.IsFinite(requestedSeconds))
            return 0;

        var normalized = Math.Max(0, requestedSeconds);
        if (double.IsFinite(durationSeconds) && durationSeconds > 0)
            normalized = Math.Min(normalized, durationSeconds);

        return normalized;
    }

    private void StartPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = new Timer(_ =>
        {
            if (_player != null && IsPlaying)
            {
                PositionChanged?.Invoke(this, _player.Position.TotalSeconds);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void StopInternal()
    {
        StopPositionTimer();
        _isMediaOpened = false;
        _pendingSeekSeconds = null;

        if (_player == null)
            return;

        try
        {
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.Source = null;
            _player.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Error stopping player");
        }
        finally
        {
            _player = null;
            _isPaused = false;
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (!ReferenceEquals(sender, _player))
            return;

        _isMediaOpened = true;
        ApplyPendingSeek(sender);
    }

    private void ApplyPendingSeek(MediaPlayer sender)
    {
        if (!_pendingSeekSeconds.HasValue)
            return;

        var seconds = NormalizeSeekSeconds(
            _pendingSeekSeconds.Value,
            sender.NaturalDuration.TotalSeconds);
        try
        {
            sender.Position = TimeSpan.FromSeconds(seconds);
            _pendingSeekSeconds = null;
            Log.Information("[Sasayaki] Seeked to {Seconds:F1}s", seconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Sasayaki] Failed to seek");
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        Log.Information("[Sasayaki] Media ended");
        StopPositionTimer();
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Log.Warning("[Sasayaki] Media failed: {Error}", args.ErrorMessage);
        StopPositionTimer();
        MediaFailed?.Invoke(this, args.ErrorMessage);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopInternal();
    }
}
