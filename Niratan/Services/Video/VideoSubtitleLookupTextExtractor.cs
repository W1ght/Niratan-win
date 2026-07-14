using System;
using System.Text;

namespace Niratan.Services.Video;

public static class VideoSubtitleLookupTextExtractor
{
    public const int DefaultScanLength = 16;

    public static string GetQueryAtCharacter(string? text, int characterIndex, int scanLength = DefaultScanLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var start = FindLookupStart(text, characterIndex);
        if (start < 0)
            return "";

        var end = TakeRunes(text, start, Math.Max(1, scanLength));
        return text[start..end];
    }

    public static string GetQueryAtInsertionOffset(string? text, int insertionOffset, int scanLength = DefaultScanLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var start = FindLookupStartFromInsertionOffset(text, insertionOffset);
        if (start < 0)
            return "";

        var end = TakeRunes(text, start, Math.Max(1, scanLength));
        return text[start..end];
    }

    public static int GetLookupOffset(string? text, int characterIndex)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var start = FindLookupStart(text, characterIndex);
        return start < 0 ? 0 : start;
    }

    public static int GetLookupOffsetAtInsertionOffset(string? text, int insertionOffset)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var start = FindLookupStartFromInsertionOffset(text, insertionOffset);
        return start < 0 ? 0 : start;
    }

    private static int FindLookupStartFromInsertionOffset(string text, int insertionOffset)
    {
        var right = Math.Clamp(insertionOffset, 0, text.Length);
        if (right < text.Length
            && right > 0
            && char.IsLowSurrogate(text[right])
            && char.IsHighSurrogate(text[right - 1]))
        {
            right--;
        }

        var left = PreviousRuneIndex(text, right);
        if (left >= 0 && IsLookupRune(Rune.GetRuneAt(text, left)))
            return left;

        if (right < text.Length && IsLookupRune(Rune.GetRuneAt(text, right)))
            return right;

        return FindLookupStart(text, right);
    }

    private static int FindLookupStart(string text, int characterIndex)
    {
        var index = Math.Clamp(characterIndex, 0, Math.Max(0, text.Length - 1));
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
            index--;

        for (var i = index; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            if (IsLookupRune(rune))
                return i;

            i += rune.Utf16SequenceLength;
        }

        for (var i = PreviousRuneIndex(text, index); i >= 0; i = PreviousRuneIndex(text, i))
        {
            if (IsLookupRune(Rune.GetRuneAt(text, i)))
                return i;
        }

        return -1;
    }

    private static int TakeRunes(string text, int start, int count)
    {
        var end = start;
        var taken = 0;
        while (end < text.Length && taken < count)
        {
            var rune = Rune.GetRuneAt(text, end);
            end += rune.Utf16SequenceLength;
            taken++;
        }

        return end;
    }

    private static int PreviousRuneIndex(string text, int index)
    {
        var previous = index - 1;
        if (previous <= 0)
            return previous;

        return char.IsLowSurrogate(text[previous]) && char.IsHighSurrogate(text[previous - 1])
            ? previous - 1
            : previous;
    }

    private static bool IsLookupRune(Rune rune)
    {
        var value = rune.Value;
        return value switch
        {
            >= 0x30 and <= 0x39 => true,
            >= 0x41 and <= 0x5A => true,
            >= 0x61 and <= 0x7A => true,
            0x25CB => true,
            >= 0x3005 and <= 0x3007 => true,
            0x303B => true,
            >= 0x3041 and <= 0x3096 => true,
            >= 0x309D and <= 0x309E => true,
            >= 0x30A1 and <= 0x30FA => true,
            0x30FC => true,
            >= 0xFF10 and <= 0xFF19 => true,
            >= 0xFF21 and <= 0xFF3A => true,
            >= 0xFF41 and <= 0xFF5A => true,
            >= 0xFF66 and <= 0xFF9D => true,
            >= 0x2E80 and <= 0x2FDF => true,
            >= 0x3400 and <= 0x4DBF => true,
            >= 0x4E00 and <= 0x9FFF => true,
            >= 0x20000 and <= 0x2A6DF => true,
            >= 0x2A700 and <= 0x2B73F => true,
            >= 0x2B740 and <= 0x2B81F => true,
            >= 0x2B820 and <= 0x2CEAF => true,
            >= 0x2CEB0 and <= 0x2EBEF => true,
            >= 0x30000 and <= 0x3134F => true,
            >= 0x31350 and <= 0x323AF => true,
            _ => false,
        };
    }
}
