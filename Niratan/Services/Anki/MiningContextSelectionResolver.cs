using System;
using System.Linq;
using Niratan.Models.Anki;

namespace Niratan.Services.Anki;

public static class MiningContextSelectionResolver
{
    public static AnkiMiningContext Apply(
        AnkiMiningContext source,
        MiningContextSelection selection,
        MiningContextSelectionRange range)
    {
        var lower = Math.Clamp(range.LowerBound, 0, selection.CurrentIndex);
        var upper = Math.Clamp(range.UpperBound, selection.CurrentIndex, selection.Sentences.Count - 1);
        var selected = selection.Sentences.Skip(lower).Take(upper - lower + 1).ToArray();
        var sentence = string.Join('\n', selected.Select(item => item.Text));
        var target = selection.Sentences[selection.CurrentIndex];
        var targetPrefixLength = selected
            .Take(selection.CurrentIndex - lower)
            .Sum(item => item.Text.Length + 1);

        var result = Clone(source);
        result.Sentence = sentence;
        result.SentenceOffset = target.TargetUtf16Location is int location
            ? targetPrefixLength + Math.Clamp(location, 0, target.Text.Length)
            : null;

        var mediaRanges = selected
            .Select(item => item.MediaRange)
            .Where(item => item != null)
            .Cast<MiningContextMediaRange>()
            .ToArray();
        if (mediaRanges.Length > 0)
        {
            var start = mediaRanges.Min(item => item.Start);
            var end = mediaRanges.Max(item => item.End);
            result.VideoSubtitle = sentence;
            result.VideoCueStart = Niratan.Services.Video.VideoMiningContextFactory.FormatTimestamp(start);
            result.VideoCueEnd = Niratan.Services.Video.VideoMiningContextFactory.FormatTimestamp(end);
            result.VideoPreviousSubtitle = lower > 0 ? selection.Sentences[lower - 1].Text : "";
            result.VideoNextSubtitle = upper + 1 < selection.Sentences.Count
                ? selection.Sentences[upper + 1].Text
                : "";
        }

        return result;
    }

    private static AnkiMiningContext Clone(AnkiMiningContext source) => new()
    {
        Sentence = source.Sentence,
        DocumentTitle = source.DocumentTitle,
        CoverPath = source.CoverPath,
        CoverTag = source.CoverTag,
        SasayakiAudioPath = source.SasayakiAudioPath,
        SasayakiAudioTag = source.SasayakiAudioTag,
        SentenceOffset = source.SentenceOffset,
        VideoFileName = source.VideoFileName,
        VideoTimestamp = source.VideoTimestamp,
        VideoCueStart = source.VideoCueStart,
        VideoCueEnd = source.VideoCueEnd,
        VideoSubtitle = source.VideoSubtitle,
        VideoPreviousSubtitle = source.VideoPreviousSubtitle,
        VideoNextSubtitle = source.VideoNextSubtitle,
        VideoScreenshotPath = source.VideoScreenshotPath,
        VideoAudioClipPath = source.VideoAudioClipPath,
        VideoScreenshotTag = source.VideoScreenshotTag,
        VideoAudioClipTag = source.VideoAudioClipTag,
        VideoMediaProvider = source.VideoMediaProvider,
        SasayakiAudioProvider = source.SasayakiAudioProvider,
        SasayakiPopupControls = source.SasayakiPopupControls,
        ContextSelection = source.ContextSelection,
    };
}
