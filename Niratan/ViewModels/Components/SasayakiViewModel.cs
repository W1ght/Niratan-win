using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Models.Sasayaki;

namespace Niratan.ViewModels.Components;

public partial class SasayakiViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial double PlaybackRate { get; set; } = 1.0;

    [ObservableProperty]
    public partial string PositionText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string CurrentCueText { get; set; } = "";

    [ObservableProperty]
    public partial int CurrentCueIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int TotalCues { get; set; }

    [ObservableProperty]
    public partial int MatchedCues { get; set; }

    [ObservableProperty]
    public partial int UnmatchedCues { get; set; }

    [ObservableProperty]
    public partial bool HasMatchData { get; set; }

    [ObservableProperty]
    public partial string SasayakiStatusText { get; set; } = "No audiobook loaded";

    public IReadOnlyList<double> AvailablePlaybackRates { get; } =
        [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public void UpdateMatchStats(SasayakiMatchData data)
    {
        TotalCues = data.TotalCueCount;
        MatchedCues = data.Matches.Count;
        UnmatchedCues = data.Unmatched;
        HasMatchData = data.IsValid;
        SasayakiStatusText = data.IsValid
            ? $"{data.Matches.Count}/{data.TotalCueCount} cues matched"
            : "No cues matched";
    }

    public void UpdatePlaybackState(bool isPlaying, bool isPaused, double position, double duration)
    {
        IsPlaying = isPlaying;
        IsPaused = isPaused;
        PositionSeconds = position;
        DurationSeconds = duration;
        PositionText = FormatTime(position);
        DurationText = FormatTime(duration);
    }

    public void UpdateCurrentCue(SasayakiMatch? cue, int cueIndex)
    {
        if (cue != null)
        {
            CurrentCueText = cue.Text;
            CurrentCueIndex = cueIndex;
        }
        else
        {
            CurrentCueText = "";
            CurrentCueIndex = -1;
        }
    }

    public void SetPlaybackRate(double rate)
    {
        PlaybackRate = rate;
    }

    public void Reset()
    {
        IsLoaded = false;
        IsPlaying = false;
        IsPaused = false;
        PositionSeconds = 0;
        DurationSeconds = 0;
        PlaybackRate = 1.0;
        PositionText = "00:00";
        DurationText = "00:00";
        CurrentCueText = "";
        CurrentCueIndex = -1;
        TotalCues = 0;
        MatchedCues = 0;
        UnmatchedCues = 0;
        HasMatchData = false;
        SasayakiStatusText = "No audiobook loaded";
    }

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0)
            return "00:00";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
