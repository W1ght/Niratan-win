using Hoshi.Models;

namespace Hoshi.ViewModels.Components;

public sealed class VideoItemViewModel
{
    public VideoItemViewModel(VideoItem video)
    {
        Video = video;
    }

    public VideoItem Video { get; }

    public string AutomationId => $"VideoItem_{Video.Id}";

    public string SubtitleStatus => string.IsNullOrWhiteSpace(Video.SubtitlePath)
        ? "No subtitles"
        : "Subtitles";

    public string ProgressText => Video.DurationSeconds <= 0
        ? ""
        : $"{Video.LastPositionSeconds / Video.DurationSeconds:P0}";
}
