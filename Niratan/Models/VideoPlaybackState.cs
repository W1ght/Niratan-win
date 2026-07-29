using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Models;

public enum VideoSubtitleSelectionKind
{
    None,
    ExternalFile,
    EmbeddedTrack,
    Off,
    RemoteLanguage,
}

public sealed record VideoSubtitleSelection(
    VideoSubtitleSelectionKind Kind,
    string? ExternalPath = null,
    int? TrackId = null,
    string? TrackName = null,
    string? RemoteLanguageCode = null)
{
    public static VideoSubtitleSelection None() => new(VideoSubtitleSelectionKind.None);

    public static VideoSubtitleSelection Off() => new(VideoSubtitleSelectionKind.Off);

    public static VideoSubtitleSelection ExternalFile(string path) =>
        new(VideoSubtitleSelectionKind.ExternalFile, ExternalPath: path);

    public static VideoSubtitleSelection EmbeddedTrack(int trackId, string? trackName = null) =>
        new(VideoSubtitleSelectionKind.EmbeddedTrack, TrackId: trackId, TrackName: trackName);

    public static VideoSubtitleSelection RemoteLanguage(string languageCode) =>
        new(VideoSubtitleSelectionKind.RemoteLanguage, RemoteLanguageCode: languageCode);
}

public enum VideoAudioSelectionKind
{
    None,
    EmbeddedTrack,
    Off,
}

public sealed record VideoAudioSelection(
    VideoAudioSelectionKind Kind,
    int? TrackId = null,
    int? FfIndex = null,
    string? Title = null,
    string? Language = null,
    string? Codec = null)
{
    public static VideoAudioSelection None() => new(VideoAudioSelectionKind.None);

    public static VideoAudioSelection Off() => new(VideoAudioSelectionKind.Off);

    public static VideoAudioSelection EmbeddedTrack(VideoTrackInfo track) =>
        new(
            VideoAudioSelectionKind.EmbeddedTrack,
            track.Id,
            track.FfIndex,
            track.Title,
            track.Language,
            track.Codec);

    public VideoTrackInfo? ResolveTrack(IReadOnlyList<VideoTrackInfo> tracks)
    {
        if (Kind != VideoAudioSelectionKind.EmbeddedTrack)
            return null;

        var track = FfIndex.HasValue
            ? tracks.FirstOrDefault(item => item.FfIndex == FfIndex)
            : tracks.FirstOrDefault(item => item.Id == TrackId);
        if (track != null)
            return track;

        var metadataMatches = tracks
            .Where(item =>
                string.Equals(item.Title, Title, StringComparison.Ordinal)
                && string.Equals(item.Language, Language, StringComparison.Ordinal)
                && string.Equals(item.Codec, Codec, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return metadataMatches.Count == 1 ? metadataMatches[0] : null;
    }
}

public sealed record VideoPlaybackState(
    double PositionSeconds,
    double DurationSeconds,
    VideoSubtitleSelection SubtitleSelection,
    int SubtitleDelayMilliseconds = 0,
    double PlaybackSpeed = 1,
    double AudioDelaySeconds = 0,
    VideoAudioSelection? AudioSelection = null)
{
    public const double MinimumPersistablePositionSeconds = 2;
    public const int MinimumSubtitleDelayMilliseconds = -10_000;
    public const int MaximumSubtitleDelayMilliseconds = 10_000;

    public static VideoPlaybackState FromVideoItem(VideoItem video) =>
        new(
            NormalizeSeconds(video.LastPositionSeconds),
            NormalizeSeconds(video.DurationSeconds),
            video.GetSubtitleSelection(),
            NormalizeSubtitleDelayMilliseconds(video.SubtitleDelayMilliseconds),
            NormalizePlaybackSpeed(video.PlaybackSpeed),
            NormalizeAudioDelaySeconds(video.AudioDelaySeconds),
            video.GetAudioSelection());

    public static int NormalizeSubtitleDelayMilliseconds(int value) =>
        Math.Clamp(
            value,
            MinimumSubtitleDelayMilliseconds,
            MaximumSubtitleDelayMilliseconds);

    public static double NormalizePlaybackSpeed(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.25, 5) : 1;

    public static double NormalizeAudioDelaySeconds(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -30, 30) : 0;

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
