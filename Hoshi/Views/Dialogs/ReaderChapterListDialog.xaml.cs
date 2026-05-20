using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Hoshi.Models.Novel;

namespace Hoshi.Views.Dialogs;

public sealed class ChapterDisplayItem
{
    public string DisplayTitle { get; }
    public string Subtitle { get; }
    public int SpineIndex { get; }
    public bool IsCurrent { get; }
    public double DisplayOpacity => IsCurrent ? 1.0 : 0.55;

    public ChapterDisplayItem(string title, string subtitle, int spineIndex, int indentLevel, bool isCurrent)
    {
        var indent = new string(' ', indentLevel * 4);
        DisplayTitle = (string.IsNullOrWhiteSpace(title) ? "Untitled" : title);
        if (indentLevel > 0)
            DisplayTitle = indent + DisplayTitle;
        Subtitle = subtitle;
        SpineIndex = spineIndex;
        IsCurrent = isCurrent;
    }
}

public sealed partial class ReaderChapterListDialog : ContentDialog
{
    private readonly List<ChapterDisplayItem> _items;
    private int _selectedIndex = -1;

    public int SelectedChapterIndex => _selectedIndex;

    public ReaderChapterListDialog(List<EpubChapter> chapters, List<EpubTocItem> toc, int currentIndex)
    {
        _items = BuildChapterRows(chapters, toc, currentIndex);
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

    private static List<ChapterDisplayItem> BuildChapterRows(
        List<EpubChapter> chapters,
        List<EpubTocItem> toc,
        int currentIndex)
    {
        if (toc.Count > 0)
        {
            var rows = new List<ChapterDisplayItem>();
            foreach (var item in toc)
                FlattenToc(item, chapters, currentIndex, 0, rows);
            if (rows.Count > 0)
                return rows;
        }

        return chapters.Select((ch, i) =>
        {
            var filename = Path.GetFileNameWithoutExtension(ch.Href);
            return new ChapterDisplayItem(
                string.IsNullOrWhiteSpace(filename) ? $"Chapter {i + 1}" : filename,
                $"Chapter {i + 1}",
                i,
                0,
                i == currentIndex);
        }).ToList();
    }

    private static void FlattenToc(
        EpubTocItem item,
        List<EpubChapter> chapters,
        int currentIndex,
        int indentLevel,
        List<ChapterDisplayItem> rows)
    {
        var spineIndex = FindSpineIndex(item.Href, chapters);
        if (spineIndex >= 0)
        {
            rows.Add(new ChapterDisplayItem(
                item.Label,
                $"Chapter {spineIndex + 1}",
                spineIndex,
                indentLevel,
                spineIndex == currentIndex));
        }

        foreach (var child in item.Children)
            FlattenToc(child, chapters, currentIndex, indentLevel + 1, rows);
    }

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
