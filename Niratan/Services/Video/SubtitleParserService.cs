using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Video;

public sealed record VideoSubtitleCue(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    string Text);

public sealed record VideoSubtitleCueContext(
    VideoSubtitleCue Cue,
    string PreviousText,
    string NextText);

public sealed class VideoSubtitleDocument
{
    private readonly List<VideoSubtitleCue> _cues;

    public VideoSubtitleDocument(IEnumerable<VideoSubtitleCue> cues)
    {
        _cues = cues
            .Where(cue => cue.End > cue.Start && !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(cue => cue.Start)
            .Select((cue, index) => cue with { Index = index })
            .ToList();
    }

    public IReadOnlyList<VideoSubtitleCue> Cues => _cues;

    public VideoSubtitleCue? FindCueAt(TimeSpan position) =>
        FindCuesAt(position).FirstOrDefault();

    public IReadOnlyList<VideoSubtitleCue> FindCuesAt(TimeSpan position)
    {
        if (_cues.Count == 0)
            return Array.Empty<VideoSubtitleCue>();

        var upperBound = FindFirstCueStartingAfter(position);
        if (upperBound == 0)
            return Array.Empty<VideoSubtitleCue>();

        var activeCues = new List<VideoSubtitleCue>();
        for (var index = upperBound - 1; index >= 0; index--)
        {
            var cue = _cues[index];
            if (position < cue.End)
                activeCues.Add(cue);
        }

        activeCues.Reverse();
        return activeCues;
    }

    private int FindFirstCueStartingAfter(TimeSpan position)
    {
        var low = 0;
        var high = _cues.Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_cues[mid].Start <= position)
            {
                low = mid + 1;
                continue;
            }

            high = mid;
        }

        return low;
    }

    public VideoSubtitleCueContext GetContext(VideoSubtitleCue cue)
    {
        var index = _cues.FindIndex(item => item.Index == cue.Index);
        if (index < 0)
            index = _cues.FindIndex(item => item.Start == cue.Start && item.End == cue.End && item.Text == cue.Text);

        var previous = index > 0 ? _cues[index - 1].Text : "";
        var next = index >= 0 && index + 1 < _cues.Count ? _cues[index + 1].Text : "";
        return new VideoSubtitleCueContext(cue, previous, next);
    }

    public VideoSubtitleCue? FindPreviousCue(TimeSpan position)
    {
        var current = FindCuesAt(position);
        if (current.Count > 0)
            return current[0].Index > 0 ? _cues[current[0].Index - 1] : null;

        for (var i = _cues.Count - 1; i >= 0; i--)
        {
            if (_cues[i].Start < position)
                return _cues[i];
        }

        return null;
    }

    public VideoSubtitleCue? FindNextCue(TimeSpan position)
    {
        var current = FindCuesAt(position);
        if (current.Count > 0)
        {
            var last = current[^1];
            return last.Index + 1 < _cues.Count ? _cues[last.Index + 1] : null;
        }

        for (var i = 0; i < _cues.Count; i++)
        {
            if (_cues[i].Start > position)
                return _cues[i];
        }

        return null;
    }
}

