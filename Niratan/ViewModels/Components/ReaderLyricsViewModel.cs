using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;

namespace Niratan.ViewModels.Components;

public partial class ReaderLyricsViewModel : ObservableObject
{
    public IReadOnlyList<SasayakiMatch> Cues { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string? CoverPath { get; set; }

    [ObservableProperty]
    public partial int CurrentCueIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string CurrentCueText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial double DelaySeconds { get; set; }

    [ObservableProperty]
    public partial bool IsVertical { get; set; }

    [ObservableProperty]
    public partial bool IsMaskEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPointerOverLyrics { get; set; }

    [ObservableProperty]
    public partial bool IsLookupPopupVisible { get; set; }

    [ObservableProperty]
    public partial string? SelectedCueId { get; set; }

    [ObservableProperty]
    public partial int SelectionStart { get; set; } = -1;

    [ObservableProperty]
    public partial int SelectionLength { get; set; }

    [ObservableProperty]
    public partial bool ShowStatistics { get; set; }

    [ObservableProperty]
    public partial bool IsStatisticsTracking { get; set; }

    [ObservableProperty]
    public partial string StatisticsSummaryText { get; set; } = "";

    public SasayakiMatch? CurrentCue =>
        CurrentCueIndex >= 0 && CurrentCueIndex < Cues.Count
            ? Cues[CurrentCueIndex]
            : null;

    public double CurrentCueProgress => ReaderLyricsModeProjection.CueProgress(
        CurrentCue,
        PositionSeconds,
        DelaySeconds);

    public double PlaybackProgress => DurationSeconds > 0
        ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1)
        : 0;

    public bool ShouldBlurLyrics =>
        IsMaskEnabled && IsPlaying && !IsPointerOverLyrics && !IsLookupPopupVisible;

    public string PositionText => FormatTime(PositionSeconds);
    public string DurationText => FormatTime(DurationSeconds);
    public string PlaybackProgressText =>
        PlaybackProgress.ToString("P0", CultureInfo.CurrentCulture);

    public void Configure(
        string title,
        string? coverPath,
        IReadOnlyList<SasayakiMatch> cues,
        int currentCueIndex,
        double delaySeconds)
    {
        Title = title;
        CoverPath = coverPath;
        Cues = cues ?? [];
        DelaySeconds = delaySeconds;
        SetCurrentCue(currentCueIndex);
        OnPropertyChanged(nameof(Cues));
    }

    public void SetCurrentCue(int cueIndex)
    {
        CurrentCueIndex = cueIndex >= 0 && cueIndex < Cues.Count ? cueIndex : -1;
        CurrentCueText = CurrentCue?.Text ?? "";
        OnPropertyChanged(nameof(CurrentCue));
        OnPropertyChanged(nameof(CurrentCueProgress));
    }

    public void UpdatePlayback(
        bool isPlaying,
        double positionSeconds,
        double durationSeconds,
        int currentCueIndex)
    {
        IsPlaying = isPlaying;
        PositionSeconds = Math.Max(0, positionSeconds);
        DurationSeconds = Math.Max(0, durationSeconds);
        SetCurrentCue(currentCueIndex);
        NotifyPlaybackProjection();
    }

    public void SetSelection(string cueId, int start, int length)
    {
        SelectedCueId = cueId;
        SelectionStart = Math.Max(0, start);
        SelectionLength = Math.Max(0, length);
    }

    public void ClearSelection()
    {
        SelectedCueId = null;
        SelectionStart = -1;
        SelectionLength = 0;
    }

    public void UpdateStatistics(
        bool showStatistics,
        bool isTracking,
        string progressText,
        string speedText,
        string timeText)
    {
        ShowStatistics = showStatistics;
        IsStatisticsTracking = isTracking;
        StatisticsSummaryText = showStatistics
            ? $"{progressText}  •  {speedText}  •  {timeText}"
            : progressText;
    }

    partial void OnPositionSecondsChanged(double value) => NotifyPlaybackProjection();
    partial void OnDurationSecondsChanged(double value) => NotifyPlaybackProjection();
    partial void OnDelaySecondsChanged(double value) => OnPropertyChanged(nameof(CurrentCueProgress));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(ShouldBlurLyrics));
    partial void OnIsMaskEnabledChanged(bool value) => OnPropertyChanged(nameof(ShouldBlurLyrics));
    partial void OnIsPointerOverLyricsChanged(bool value) => OnPropertyChanged(nameof(ShouldBlurLyrics));
    partial void OnIsLookupPopupVisibleChanged(bool value) => OnPropertyChanged(nameof(ShouldBlurLyrics));

    private void NotifyPlaybackProjection()
    {
        OnPropertyChanged(nameof(CurrentCueProgress));
        OnPropertyChanged(nameof(PlaybackProgress));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlaybackProgressText));
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return "00:00";

        var value = TimeSpan.FromSeconds(seconds);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes:D2}:{value.Seconds:D2}";
    }
}
