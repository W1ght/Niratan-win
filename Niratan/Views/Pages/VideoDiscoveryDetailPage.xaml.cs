using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Models.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class VideoDiscoveryDetailPage : Page, IDisposable
{
    public VideoDiscoveryDetailPageViewModel ViewModel { get; }
    private bool _disposed;

    public VideoDiscoveryDetailPage()
    {
        ViewModel = App.GetService<VideoDiscoveryDetailPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Unloaded += OnUnloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is VideoDiscoveryNavigationTarget target)
            await ViewModel.InitializeAsync(target);
        else if (Frame.CanGoBack)
            Frame.GoBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    private void SearchResourcesButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(
            typeof(VideoDiscoveryResourceSearchPage),
            new VideoDiscoveryResourceSearchTarget(
                ViewModel.AcquisitionTarget,
                VideoDiscoveryResourceRouteMode.Download));

    private void SearchSubtitlesButton_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(
            typeof(VideoDiscoverySubtitleSearchPage),
            ViewModel.AcquisitionTarget);

    private void SubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSubscribed)
        {
            ViewModel.OpenSubscriptionsCommand.Execute(null);
            return;
        }
        Frame.Navigate(
            typeof(VideoDiscoveryResourceSearchPage),
            new VideoDiscoveryResourceSearchTarget(
                ViewModel.AcquisitionTarget,
                VideoDiscoveryResourceRouteMode.Subscription));
    }

    private void RelatedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoDiscoveryNavigationTarget target })
            Frame.Navigate(typeof(VideoDiscoveryDetailPage), target);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        ViewModel.Dispose();
    }
}
