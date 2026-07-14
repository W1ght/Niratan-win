using System;
using System.Collections.Generic;
using System.Text;

namespace Niratan.Services.Video;

public static class VideoSubtitleHitTestResolver
{
    public static int ResolveCharacterIndex(
        string? text,
        int insertionOffset,
        double pointX,
        double pointY,
        double textWidth,
        double textHeight)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var insertionIndex = VideoSubtitleLookupTextExtractor.GetLookupOffsetAtInsertionOffset(text, insertionOffset);
        var visualIndex = EstimateSingleLineCharacterIndex(text, pointX, textWidth);
        if (visualIndex.HasValue
            && VideoSubtitleLookupTextExtractor.GetLookupOffset(text, visualIndex.Value) == visualIndex.Value
            && visualIndex.Value <= insertionIndex
            && insertionIndex - visualIndex.Value <= 2)
        {
            return visualIndex.Value;
        }

        return insertionIndex;
    }

    private static int? EstimateSingleLineCharacterIndex(string text, double pointX, double textWidth)
    {
        if (string.IsNullOrEmpty(text)
            || text.Contains('\n', StringComparison.Ordinal)
            || text.Contains('\r', StringComparison.Ordinal)
            || double.IsNaN(pointX)
            || double.IsInfinity(pointX)
            || double.IsNaN(textWidth)
            || double.IsInfinity(textWidth)
            || textWidth <= 0)
        {
            return null;
        }

        var runeStarts = GetRuneStarts(text);
        if (runeStarts.Count == 0)
            return null;

        var x = Math.Clamp(pointX, 0, Math.Max(0, textWidth - 0.01));
        var ordinal = (int)Math.Floor(x / textWidth * runeStarts.Count);
        ordinal = Math.Clamp(ordinal, 0, runeStarts.Count - 1);
        return runeStarts[ordinal];
    }

    private static List<int> GetRuneStarts(string text)
    {
        var starts = new List<int>();
        for (var i = 0; i < text.Length;)
        {
            starts.Add(i);
            i += Rune.GetRuneAt(text, i).Utf16SequenceLength;
        }

        return starts;
    }
}
