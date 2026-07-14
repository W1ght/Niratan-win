using System;
using System.IO;
using Niratan.Models.Anki;

namespace Niratan.Services.Video;

public static class VideoMiningContextFactory
{
    public static AnkiMiningContext Create(
        string videoPath,
        TimeSpan timestamp,
        VideoSubtitleCueContext cueContext,
        string? screenshotPath = null,
        string? audioClipPath = null,
        int? sentenceOffset = null)
    {
        var fileName = Path.GetFileName(videoPath);
        var title = Path.GetFileNameWithoutExtension(videoPath);

        return new AnkiMiningContext
        {
            Sentence = cueContext.Cue.Text,
            SentenceOffset = sentenceOffset,
            DocumentTitle = title,
            VideoFileName = fileName,
            VideoTimestamp = FormatTimestamp(timestamp),
            VideoCueStart = FormatTimestamp(cueContext.Cue.Start),
            VideoCueEnd = FormatTimestamp(cueContext.Cue.End),
            VideoSubtitle = cueContext.Cue.Text,
            VideoPreviousSubtitle = cueContext.PreviousText,
            VideoNextSubtitle = cueContext.NextText,
            VideoScreenshotPath = screenshotPath,
            VideoAudioClipPath = audioClipPath,
        };
    }

    public static string FormatTimestamp(TimeSpan time)
    {
        var totalHours = (int)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
