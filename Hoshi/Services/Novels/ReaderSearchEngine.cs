using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed class ReaderSearchEngine
{
    private readonly ReaderSearchIndex _index;

    public ReaderSearchEngine(ReaderSearchDocument document)
    {
        _index = new ReaderSearchIndex(document);
    }

    public IReadOnlyList<ReaderSearchResult> Search(string query, int maxResults = 1_000)
    {
        var queryElements = ReaderSearchTextFilter.FilteredSearchElements(query);
        if (queryElements.Count == 0 || maxResults <= 0)
            return [];

        var results = new List<ReaderSearchResult>();
        var fromIndex = 0;

        while (fromIndex <= _index.SearchElements.Count - queryElements.Count
            && results.Count < maxResults)
        {
            var matchStart = _index.IndexOf(queryElements, fromIndex);
            if (matchStart < 0)
                break;

            var matchEnd = matchStart + queryElements.Count;
            var chapter = _index.ChapterContaining(matchStart, matchEnd);
            if (chapter != null)
            {
                results.Add(_index.CreateResult(chapter, matchStart, queryElements.Count));
                fromIndex = matchEnd;
            }
            else
            {
                fromIndex = matchStart + 1;
            }
        }

        return results;
    }
}

internal static partial class ReaderSearchTextFilter
{
    public static bool HasMatchableText(string value) =>
        FilteredSearchElements(value).Count > 0;

    public static IReadOnlyList<string> FilteredSearchElements(string value) =>
        TextElements(VisibleText(value))
            .Where(IsReaderSearchMatchable)
            .ToList();

    public static string VisibleText(string html)
    {
        var text = BodyRegex().Match(html) is { Success: true } bodyMatch
            ? bodyMatch.Value
            : html;
        text = RubyAnnotationRegex().Replace(text, "");
        text = ScriptOrStyleRegex().Replace(text, "");
        text = TagRegex().Replace(text, "");
        return WebUtility.HtmlDecode(text);
    }

    public static IReadOnlyList<string> TextElements(string value)
    {
        var indexes = StringInfo.ParseCombiningCharacters(value);
        var elements = new List<string>(indexes.Length);
        for (var i = 0; i < indexes.Length; i++)
        {
            var start = indexes[i];
            var end = i + 1 < indexes.Length ? indexes[i + 1] : value.Length;
            elements.Add(value[start..end]);
        }

        return elements;
    }

    public static bool IsReaderSearchMatchable(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (IsReaderSearchMatchable(rune))
                return true;
        }

        return false;
    }

    private static bool IsReaderSearchMatchable(Rune rune)
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

    [GeneratedRegex("(?is)<body[^>]*>.*?</body>")]
    private static partial Regex BodyRegex();

    [GeneratedRegex("(?is)<r[tp][^>]*>.*?</r[tp]>")]
    private static partial Regex RubyAnnotationRegex();

    [GeneratedRegex("(?is)<(script|style)[^>]*>.*?</\\1>")]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}

internal sealed class ReaderSearchIndex
{
    private const int SnippetLeadingCharacters = 24;
    private const int SnippetTrailingCharacters = 48;

    private readonly IReadOnlyList<string> _displayElements;
    private readonly IReadOnlyList<int> _searchToDisplayOffsets;
    private readonly IReadOnlyList<ReaderSearchChapterRange> _chapters;
    private readonly IReadOnlyDictionary<int, string> _labels;

    public ReaderSearchIndex(ReaderSearchDocument document)
    {
        var builder = new ReaderSearchDocumentBuilder();
        var ranges = new List<ReaderSearchChapterRange>();

        foreach (var chapter in document.Chapters)
        {
            document.HtmlByPath.TryGetValue(chapter.Path, out var html);
            var bounds = builder.AppendChapter(html ?? "");
            ranges.Add(new ReaderSearchChapterRange(
                chapter.Index,
                chapter.CurrentTotal,
                chapter.CharacterCount,
                bounds.StartSearchCharacter,
                bounds.EndSearchCharacter,
                bounds.StartDisplayCharacter,
                bounds.EndDisplayCharacter));
        }

        SearchElements = builder.SearchElements;
        _displayElements = builder.DisplayElements;
        _searchToDisplayOffsets = builder.SearchToDisplayOffsets;
        _chapters = ranges;
        _labels = document.Labels;
    }

    public IReadOnlyList<string> SearchElements { get; }

