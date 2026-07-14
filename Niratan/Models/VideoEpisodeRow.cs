using System;
using System.IO;

namespace Niratan.Models;

public sealed class VideoEpisodeRow
{
    public VideoEpisodeRow(VideoItem video, bool isCurrent)
    {
        Video = video;
        IsCurrent = isCurrent;
    }

    public VideoItem Video { get; }

    public string Title => string.IsNullOrWhiteSpace(Video.Title)
        ? Path.GetFileNameWithoutExtension(Video.FilePath)
        : Video.Title;

    public string FilePath => Video.FilePath;

    public bool IsCurrent { get; }

    public double CurrentIndicatorOpacity => IsCurrent ? 1 : 0;

    public string DurationText => Video.DurationSeconds > 0
        ? TimeSpan.FromSeconds(Video.DurationSeconds).ToString(@"hh\:mm\:ss")
        : "";

    public string AutomationName => IsCurrent
        ? $"{Title}, current episode"
        : Title;
}
