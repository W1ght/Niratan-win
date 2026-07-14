using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Niratan.Models;
using Microsoft.UI.Xaml;

namespace Niratan.Views.Video;

public sealed partial class VideoPlayerWindow
{
    private void OpenTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        OpenInspectorTab(VideoInspectorTab.SubtitleList);
        ViewModel.RefreshTranscriptWindowForCurrentRow();
        InspectorSubtitleListContent.FocusList();
        ScrollCurrentTranscriptRowIntoView(refreshWindow: false);
    }

    private async void InspectorSubtitleListContent_TranscriptSelected(
        object? sender,
        VideoTranscriptRowEventArgs e)
    {
        await SeekToAsync(e.Row.Start);
    }

    private async void InspectorSubtitleListContent_SetABLoopStartRequested(
        object? sender,
        VideoTranscriptRowEventArgs e)
    {
        await SetABLoopStartAsync(e.Row.Start);
    }

    private async void InspectorSubtitleListContent_SetABLoopEndRequested(
        object? sender,
        VideoTranscriptRowEventArgs e)
    {
        await SetABLoopEndAsync(e.Row.End);
    }

    private void TranscriptVisibleRows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var shouldScroll = false;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in _subscribedTranscriptRows)
                row.PropertyChanged -= TranscriptRow_PropertyChanged;
            _subscribedTranscriptRows.Clear();
        }

        if (e.OldItems != null)
        {
            foreach (var row in e.OldItems.OfType<VideoTranscriptRow>())
            {
                row.PropertyChanged -= TranscriptRow_PropertyChanged;
                _subscribedTranscriptRows.Remove(row);
            }
        }

        if (e.NewItems != null)
        {
            foreach (var row in e.NewItems.OfType<VideoTranscriptRow>())
            {
                if (_subscribedTranscriptRows.Add(row))
                    row.PropertyChanged += TranscriptRow_PropertyChanged;
                shouldScroll |= row.IsCurrent;
            }
        }

        UpdateTranscriptListVisibility();
        if (shouldScroll)
            ScrollCurrentTranscriptRowIntoView(refreshWindow: false);
    }

    private void TranscriptRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoTranscriptRow.IsCurrent)
            && sender is VideoTranscriptRow { IsCurrent: true } row)
        {
            InspectorSubtitleListContent.ScrollRowIntoView(row);
        }
    }

    private void ScrollCurrentTranscriptRowIntoView(bool refreshWindow = true)
    {
        if (refreshWindow)
            ViewModel.RefreshTranscriptWindowForCurrentRow();

        InspectorSubtitleListContent.ScrollCurrentRowIntoView();
    }

    private void UpdateTranscriptListVisibility()
    {
        InspectorSubtitleListContent.UpdateListVisibility();
    }
}
