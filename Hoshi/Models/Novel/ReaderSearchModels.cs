using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hoshi.Models.Novel;

public sealed record ReaderSearchResult(
    int ChapterIndex,
    string ChapterLabel,
    int Character,
    double ChapterProgress,
    string Snippet,
    int SnippetMatchStart,
    int SnippetMatchEnd)
{
    public Guid Id { get; } = Guid.NewGuid();

    public string SnippetBeforeMatch => SliceSnippet(0, SnippetMatchStart);

    public string SnippetMatch => SliceSnippet(SnippetMatchStart, SnippetMatchEnd);

    public string SnippetAfterMatch => SliceSnippet(SnippetMatchEnd, int.MaxValue);

    private string SliceSnippet(int start, int end)
    {
        var elements = StringInfo.ParseCombiningCharacters(Snippet);
        var safeStart = Math.Clamp(start, 0, elements.Length);
        var safeEnd = Math.Clamp(end, safeStart, elements.Length);
        var startIndex = safeStart < elements.Length ? elements[safeStart] : Snippet.Length;
        var endIndex = safeEnd < elements.Length ? elements[safeEnd] : Snippet.Length;
        return Snippet[startIndex..endIndex];
    }
}

public sealed record ReaderSearchChapter(
    int Index,
    string Path,
    int CurrentTotal,
    int CharacterCount);

public sealed class ReaderSearchDocument
{
    public ReaderSearchDocument(
        IReadOnlyList<ReaderSearchChapter> chapters,
        IReadOnlyDictionary<string, string> htmlByPath,
        IReadOnlyDictionary<int, string> labels)
    {
        Chapters = chapters;
        HtmlByPath = htmlByPath;
        Labels = labels;
    }

    public IReadOnlyList<ReaderSearchChapter> Chapters { get; }
    public IReadOnlyDictionary<string, string> HtmlByPath { get; }
    public IReadOnlyDictionary<int, string> Labels { get; }
}
