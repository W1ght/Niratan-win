using Niratan.Models.Video;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Niratan.Views.Pages;

public sealed partial class VideoLibraryPage : Page
{
    public VideoLibraryPageViewModel ViewModel { get; set; }

    public VideoLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<VideoLibraryPageViewModel>();
        DataContext = ViewModel;
        SetSelectedNavigationItem(ViewModel.SelectedLibraryView);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        SetSelectedNavigationItem(ViewModel.SelectedLibraryView);
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

    private void ListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItemViewModel videoItem)
            ViewModel.OpenVideoCommand.Execute(videoItem);
    }

    private void VideoButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is VideoItemViewModel videoItem)
            ViewModel.OpenVideoCommand.Execute(videoItem);
    }

    private void VideoLibrarySecondaryNavigationView_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string viewName)
            return;

        ViewModel.SelectLibraryViewCommand.Execute(viewName);
        SetSelectedNavigationItem(ViewModel.SelectedLibraryView);
    }

    private void CreateSmartCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        CreateSmartCollectionDialog.XamlRoot = XamlRoot;
        _ = CreateSmartCollectionDialog.ShowAsync();
    }

    private void CreateSmartCollectionSecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        CreateSmartCollectionDialog.Hide();
    }

    private void CreateSmartCollectionPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        CreateSmartCollectionDialog.Hide();
    }

    private void SetSelectedNavigationItem(VideoLibraryView view)
    {
        VideoLibrarySecondaryNavigationView.SelectedItem = view switch
        {
            VideoLibraryView.ContinueWatching => VideoLibraryContinueWatchingNavItem,
            VideoLibraryView.Watched => VideoLibraryFinishedNavItem,
            VideoLibraryView.Unwatched => VideoLibraryUnwatchedNavItem,
            VideoLibraryView.Finished => VideoLibraryFinishedNavItem,
            VideoLibraryView.Recent => VideoLibraryRecentNavItem,
            VideoLibraryView.NeedsReview => VideoLibraryNeedsReviewNavItem,
            VideoLibraryView.Favorites => VideoLibraryFavoritesNavItem,
            VideoLibraryView.Series => VideoLibrarySeriesNavItem,
            VideoLibraryView.Folders => VideoLibraryFoldersNavItem,
            VideoLibraryView.Collections => VideoLibraryCollectionsNavItem,
            VideoLibraryView.Tags => VideoLibraryTagsNavItem,
            _ => VideoLibraryAllNavItem,
        };
    }
}
