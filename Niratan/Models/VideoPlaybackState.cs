using System;

namespace Niratan.Models;

public enum VideoSubtitleSelectionKind
{
    None,
    ExternalFile,
    EmbeddedTrack,
    Off,
}

public sealed record VideoSubtitleSelection(
    VideoSubtitleSelectionKind Kind,
    string? ExternalPath = null,
    int? TrackId = null,
    string? TrackName = null)
{
    public static VideoSubtitleSelection None() => new(VideoSubtitleSelectionKind.None);

    public static VideoSubtitleSelection Off() => new(VideoSubtitleSelectionKind.Off);

    public static VideoSubtitleSelection ExternalFile(string path) =>
        new(VideoSubtitleSelectionKind.ExternalFile, ExternalPath: path);

    public static VideoSubtitleSelection EmbeddedTrack(int trackId, string? trackName = null) =>
        new(VideoSubtitleSelectionKind.EmbeddedTrack, TrackId: trackId, TrackName: trackName);
}

public sealed record VideoPlaybackState(
    double PositionSeconds,
    double DurationSeconds,
    VideoSubtitleSelection SubtitleSelection)
{
    public const double MinimumPersistablePositionSeconds = 5;

    public static VideoPlaybackState FromVideoItem(VideoItem video) =>
        new(
            NormalizeSeconds(video.LastPositionSeconds),
            NormalizeSeconds(video.DurationSeconds),
            video.GetSubtitleSelection());

    public static bool ShouldPersistProgress(TimeSpan position, TimeSpan duration)
    {
        if (!double.IsFinite(position.TotalSeconds) || position < TimeSpan.Zero)
            return false;

        if (position.TotalSeconds < MinimumPersistablePositionSeconds)
            return false;

        return true;
    }

    public TimeSpan? ResolveRestorePosition(TimeSpan actualDuration)
    {
        if (PositionSeconds <= 0)
            return null;

        var durationSeconds = actualDuration > TimeSpan.Zero
            ? actualDuration.TotalSeconds
            : DurationSeconds;
        if (durationSeconds > 0 && PositionSeconds >= durationSeconds - 2)
            return null;

        var max = durationSeconds > 0
            ? Math.Max(0, durationSeconds - 1)
            : PositionSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(PositionSeconds, 0, max));
    }

    private static double NormalizeSeconds(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;
}
