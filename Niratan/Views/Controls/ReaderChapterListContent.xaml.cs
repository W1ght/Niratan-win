using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Niratan.Models.Novel;
using Niratan.Views.Dialogs;

namespace Niratan.Views.Controls;

public sealed partial class ReaderChapterListContent : UserControl
{
    private List<ChapterDisplayItem> _items = [];
    private int _currentListIndex = -1;

    public event EventHandler<int>? ChapterSelected;
    public event EventHandler<int>? CharacterJumpRequested;

    public int TotalCharacterCount { get; private set; }

    public ReaderChapterListContent()
    {
        InitializeComponent();
    }

    public void Load(
        List<EpubChapter> chapters,
        List<EpubTocItem> toc,
        int currentIndex,
        IReadOnlyList<int> chapterStartCharacterCounts,
        int currentCharacterCount,
        int totalCharacterCount)
    {
        _items = ReaderChapterListDialog.BuildChapterRows(
            chapters,
            toc,
            currentIndex,
            chapterStartCharacterCounts,
            currentCharacterCount);
        TotalCharacterCount = Math.Max(0, totalCharacterCount);
        CharacterJumpNumberBox.Maximum = Math.Max(0, TotalCharacterCount);
        CharacterJumpNumberBox.Value = Math.Clamp(currentCharacterCount, 0, Math.Max(0, TotalCharacterCount));
        CharacterJumpStatusText.Text = $"{currentCharacterCount:N0} / {TotalCharacterCount:N0}";
        ChapterListView.ItemsSource = _items;
        _currentListIndex = _items.FindIndex(i => i.IsCurrent);

        SelectCurrentChapter();
    }

    public void SelectCurrentChapter()
    {
        if (_currentListIndex < 0 || _currentListIndex >= _items.Count)
            return;

        var currentItem = _items[_currentListIndex];
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ChapterListView.SelectedIndex = _currentListIndex;
            ChapterListView.ScrollIntoView(currentItem);
            ChapterListView.UpdateLayout();
        });
    }

    private void ChapterListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChapterDisplayItem item)
            ChapterSelected?.Invoke(this, item.SpineIndex);
    }

    private void CharacterJumpButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var max = Math.Max(0, TotalCharacterCount);
        var rawValue = double.IsNaN(CharacterJumpNumberBox.Value)
            ? 0
            : CharacterJumpNumberBox.Value;
        var target = Math.Clamp((int)Math.Round(rawValue), 0, max);
        CharacterJumpRequested?.Invoke(this, target);
    }
}
