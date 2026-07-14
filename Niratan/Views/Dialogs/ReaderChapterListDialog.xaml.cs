using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niratan.Models.Novel;

namespace Niratan.Views.Dialogs;

public sealed class ChapterDisplayItem
{
    public string DisplayTitle { get; }
    public string Subtitle { get; }
    public int SpineIndex { get; }
    public bool IsCurrent { get; }
    public int? CharacterCount { get; }
    public double DisplayOpacity => IsCurrent ? 1.0 : 0.55;
    public Visibility CurrentIndicatorVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public string CharacterCountText { get; }
    public Visibility CharacterCountVisibility => string.IsNullOrEmpty(CharacterCountText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public ChapterDisplayItem(
        string title,
        string subtitle,
        int spineIndex,
        int indentLevel,
        bool isCurrent,
        int? characterCount = null,
        string characterCountText = "")
    {
        var indent = new string(' ', indentLevel * 4);
        DisplayTitle = (string.IsNullOrWhiteSpace(title) ? "Untitled" : title);
        if (indentLevel > 0)
            DisplayTitle = indent + DisplayTitle;
        Subtitle = subtitle;
        SpineIndex = spineIndex;
        IsCurrent = isCurrent;
        CharacterCount = characterCount;
        CharacterCountText = characterCountText;
    }
}

public sealed partial class ReaderChapterListDialog : ContentDialog
{
    private readonly List<ChapterDisplayItem> _items;
    private int _selectedIndex = -1;

    public int SelectedChapterIndex => _selectedIndex;

    public ReaderChapterListDialog(List<EpubChapter> chapters, List<EpubTocItem> toc, int currentIndex)
    {
        _items = BuildChapterRows(chapters, toc, currentIndex, [], null);
        InitializeComponent();
        ChapterListView.ItemsSource = _items;

        var currentItem = _items.FirstOrDefault(i => i.IsCurrent);
        if (currentItem != null)
        {
            ChapterListView.SelectedItem = currentItem;
            ChapterListView.ScrollIntoView(currentItem);
        }
    }

    public static async Task<ReaderChapterListDialog> ShowAsync(
        XamlRoot xamlRoot,
        List<EpubChapter> chapters,
        List<EpubTocItem> toc,
        int currentIndex)
    {
        var dialog = new ReaderChapterListDialog(chapters, toc, currentIndex)
        {
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
        return dialog;
    }

    private void ChapterListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChapterDisplayItem item)
        {
            var match = _items.FirstOrDefault(i => i.SpineIndex == item.SpineIndex);
            if (match != null)
                _selectedIndex = match.SpineIndex;
            Hide();
        }
    }

    internal static List<ChapterDisplayItem> BuildChapterRows(
        List<EpubChapter> chapters,
        List<EpubTocItem> toc,
        int currentIndex,
        IReadOnlyList<int> chapterStartCharacterCounts,
        int? currentCharacterCount)
    {
        var selectedIndex = ResolveCurrentChapterIndex(
            chapters.Count,
            currentIndex,
            chapterStartCharacterCounts,
            currentCharacterCount);

        if (toc.Count > 0)
        {
            var rows = new List<ChapterDisplayItem>();
            foreach (var item in toc)
                FlattenToc(item, chapters, selectedIndex, chapterStartCharacterCounts, 0, rows);
            if (rows.Count > 0)
                return SelectCurrentVisibleRowByCharacter(rows, currentCharacterCount);
        }

        var chapterRows = chapters.Select((ch, i) =>
        {
            var filename = Path.GetFileNameWithoutExtension(ch.Href);
            var characterCount = GetChapterStartCharacterCount(chapterStartCharacterCounts, i);
            return new ChapterDisplayItem(
                string.IsNullOrWhiteSpace(filename) ? $"章节 {i + 1}" : filename,
                $"章节 {i + 1}",
                i,
                0,
                i == selectedIndex,
                characterCount,
                FormatCharacterCount(characterCount));
        }).ToList();

        return SelectCurrentVisibleRowByCharacter(chapterRows, currentCharacterCount);
    }

    private static void FlattenToc(
        EpubTocItem item,
        List<EpubChapter> chapters,
        int currentIndex,
        IReadOnlyList<int> chapterStartCharacterCounts,
        int indentLevel,
        List<ChapterDisplayItem> rows)
    {
        var spineIndex = FindSpineIndex(item.Href, chapters);
        if (spineIndex >= 0)
        {
            var characterCount = GetChapterStartCharacterCount(chapterStartCharacterCounts, spineIndex);
            rows.Add(new ChapterDisplayItem(
                item.Label,
                $"章节 {spineIndex + 1}",
                spineIndex,
                indentLevel,
                spineIndex == currentIndex,
                characterCount,
                FormatCharacterCount(characterCount)));
        }

        foreach (var child in item.Children)
            FlattenToc(child, chapters, currentIndex, chapterStartCharacterCounts, indentLevel + 1, rows);
    }

    private static List<ChapterDisplayItem> SelectCurrentVisibleRowByCharacter(
        List<ChapterDisplayItem> rows,
        int? currentCharacterCount)
    {
        if (!currentCharacterCount.HasValue || rows.Count == 0)
            return rows;

        var target = Math.Max(0, currentCharacterCount.Value);
        var currentRowIndex = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            var characterCount = rows[i].CharacterCount;
            if (!characterCount.HasValue)
                continue;

            if (characterCount.Value <= target)
                currentRowIndex = i;
            else
                break;
        }

        if (currentRowIndex < 0)
            return rows;

        return rows.Select((row, index) => new ChapterDisplayItem(
            row.DisplayTitle,
            row.Subtitle,
            row.SpineIndex,
            0,
            index == currentRowIndex,
            row.CharacterCount,
            row.CharacterCountText)).ToList();
    }

    private static int ResolveCurrentChapterIndex(
        int chapterCount,
        int fallbackIndex,
        IReadOnlyList<int> chapterStartCharacterCounts,
        int? currentCharacterCount)
    {
        if (chapterCount <= 0)
            return 0;

        var selected = Math.Clamp(fallbackIndex, 0, chapterCount - 1);
        if (!currentCharacterCount.HasValue || chapterStartCharacterCounts.Count == 0)
            return selected;

        var target = Math.Max(0, currentCharacterCount.Value);
        for (var i = 0; i < Math.Min(chapterCount, chapterStartCharacterCounts.Count); i++)
        {
            if (chapterStartCharacterCounts[i] <= target)
                selected = i;
            else
                break;
        }

        return selected;
    }

    private static int? GetChapterStartCharacterCount(IReadOnlyList<int> chapterStartCharacterCounts, int index)
    {
        if (index < 0 || index >= chapterStartCharacterCounts.Count)
            return null;

        return chapterStartCharacterCounts[index];
    }

    private static string FormatCharacterCount(int? count) =>
        count.HasValue ? count.Value.ToString("N0") : string.Empty;

    private static int FindSpineIndex(string? tocHref, List<EpubChapter> chapters)
    {
        if (string.IsNullOrWhiteSpace(tocHref))
            return -1;

        var tocPath = tocHref
            .Replace('\\', '/')
            .TrimStart('/')
            .Split('#')[0]
            .Split('?')[0];

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapterPath = chapters[i].Href
                .Replace('\\', '/')
                .TrimStart('/')
                .Split('#')[0]
                .Split('?')[0];

            if (string.Equals(tocPath, chapterPath, StringComparison.OrdinalIgnoreCase)
                || tocPath.EndsWith("/" + chapterPath, StringComparison.OrdinalIgnoreCase)
                || chapterPath.EndsWith("/" + tocPath, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
