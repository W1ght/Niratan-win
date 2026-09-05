using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Models.Video;
using Niratan.ViewModels.Pages;
using Windows.System;

namespace Niratan.Views.Pages;

public sealed partial class VideoDiscoveryResourceSearchPage : Page, IDisposable
{
    public VideoDiscoveryResourceSearchPageViewModel ViewModel { get; }
    private bool _disposed;

    public VideoDiscoveryResourceSearchPage()
    {
        ViewModel = App.GetService<VideoDiscoveryResourceSearchPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Unloaded += OnUnloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is VideoDiscoveryResourceSearchTarget target)
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

    private void ResourceSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.SearchCommand.CanExecute(null))
            ViewModel.SearchCommand.Execute(null);
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
