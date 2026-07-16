using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Niratan.Views.Video;

public sealed partial class VideoPlayerWindow
{
    private const double InspectorMinimumWidth = 320;
    private const double InspectorMaximumWidth = 720;
    private const double MinimumVideoWidthWhileResizingInspector = 320;

    private void InspectorButton_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = !_isInspectorOpen;
        if (_isInspectorOpen)
        {
            InspectorPanel.Visibility = Visibility.Visible;
            SelectInspectorTab(_selectedInspectorTab);
        }
        else
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
        }

        RefreshVideoLayoutAfterInspectorChanged();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void InspectorCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = false;
        InspectorPanel.Visibility = Visibility.Collapsed;
        RefreshVideoLayoutAfterInspectorChanged();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void InspectorResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isInspectorOpen || RootGrid.ActualWidth <= 0)
            return;

        var currentWidth = double.IsNaN(InspectorPanel.Width)
            ? InspectorPanel.ActualWidth
            : InspectorPanel.Width;
        var availableWidth = RootGrid.ActualWidth - MinimumVideoWidthWhileResizingInspector;
        var maximumWidth = Math.Clamp(
            availableWidth,
            InspectorMinimumWidth,
            InspectorMaximumWidth);
        var nextWidth = Math.Clamp(
            currentWidth - e.HorizontalChange,
            InspectorMinimumWidth,
            maximumWidth);

        if (Math.Abs(nextWidth - currentWidth) < 0.5)
            return;

        InspectorPanel.Width = nextWidth;
        RootGrid.UpdateLayout();
        FitWindowToVideoAspectRatio();
        PositionBottomChromeOverlay();
        PositionVideoHost();
    }

    private void InspectorMiningHistoryTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.MiningHistory);

    private void InspectorSubtitleListTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.SubtitleList);

    private void InspectorChaptersTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.Chapters);

    private void InspectorVideoTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.Video);

    private void InspectorAudioTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.Audio);

    private void InspectorSubtitlesTabButton_Checked(object sender, RoutedEventArgs e) =>
        SelectInspectorTab(VideoInspectorTab.Subtitles);

    private void InspectorTabButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button && IsInspectorTabButtonSelected(button))
            button.IsChecked = true;
    }

    private bool IsInspectorTabButtonSelected(ToggleButton button) =>
        (button == InspectorMiningHistoryTabButton && _selectedInspectorTab == VideoInspectorTab.MiningHistory)
        || (button == InspectorSubtitleListTabButton && _selectedInspectorTab == VideoInspectorTab.SubtitleList)
        || (button == InspectorChaptersTabButton && _selectedInspectorTab == VideoInspectorTab.Chapters)
        || (button == InspectorVideoTabButton && _selectedInspectorTab == VideoInspectorTab.Video)
        || (button == InspectorAudioTabButton && _selectedInspectorTab == VideoInspectorTab.Audio)
        || (button == InspectorSubtitlesTabButton && _selectedInspectorTab == VideoInspectorTab.Subtitles);

    private void SelectInspectorTab(VideoInspectorTab tab)
    {
        _selectedInspectorTab = tab;

        InspectorMiningHistoryTabButton.IsChecked = tab == VideoInspectorTab.MiningHistory;
        InspectorSubtitleListTabButton.IsChecked = tab == VideoInspectorTab.SubtitleList;
        InspectorChaptersTabButton.IsChecked = tab == VideoInspectorTab.Chapters;
        InspectorVideoTabButton.IsChecked = tab == VideoInspectorTab.Video;
        InspectorAudioTabButton.IsChecked = tab == VideoInspectorTab.Audio;
        InspectorSubtitlesTabButton.IsChecked = tab == VideoInspectorTab.Subtitles;

        InspectorScrollableContent.Visibility = tab == VideoInspectorTab.SubtitleList
            ? Visibility.Collapsed
            : Visibility.Visible;
        InspectorMiningHistoryContent.Visibility = tab == VideoInspectorTab.MiningHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorSubtitleListContent.Visibility = tab == VideoInspectorTab.SubtitleList
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorChaptersContent.Visibility = tab == VideoInspectorTab.Chapters
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorVideoContent.Visibility = tab == VideoInspectorTab.Video
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorAudioContent.Visibility = tab == VideoInspectorTab.Audio
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorSubtitlesContent.Visibility = tab == VideoInspectorTab.Subtitles
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (tab == VideoInspectorTab.SubtitleList)
            ScrollCurrentTranscriptRowIntoView();
        else if (tab == VideoInspectorTab.Chapters)
            ScrollCurrentChapterRowIntoView();
    }

    private void OpenInspectorTab(VideoInspectorTab tab)
    {
        _isInspectorOpen = true;
        InspectorPanel.Visibility = Visibility.Visible;
        SelectInspectorTab(tab);
        RefreshVideoLayoutAfterInspectorChanged();
    }

    private void RefreshVideoLayoutAfterInspectorChanged()
    {
        RootGrid.UpdateLayout();
        FitWindowToVideoAspectRatio();
        PositionBottomChromeOverlay();
        PositionVideoHost();
        DispatcherQueue.TryEnqueue(() =>
        {
            RootGrid.UpdateLayout();
            FitWindowToVideoAspectRatio();
            PositionBottomChromeOverlay();
            PositionVideoHost();
        });
    }
}
