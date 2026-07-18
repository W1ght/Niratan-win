using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;

namespace Niratan.ViewModels.Components;

public partial class SasayakiViewModel : ObservableObject
{
    public ObservableCollection<SasayakiChapterItemViewModel> Chapters { get; } = [];

    public bool HasChapters => Chapters.Count > 0;

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

    public void UpdateChapters(SasayakiMatchData data, EpubBook book)
    {
        Chapters.Clear();
        var tocItems = FlattenToc(book.Toc).ToArray();

        foreach (var group in data.Matches
                     .Where(match => match.ChapterIndex >= 0 && match.ChapterIndex < book.Chapters.Count)
                     .GroupBy(match => match.ChapterIndex)
                     .OrderBy(group => group.Key))
        {
            var chapter = book.Chapters[group.Key];
            var title = tocItems
                .FirstOrDefault(item => HrefMatches(chapter.Href, item.Href))
                ?.Label;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ResourceStringHelper.FormatString(
                    "NovelReaderSasayakiChapterFallback",
                    "Chapter {0}",
                    group.Key + 1);
            }

            Chapters.Add(new SasayakiChapterItemViewModel(
                group.Key,
                title,
                group.Min(match => match.StartTime)));
        }

        OnPropertyChanged(nameof(HasChapters));
        UpdateCurrentChapter(PositionSeconds);
    }

    public void UpdateCurrentChapter(double positionSeconds)
    {
        var current = Chapters
            .Where(chapter => chapter.StartTime <= positionSeconds)
            .LastOrDefault()
            ?? Chapters.FirstOrDefault();
        foreach (var chapter in Chapters)
            chapter.IsCurrent = ReferenceEquals(chapter, current);
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
        Chapters.Clear();
        OnPropertyChanged(nameof(HasChapters));
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

    private static IEnumerable<EpubTocItem> FlattenToc(IEnumerable<EpubTocItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in FlattenToc(item.Children))
                yield return child;
        }
    }

    private static bool HrefMatches(string chapterHref, string? tocHref)
    {
        if (string.IsNullOrWhiteSpace(tocHref))
            return false;

        var chapter = NormalizeHref(chapterHref);
        var toc = NormalizeHref(tocHref);
        return chapter.Equals(toc, StringComparison.OrdinalIgnoreCase)
            || chapter.EndsWith('/' + toc, StringComparison.OrdinalIgnoreCase)
            || toc.EndsWith('/' + chapter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHref(string href)
    {
        var fragment = href.IndexOf('#');
        if (fragment >= 0)
            href = href[..fragment];
        return Uri.UnescapeDataString(href.Replace('\\', '/').TrimStart('/'));
    }
}

public partial class SasayakiChapterItemViewModel(
    int chapterIndex,
    string title,
    double startTime) : ObservableObject
{
    public int ChapterIndex { get; } = chapterIndex;
    public string Title { get; } = title;
    public double StartTime { get; } = startTime;
    public string StartTimeText { get; } = FormatTime(startTime);

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }
}
