using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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

    private void NovelLibrary_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
    }

    private async void NovelLibrary_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync().AsTask();
        var filePaths = items
            .OfType<StorageFile>()
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (filePaths.Count > 0)
            await ViewModel.ImportDroppedNovelsCommand.ExecuteAsync(filePaths);
    }

    private async void NovelBookGridView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.Move)
            await ViewModel.SaveCurrentManualOrderCommand.ExecuteAsync(null);
    }
}