public sealed class SubtitleParserService
{
    private static readonly Regex TimingLinePattern = new(
        @"(?<start>(?:\d{1,2}:)?\d{2}:\d{2}[\.,]\d{1,3})\s+-->\s+(?<end>(?:\d{1,2}:)?\d{2}:\d{2}[\.,]\d{1,3})(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssOverrideTagPattern = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

    public VideoSubtitleDocument Parse(string subtitleText, string extension)
    {
        if (string.IsNullOrWhiteSpace(subtitleText))
            return new VideoSubtitleDocument([]);

        var normalizedExtension = extension.TrimStart('.').ToLowerInvariant();
        var document = normalizedExtension switch
        {
            "ass" or "ssa" => ParseAss(subtitleText),
            "vtt" => ParseTextBlocks(subtitleText, isWebVtt: true),
            _ => ParseTextBlocks(subtitleText, isWebVtt: false),
        };

        if (document.Cues.Count == 0)
            throw new InvalidDataException("No valid subtitle cues found.");

        return document;
    }

    public async Task<VideoSubtitleDocument> ParseFileAsync(string subtitlePath, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(subtitlePath, ct);
        var extension = Path.GetExtension(subtitlePath);
        return await Task.Run(() => Parse(text, extension), ct);
    }

    private static VideoSubtitleDocument ParseTextBlocks(string subtitleText, bool isWebVtt)
    {
        var cues = new List<VideoSubtitleCue>();
        var blocks = SplitBlocks(subtitleText);

        foreach (var block in blocks)
        {
            var lines = block
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (lines.Count == 0)
                continue;

            if (isWebVtt && IsWebVttMetadataBlock(lines[0]))
                continue;

            var timingIndex = lines.FindIndex(line => TimingLinePattern.IsMatch(line));
            if (timingIndex < 0)
                continue;

            var match = TimingLinePattern.Match(lines[timingIndex]);
            var start = ParseTimestamp(match.Groups["start"].Value);
            var end = ParseTimestamp(match.Groups["end"].Value);
            var text = CleanupSubtitleText(string.Join('\n', lines.Skip(timingIndex + 1)));
            if (string.IsNullOrWhiteSpace(text))
                continue;

            cues.Add(new VideoSubtitleCue(cues.Count, start, end, text));
        }

        return new VideoSubtitleDocument(cues);
    }

    private static VideoSubtitleDocument ParseAss(string subtitleText)
    {
        var cues = new List<VideoSubtitleCue>();
        var inEvents = false;
        var format = new List<string>();

        foreach (var rawLine in subtitleText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                inEvents = true;
                continue;
            }

            if (!inEvents)
                continue;

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                break;

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = trimmed["Format:".Length..]
                    .Split(',')
                    .Select(part => part.Trim())
                    .ToList();
                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || format.Count == 0)
                continue;

            var values = SplitAssDialogue(trimmed["Dialogue:".Length..], format.Count);
            var startIndex = FindAssField(format, "Start");
            var endIndex = FindAssField(format, "End");
            var textIndex = FindAssField(format, "Text");
            if (startIndex < 0 || endIndex < 0 || textIndex < 0 || values.Count <= Math.Max(endIndex, textIndex))
                continue;

            var text = CleanupAssText(values[textIndex]);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            cues.Add(new VideoSubtitleCue(
                cues.Count,
                ParseAssTimestamp(values[startIndex].Trim()),
                ParseAssTimestamp(values[endIndex].Trim()),
                text));
        }

        return new VideoSubtitleDocument(cues);
    }

    private static IReadOnlyList<string> SplitAssDialogue(string value, int fieldCount)
    {
        var fields = new List<string>(fieldCount);
        var start = 0;

        for (var index = 0; index < value.Length && fields.Count < fieldCount - 1; index++)
        {
            if (value[index] != ',')
                continue;

            fields.Add(value[start..index]);
            start = index + 1;
        }

        fields.Add(start <= value.Length ? value[start..] : "");
        return fields;
    }

    private static int FindAssField(IReadOnlyList<string> format, string name)
    {
        for (var i = 0; i < format.Count; i++)
        {
            if (string.Equals(format[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool IsWebVttMetadataBlock(string firstLine)
    {
        var trimmed = firstLine.Trim();
        return trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("REGION", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitBlocks(string text)
    {
        var normalized = text.Trim('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        return Regex.Split(normalized, @"\n\s*\n")
            .Select(block => block.Trim())
            .Where(block => block.Length > 0)
            .ToList();
    }

    private static TimeSpan ParseTimestamp(string value)
    {
        var normalized = value.Replace(',', '.');
        var parts = normalized.Split(':');
        var secondsParts = parts[^1].Split('.');
        var seconds = int.Parse(secondsParts[0]);
        var milliseconds = int.Parse(secondsParts.ElementAtOrDefault(1)?.PadRight(3, '0')[..3] ?? "0");

        if (parts.Length == 3)
        {
            var hours = int.Parse(parts[0]);
            var minutes = int.Parse(parts[1]);
            return new TimeSpan(0, hours, minutes, seconds, milliseconds);
        }

        return new TimeSpan(0, 0, int.Parse(parts[0]), seconds, milliseconds);
    }

    private static TimeSpan ParseAssTimestamp(string value) => ParseTimestamp(value);

    private static string CleanupAssText(string text)
    {
        var withoutTags = AssOverrideTagPattern.Replace(text, "");
        withoutTags = withoutTags
            .Replace("\\N", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal);
        return CleanupSubtitleText(withoutTags);
    }

    private static string CleanupSubtitleText(string text)
    {
        var withoutHtml = HtmlTagPattern.Replace(text, "");
        var decoded = WebUtility.HtmlDecode(withoutHtml);
        return string.Join(
            '\n',
            decoded.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
    }
}
