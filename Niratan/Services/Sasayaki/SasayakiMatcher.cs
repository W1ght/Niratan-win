using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public sealed partial class SasayakiMatcher
{
    private const int InitialCuesForOffset = 15;

    public async Task<SasayakiMatchData> MatchAsync(
        EpubBook book,
        List<SasayakiCue> cues,
        int searchWindow = SasayakiSettings.DefaultSearchWindow)
    {
        // Build global code point array and chapter ranges
        var (globalCodePoints, chapterRanges) = await BuildGlobalCodePointsAsync(book);

        if (globalCodePoints.Length == 0 || cues.Count == 0)
        {
            return new SasayakiMatchData
            {
                Matches = [],
                Unmatched = cues.Count,
            };
        }

        // Find initial offset using first N cues
        var cursor = FindInitialOffset(globalCodePoints, cues);

        // Match each cue sequentially
        var matches = new List<SasayakiMatch>();
        var unmatched = 0;

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var cueCodePoints = ToCodePoints(cue.Text);

            if (cueCodePoints.Length == 0)
            {
                unmatched++;
                continue;
            }

            // Niratan treats short asterisk-prefixed cues as narration/audio markers.
            // Matching their one or two common characters can advance the monotonic
            // cursor beyond the next real sentence and derail every later match.
            if (cue.Text.StartsWith('＊') && cueCodePoints.Length < 5)
            {
                unmatched++;
                continue;
            }

            var searchEnd = Math.Min(globalCodePoints.Length, cursor + cueCodePoints.Length + searchWindow);
            var matchPos = FindExactMatch(globalCodePoints, cueCodePoints, cursor, searchEnd);

            if (matchPos >= 0)
            {
                var range = FindChapterRange(chapterRanges, matchPos);
                if (range == null || matchPos + cueCodePoints.Length > range.StartCodePoint + range.Length)
                {
                    unmatched++;
                    continue;
                }

                matches.Add(new SasayakiMatch
                {
                    Id = cue.Id,
                    StartTime = cue.StartTime,
                    EndTime = cue.EndTime,
                    Text = cue.Text,
                    ChapterIndex = range.ChapterIndex,
                    Start = matchPos - range.StartCodePoint,
                    Length = cueCodePoints.Length,
                });
                cursor = matchPos + cueCodePoints.Length;
            }
            else
            {
                unmatched++;
            }
        }

        return new SasayakiMatchData
        {
            Matches = matches,
            Unmatched = unmatched,
        };
    }

    private static int FindInitialOffset(int[] globalCodePoints, List<SasayakiCue> cues)
    {
        var initialCues = cues.Take(InitialCuesForOffset).ToList();
        if (initialCues.Count == 0)
            return 0;

        int? earliestMatch = null;
        foreach (var cue in initialCues)
        {
            if (cue.Text.StartsWith('＊'))
                continue;

            var cueCodePoints = ToCodePoints(cue.Text);
            if (cueCodePoints.Length < 6)
                continue;

            var match = FindExactMatch(globalCodePoints, cueCodePoints, 0, globalCodePoints.Length);
            if (match >= 0)
                earliestMatch = Math.Min(earliestMatch ?? match, match);
        }

        return earliestMatch ?? 0;
    }

    private static int FindExactMatch(int[] text, int[] pattern, int start, int end)
    {
        var patternLen = pattern.Length;
        var searchEnd = Math.Min(text.Length - patternLen + 1, end);

        for (var i = start; i < searchEnd; i++)
        {
            var match = true;
            for (var j = 0; j < patternLen; j++)
            {
                if (text[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    private static ChapterCodePointRange? FindChapterRange(
        List<ChapterCodePointRange> ranges, int globalPosition)
    {
        foreach (var range in ranges)
        {
            if (globalPosition >= range.StartCodePoint
                && globalPosition < range.StartCodePoint + range.Length)
                return range;
        }

        return null;
    }

    private static async Task<(int[] CodePoints, List<ChapterCodePointRange> Ranges)> BuildGlobalCodePointsAsync(
        EpubBook book)
    {
        var allCodePoints = new List<int>();
        var ranges = new List<ChapterCodePointRange>();

        for (var i = 0; i < book.Chapters.Count; i++)
        {
            var chapter = book.Chapters[i];
            var start = allCodePoints.Count;

            if (File.Exists(chapter.Href))
            {
                var html = await File.ReadAllTextAsync(chapter.Href);
                var visibleText = ExtractVisibleText(html);
                var codePoints = ToCodePoints(visibleText);
                allCodePoints.AddRange(codePoints);
            }

            ranges.Add(new ChapterCodePointRange
            {
                ChapterIndex = i,
                StartCodePoint = start,
                Length = allCodePoints.Count - start,
            });
        }

        return (allCodePoints.ToArray(), ranges);
    }

    private static string ExtractVisibleText(string html)
    {
        var bodyMatch = BodyRegex().Match(html);
        if (bodyMatch.Success)
            html = bodyMatch.Groups["body"].Value;

        // Remove script and style elements
        html = ScriptOrStyleRegex().Replace(html, "");
        // Remove ruby annotations (rt/rp)
        html = RubyRegex().Replace(html, "");
        // Remove all HTML tags
        html = TagRegex().Replace(html, "");
        // Decode HTML entities
        html = WebUtility.HtmlDecode(html);

        return html;
    }

    public static int[] ToCodePoints(string text)
    {
        var codePoints = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var cp = char.ConvertToUtf32(text, i);
            if (IsReaderMatchableCodePoint(cp))
                codePoints.Add(cp);
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }

        return codePoints.ToArray();
    }

    private static bool IsReaderMatchableCodePoint(int cp) =>
        cp is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or 0x25CB
            or 0x25EF
            or >= 0x3005 and <= 0x3007
            or 0x303B
            or >= 0x3041 and <= 0x3096
            or >= 0x309D and <= 0x309E
            or >= 0x30A1 and <= 0x30FA
            or 0x30FC
            or >= 0xFF10 and <= 0xFF19
            or >= 0xFF21 and <= 0xFF3A
            or >= 0xFF41 and <= 0xFF5A
            or >= 0xFF66 and <= 0xFF9D
            or >= 0x2E80 and <= 0x2EFF
            or >= 0x2F00 and <= 0x2FDF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF
            or >= 0x2CEB0 and <= 0x2EBEF
            or >= 0x30000 and <= 0x323AF;

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"<body\b[^>]*>(?<body>.*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"<(rt|rp)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RubyRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
