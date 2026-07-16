using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Niratan.Models;
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
        var fileName = RemoteVideoIdentity.IsPersistenceKey(videoPath, YouTubeUrlParser.ProviderId)
            ? videoPath[(videoPath.LastIndexOf('/') + 1)..]
            : Path.GetFileName(videoPath);
        var title = RemoteVideoIdentity.IsPersistenceKey(videoPath, YouTubeUrlParser.ProviderId)
            ? fileName
            : Path.GetFileNameWithoutExtension(videoPath);

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

    public static MiningContextSelection? CreateSelection(
        IEnumerable<VideoSubtitleCue> cues,
        VideoSubtitleCue currentCue,
        int? targetUtf16Location)
    {
        var usable = cues
            .Where(cue => !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(cue => cue.Start)
            .ToArray();
        var currentIndex = Array.FindIndex(usable, cue =>
            cue.Index == currentCue.Index
            || (cue.Start == currentCue.Start
                && cue.End == currentCue.End
                && cue.Text == currentCue.Text));
        if (currentIndex < 0)
            return null;

        var sentences = usable.Select((cue, index) => new MiningContextSentence(
            $"{cue.Index}:{cue.Start.Ticks}",
            cue.Text,
            index == currentIndex ? Math.Max(0, targetUtf16Location ?? 0) : null,
            new MiningContextMediaRange(cue.Start, cue.End)));
        return new MiningContextSelection(sentences, currentIndex);
    }

    public static string FormatTimestamp(TimeSpan time)
    {
        var totalHours = (int)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
