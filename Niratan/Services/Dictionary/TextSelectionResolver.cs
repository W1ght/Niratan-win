using System;
using System.Collections.Generic;
using Niratan.Models.Profiles;

namespace Niratan.Services.Dictionary;

public sealed record TextLookupCandidate(string Text, int Utf16Start);

public static class TextSelectionResolver
{
    private static readonly HashSet<char> EnglishScanDelimiters =
        new("\"“”„‟'‘’‚‛«»‹›!?—–-‐‑‒/\\|@#$%^&*_+=~`<>");
    private static readonly HashSet<char> EnglishWordInternalDelimiters =
        new("'’`-‐‑");
    private static readonly HashSet<char> SharedScanDelimiters =
        new("。、！？…‥「」『』（）()【】〈〉《》〔〕｛｝{}［］[]・：；:;，,.─\n\r");

    public static TextLookupCandidate? LookupCandidate(
        string sentence,
        int utf16Offset,
        int scanLength,
        ContentLanguageProfile contentLanguage)
    {
        if (utf16Offset < 0
            || utf16Offset >= sentence.Length
            || scanLength <= 0)
        {
            return null;
        }

        var candidateStart =
            contentLanguage.Id == ContentLanguageProfile.English.Id
                ? FindEnglishWordStart(sentence, utf16Offset)
                : utf16Offset;
        var length = Math.Min(scanLength, sentence.Length - candidateStart);
        var raw = sentence.Substring(candidateStart, length);
        var text = raw.Trim();
        if (text.Length == 0)
            return null;
        var leadingOffset = raw.IndexOf(text, StringComparison.Ordinal);
        return new TextLookupCandidate(
            text,
            candidateStart + Math.Max(0, leadingOffset));
    }

    private static int FindEnglishWordStart(string value, int utf16Offset)
    {
        var offset = utf16Offset;
        while (offset > 0 && !IsEnglishHitBoundary(value, offset - 1))
            offset--;
        return offset;
    }

    private static bool IsEnglishHitBoundary(string value, int offset)
    {
        var codeUnit = value[offset];
        if (char.IsWhiteSpace(codeUnit)
            || SharedScanDelimiters.Contains(codeUnit))
        {
            return true;
        }

        return EnglishScanDelimiters.Contains(codeUnit)
            && !IsInternalEnglishWordDelimiter(value, offset);
    }

    private static bool IsInternalEnglishWordDelimiter(
        string value,
        int offset) =>
        EnglishWordInternalDelimiters.Contains(value[offset])
        && offset > 0
        && offset + 1 < value.Length
        && IsAsciiAlphaNumeric(value[offset - 1])
        && IsAsciiAlphaNumeric(value[offset + 1]);

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= '0' and <= '9'
        or >= 'A' and <= 'Z'
        or >= 'a' and <= 'z';
}
