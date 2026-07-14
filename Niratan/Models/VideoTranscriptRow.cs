using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Niratan.Models;

public partial class VideoTranscriptRow : ObservableObject
{
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsABLoopStart { get; set; }

    [ObservableProperty]
    public partial bool IsABLoopEnd { get; set; }

    public VideoTranscriptRow(
        int index,
        TimeSpan start,
        TimeSpan end,
        string text,
        string startText)
    {
        Index = index;
        Start = start;
        End = end;
        Text = text;
        StartText = startText;
    }

    public int Index { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public string Text { get; }
    public string StartText { get; }
    public string AutomationName => $"{StartText} {Text}";
    public double CurrentIndicatorOpacity => IsCurrent ? 1 : 0.18;
    public double CurrentCardTintOpacity => IsCurrent ? 1 : 0;
    public double ABLoopStartMarkerOpacity => IsABLoopStart ? 1 : 0.32;
    public double ABLoopEndMarkerOpacity => IsABLoopEnd ? 1 : 0.32;

    public void SetABLoopMarkers(bool isStart, bool isEnd)
    {
        IsABLoopStart = isStart;
        IsABLoopEnd = isEnd;
    }

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentIndicatorOpacity));
        OnPropertyChanged(nameof(CurrentCardTintOpacity));
    }

    partial void OnIsABLoopStartChanged(bool value)
    {
        OnPropertyChanged(nameof(ABLoopStartMarkerOpacity));
    }

    partial void OnIsABLoopEndChanged(bool value)
    {
        OnPropertyChanged(nameof(ABLoopEndMarkerOpacity));
    }
}
