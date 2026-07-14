using System;

namespace Niratan.Models;

public sealed record VideoChapter(int Id, string Title, TimeSpan StartTime);

public sealed class VideoChapterRow
{
    public VideoChapterRow(VideoChapter chapter, bool isCurrent)
    {
        Chapter = chapter;
        IsCurrent = isCurrent;
    }

    public VideoChapter Chapter { get; }
    public int Id => Chapter.Id;
    public string Title => Chapter.Title;
    public TimeSpan StartTime => Chapter.StartTime;
    public string StartTimeText => VideoTimeText.Format(StartTime);
    public bool IsCurrent { get; private set; }
    public double CurrentIndicatorOpacity => IsCurrent ? 1 : 0;
    public string AutomationName => IsCurrent ? $"{Title}, current chapter" : Title;

    public void SetCurrent(bool isCurrent) => IsCurrent = isCurrent;
}

internal static class VideoTimeText
{
    public static string Format(TimeSpan time)
    {
        var totalHours = (int)Math.Floor(Math.Max(0, time.TotalHours));
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
