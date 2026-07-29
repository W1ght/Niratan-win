using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class BrowsePage : Page
{
    public MangaLibraryPageViewModel ViewModel { get; }

    public BrowsePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MangaLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeBrowseAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async void BrowseSourceRow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
        {
                DataContext: MangaBrowseSourceItemViewModel item
        })
        {
            await item.EnsureIconAsync();
        }
    }

    private void MangaBrowseBookshelf_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        var preloadStart = Math.Max(0, ViewModel.BrowseBooks.Count - 6);
        if (args.Index < preloadStart)
            return;

        var command = ViewModel.LoadNextBrowsePageCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
