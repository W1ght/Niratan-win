using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Niratan.Views.Pages;

public sealed partial class MangaLibraryPage : Page
{
    public MangaLibraryPageViewModel ViewModel { get; }

    public MangaLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MangaLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private void MangaLibrary_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void MangaLibrary_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync().AsTask();
        var paths = items
            .Select(item => item switch
            {
                StorageFile file => file.Path,
                StorageFolder folder => folder.Path,
                _ => null,
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        if (paths.Count > 0)
            await ViewModel.ImportDroppedCommand.ExecuteAsync(paths);
    }

    private void MangaOnlineBrowseButton_Click(
        object sender,
        RoutedEventArgs e) =>
        App.GetService<INavigationService>().Navigate(typeof(BrowsePage));
}
