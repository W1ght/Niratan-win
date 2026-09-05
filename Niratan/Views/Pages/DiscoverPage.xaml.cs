using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Models.Video;
using Niratan.ViewModels.Pages;
using Windows.System;

namespace Niratan.Views.Pages;

public sealed partial class DiscoverPage : Page, IDisposable
{
    public static readonly DependencyProperty IsEmbeddedProperty = DependencyProperty.Register(
        nameof(IsEmbedded),
        typeof(bool),
        typeof(DiscoverPage),
        new PropertyMetadata(false));

    public DiscoverPageViewModel ViewModel { get; }
    private readonly HashSet<ScrollViewer> _horizontalVideoLists = [];
    private bool _disposed;

    public bool IsEmbedded
    {
        get => (bool)GetValue(IsEmbeddedProperty);
        private set => SetValue(IsEmbeddedProperty, value);
    }

    public DiscoverPage()
    {
        ViewModel = App.GetService<DiscoverPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        IsEmbedded = e.Parameter is true;
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private void VideoSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.SearchVideosCommand.CanExecute(null))
            ViewModel.SearchVideosCommand.Execute(null);
    }

    private void VideoContentScrollViewer_ViewChanged(
        object sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
            return;
        var scrollViewer = (ScrollViewer)sender;
        if (scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > 700)
            return;
        if (ViewModel.LoadMoreCommand.CanExecute(null))
            ViewModel.LoadMoreCommand.Execute(null);
    }

    private void DiscoveryCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoDiscoveryNavigationTarget target })
            Frame.Navigate(typeof(VideoDiscoveryDetailPage), target);
    }

    private void HorizontalVideoList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || !_horizontalVideoLists.Add(scrollViewer))
            return;

        scrollViewer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(HorizontalVideoList_PointerWheelChanged),
            true);
    }

    private void HorizontalVideoList_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer horizontalScrollViewer)
            return;

        var delta = e.GetCurrentPoint(horizontalScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            var verticalScrollViewer = FindVerticalScrollViewer(
                VisualTreeHelper.GetParent(horizontalScrollViewer));
            if (verticalScrollViewer is null)
                return;

            var verticalTarget = Math.Clamp(
                verticalScrollViewer.VerticalOffset - delta,
                0,
                verticalScrollViewer.ScrollableHeight);
            verticalScrollViewer.ChangeView(
                null,
                verticalTarget,
                null,
                disableAnimation: false);
            e.Handled = true;
            return;
        }

        if (horizontalScrollViewer.ScrollableWidth <= 0)
            return;

        var target = Math.Clamp(
            horizontalScrollViewer.HorizontalOffset - delta,
            0,
            horizontalScrollViewer.ScrollableWidth);
        if (Math.Abs(target - horizontalScrollViewer.HorizontalOffset) < 0.5)
        {
            e.Handled = true;
            return;
        }

        horizontalScrollViewer.ChangeView(
            target,
            null,
            null,
            disableAnimation: false);
        e.Handled = true;
    }

    private static ScrollViewer? FindVerticalScrollViewer(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer && scrollViewer.ScrollableHeight > 0)
                return scrollViewer;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ViewModel.Dispose();
    }
}
