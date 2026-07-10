using System;
using System.Linq;
using Hoshi.Models;
using Hoshi.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Hoshi.Views.Video;

public sealed partial class VideoTranscriptListControl : UserControl
{
    private const int TranscriptWindowEdgeThreshold = 6;
    private const int MaxCenterAttempts = 8;

    private VideoPlayerViewModel? _viewModel;
    private bool _isTranscriptWindowExpansionQueued;
    private int _scrollRequestVersion;

    public VideoTranscriptListControl()
    {
        InitializeComponent();
    }

    public event EventHandler<VideoTranscriptRowEventArgs>? TranscriptSelected;
    public event EventHandler<VideoTranscriptRowEventArgs>? SetABLoopStartRequested;
    public event EventHandler<VideoTranscriptRowEventArgs>? SetABLoopEndRequested;

    public void Initialize(VideoPlayerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        UpdateListVisibility();
    }

    public void FocusList()
    {
        TranscriptListView.Focus(FocusState.Programmatic);
    }

    public void ScrollCurrentRowIntoView()
    {
        if (TranscriptListView.Visibility != Visibility.Visible || _viewModel == null)
            return;

        var currentRow = _viewModel.TranscriptVisibleRows.FirstOrDefault(row => row.IsCurrent)
            ?? _viewModel.TranscriptRows.FirstOrDefault(row => row.IsCurrent);
        if (currentRow != null)
            ScrollRowIntoView(currentRow);
    }

    public void ScrollRowIntoView(VideoTranscriptRow row)
    {
        if (TranscriptListView.Visibility != Visibility.Visible)
            return;

        if (_viewModel != null && !_viewModel.TranscriptVisibleRows.Contains(row))
            _viewModel.RefreshTranscriptWindowForCurrentRow();

        var requestVersion = ++_scrollRequestVersion;
        QueueCenterRowInViewport(row, requestVersion, attempt: 0);
    }

    public void UpdateListVisibility()
    {
        var hasRows = _viewModel?.TranscriptRows.Count > 0;
        TranscriptListView.Visibility = hasRows
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranscriptEmptyText.Visibility = hasRows
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void TranscriptListView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (_viewModel == null || args.Item is not VideoTranscriptRow row)
            return;

        if (_viewModel.TranscriptVisibleRows.Count == 0)
            return;

        var firstIndex = _viewModel.TranscriptVisibleRows[0].Index;
        var lastIndex = _viewModel.TranscriptVisibleRows[^1].Index;
        if (row.Index - firstIndex <= TranscriptWindowEdgeThreshold)
            QueueTranscriptWindowExpansion(towardStart: true);
        else if (lastIndex - row.Index <= TranscriptWindowEdgeThreshold)
            QueueTranscriptWindowExpansion(towardStart: false);
    }

    private void QueueTranscriptWindowExpansion(bool towardStart)
    {
        if (_isTranscriptWindowExpansionQueued)
            return;

        _isTranscriptWindowExpansionQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _isTranscriptWindowExpansionQueued = false;
            if (_viewModel == null)
                return;

            if (towardStart)
                _viewModel.ExpandTranscriptWindowTowardStart();
            else
                _viewModel.ExpandTranscriptWindowTowardEnd();
        });
    }

    private void QueueCenterRowInViewport(VideoTranscriptRow row, int requestVersion, int attempt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (requestVersion != _scrollRequestVersion)
                return;

            if (_viewModel != null && !_viewModel.TranscriptVisibleRows.Contains(row))
                _viewModel.RefreshTranscriptWindowForCurrentRow();

            TranscriptListView.ScrollIntoView(row, ScrollIntoViewAlignment.Default);
            if (TryCenterRowInViewport(row))
                return;

            if (attempt < MaxCenterAttempts)
                QueueCenterRowInViewport(row, requestVersion, attempt + 1);
        });
    }

    private bool TryCenterRowInViewport(VideoTranscriptRow row)
    {
        if (TranscriptListView.ContainerFromItem(row) is not ListViewItem container)
            return false;

        var scrollViewer = FindDescendant<ScrollViewer>(TranscriptListView);
        if (scrollViewer == null || scrollViewer.ViewportHeight <= 0)
            return false;

        if (container.ActualHeight <= 0)
            return false;

        var bounds = container
            .TransformToVisual(scrollViewer)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        var centeredOffset = scrollViewer.VerticalOffset
            + bounds.Top
            - ((scrollViewer.ViewportHeight - bounds.Height) / 2);
        var targetOffset = Math.Clamp(centeredOffset, 0, scrollViewer.ScrollableHeight);
        scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
        return true;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;

            var descendant = FindDescendant<T>(child);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private void TranscriptCardBody_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoTranscriptRow row })
            TranscriptSelected?.Invoke(this, new VideoTranscriptRowEventArgs(row));
    }

    private void TranscriptActions_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void TranscriptSetABLoopStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoTranscriptRow row })
            SetABLoopStartRequested?.Invoke(this, new VideoTranscriptRowEventArgs(row));
    }

    private void TranscriptSetABLoopEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoTranscriptRow row })
            SetABLoopEndRequested?.Invoke(this, new VideoTranscriptRowEventArgs(row));
    }
}

public sealed class VideoTranscriptRowEventArgs(VideoTranscriptRow row) : EventArgs
{
    public VideoTranscriptRow Row { get; } = row;
}
