using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class VideoLibraryPage : Page
{
    public VideoLibraryPageViewModel ViewModel { get; set; }

    public VideoLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<VideoLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItemViewModel videoItem)
            ViewModel.OpenVideoCommand.Execute(videoItem);
    }

    private void VideoButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is VideoItemViewModel videoItem)
            ViewModel.OpenVideoCommand.Execute(videoItem);
    }
}