    public int IndexOf(IReadOnlyList<string> queryElements, int startIndex)
    {
        for (var i = startIndex; i <= SearchElements.Count - queryElements.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < queryElements.Count; j++)
            {
                if (!string.Equals(
                    SearchElements[i + j],
                    queryElements[j],
                    StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    public ReaderSearchChapterRange? ChapterContaining(int searchStart, int searchEnd) =>
        _chapters.FirstOrDefault(chapter =>
            searchStart >= chapter.StartSearchCharacter
            && searchEnd <= chapter.EndSearchCharacter);

    public ReaderSearchResult CreateResult(
        ReaderSearchChapterRange chapter,
        int searchStart,
        int queryLength)
    {
        var displayMatchStart = _searchToDisplayOffsets[searchStart];
        var displayMatchEnd = _searchToDisplayOffsets[searchStart + queryLength - 1] + 1;
        var snippetStart = Math.Max(
            chapter.StartDisplayCharacter,
            displayMatchStart - SnippetLeadingCharacters);
        var snippetEnd = Math.Min(
            chapter.EndDisplayCharacter,
            displayMatchEnd + SnippetTrailingCharacters);
        var hasPrefix = snippetStart > chapter.StartDisplayCharacter;
        var hasSuffix = snippetEnd < chapter.EndDisplayCharacter;
        var prefix = hasPrefix ? "..." : "";
        var suffix = hasSuffix ? "..." : "";
        var body = string.Concat(_displayElements.Skip(snippetStart).Take(snippetEnd - snippetStart));
        var matchStart = prefix.Length + displayMatchStart - snippetStart;
        var character = chapter.CurrentTotal + searchStart - chapter.StartSearchCharacter;
        var chapterRelativeCharacter = character - chapter.CurrentTotal;
        var chapterProgress = chapter.CharacterCount > 0
            ? Math.Clamp(chapterRelativeCharacter / (double)chapter.CharacterCount, 0, 1)
            : 0;

        return new ReaderSearchResult(
            chapter.Index,
            LabelFor(chapter.Index),
            character,
            chapterProgress,
            prefix + body + suffix,
            matchStart,
            matchStart + displayMatchEnd - displayMatchStart);
    }

    private string LabelFor(int chapterIndex)
    {
        for (var index = chapterIndex; index >= 0; index--)
        {
            if (_labels.TryGetValue(index, out var label))
                return label;
        }

        return "";
    }
}

internal sealed record ReaderSearchChapterRange(
    int Index,
    int CurrentTotal,
    int CharacterCount,
    int StartSearchCharacter,
    int EndSearchCharacter,
    int StartDisplayCharacter,
    int EndDisplayCharacter);

internal sealed record ReaderSearchChapterBounds(
    int StartSearchCharacter,
    int EndSearchCharacter,
    int StartDisplayCharacter,
    int EndDisplayCharacter);

internal sealed class ReaderSearchDocumentBuilder
{
    private readonly List<string> _searchElements = [];
    private readonly List<string> _displayElements = [];
    private readonly List<int> _searchToDisplayOffsets = [];

    public IReadOnlyList<string> SearchElements => _searchElements;
    public IReadOnlyList<string> DisplayElements => _displayElements;
    public IReadOnlyList<int> SearchToDisplayOffsets => _searchToDisplayOffsets;

    public ReaderSearchChapterBounds AppendChapter(string html)
    {
        var startSearchCharacter = _searchElements.Count;
        var startDisplayCharacter = _displayElements.Count;
        var hasDisplayContent = false;
        var pendingWhitespace = false;

        foreach (var element in ReaderSearchTextFilter.TextElements(
            ReaderSearchTextFilter.VisibleText(html)))
        {
            if (string.IsNullOrEmpty(element))
                continue;

            if (element.All(char.IsWhiteSpace))
            {
                if (hasDisplayContent)
                    pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace)
            {
                AppendDisplayElement(" ");
                pendingWhitespace = false;
            }

            hasDisplayContent = true;
            var displayOffset = _displayElements.Count;
            AppendDisplayElement(element);
            if (ReaderSearchTextFilter.IsReaderSearchMatchable(element))
            {
                _searchElements.Add(element);
                _searchToDisplayOffsets.Add(displayOffset);
            }
        }

        return new ReaderSearchChapterBounds(
            startSearchCharacter,
            _searchElements.Count,
            startDisplayCharacter,
            _displayElements.Count);
    }

    private void AppendDisplayElement(string element) => _displayElements.Add(element);
}
