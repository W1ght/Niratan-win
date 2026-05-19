using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class NovelLibraryPage : Page
{
    public NovelLibraryPageViewModel ViewModel { get; set; }

    public NovelLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelLibraryPageViewModel>();
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
        if (e.ClickedItem is NovelBookItemViewModel novelItem)
            ViewModel.OpenNovelCommand.Execute(novelItem);
    }

    private void NovelBookButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is NovelBookItemViewModel novelItem)
            ViewModel.OpenNovelCommand.Execute(novelItem);
    }
}
