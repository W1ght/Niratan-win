using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Hoshi.Views.Video;

public sealed partial class VideoPlayerWindow
{
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
            ScrollCurrentEpisodeRowIntoView();
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
        PositionBottomChromeOverlay();
        PositionVideoHost();
        DispatcherQueue.TryEnqueue(() =>
        {
            RootGrid.UpdateLayout();
            PositionBottomChromeOverlay();
            PositionVideoHost();
        });
    }
}
