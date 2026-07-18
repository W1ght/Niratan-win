using System;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public readonly record struct ReaderLyricsCueWindow(int StartIndex, int EndIndex)
{
    public int Count => Math.Max(0, EndIndex - StartIndex);
}

public static class ReaderLyricsModeProjection
{
    public static bool CanEnter(
        bool sasayakiEnabled,
        bool hasAudio,
        SasayakiMatchData? matchData) =>
        sasayakiEnabled && hasAudio && matchData?.IsValid == true;

    public static double CueProgress(
        SasayakiMatch? cue,
        double playbackSeconds,
        double delaySeconds = 0)
    {
        if (cue == null || !double.IsFinite(playbackSeconds) || !double.IsFinite(delaySeconds))
            return 0;

        var duration = cue.EndTime - cue.StartTime;
        if (!double.IsFinite(duration) || duration <= 0)
            return playbackSeconds - delaySeconds >= cue.EndTime ? 1 : 0;

        return Math.Clamp(
            (playbackSeconds - delaySeconds - cue.StartTime) / duration,
            0,
            1);
    }

    public static ReaderLyricsCueWindow VisibleCueWindow(
        int cueCount,
        int currentIndex,
        double viewportHeight)
    {
        if (cueCount <= 0)
            return new ReaderLyricsCueWindow(0, 0);

        var safeIndex = Math.Clamp(currentIndex, 0, cueCount - 1);
        var radius = viewportHeight switch
        {
            < 560 => 1,
            < 720 => 2,
            < 880 => 3,
            _ => 4,
        };
        var start = Math.Max(0, safeIndex - radius);
        var end = Math.Min(cueCount, safeIndex + radius + 1);
        return new ReaderLyricsCueWindow(start, end);
    }
}
