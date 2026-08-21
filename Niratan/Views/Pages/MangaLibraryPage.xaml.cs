using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.ViewModels.Pages;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Niratan.Views.Pages;

public sealed partial class MangaLibraryPage : Page
{
    public MangaLibraryPageViewModel ViewModel { get; }
    private MangaLibraryPageViewModel? _browsePageViewModel;

    public MangaLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<MangaLibraryPageViewModel>();
        DataContext = ViewModel;
        SetSelectedNavigationItem(ViewModel.SelectedSection);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        SetSelectedNavigationItem(ViewModel.SelectedSection);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DetachBrowsePageViewModel();
        if (MangaDiscoverPageHostFrame.Content is BrowsePage browsePage)
            browsePage.ViewModel.OnNavigatedFrom();
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async void MangaLibraryNavigationView_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string viewName)
            return;

        var section = viewName switch
        {
            "Discover" => MangaHomeSection.Discover,
            "Browse" => MangaHomeSection.Browse,
            "Extensions" => MangaHomeSection.Sources,
            "Settings" => MangaHomeSection.Settings,
            _ => MangaHomeSection.Library,
        };
        ViewModel.SelectedSection = section;
        if (section == MangaHomeSection.Library)
        {
            ViewModel.SelectLibraryCommand.Execute(null);
        }
        else
        {
            await ShowMangaDiscoverPageAsync(section);
        }

        SetSelectedNavigationItem(ViewModel.SelectedSection);
    }

    private async Task ShowMangaDiscoverPageAsync(
        MangaHomeSection section = MangaHomeSection.Discover)
    {
        if (MangaDiscoverPageHostFrame.Content is BrowsePage browsePage)
        {
            AttachBrowsePageViewModel(browsePage);
            await browsePage.ViewModel.SelectBrowseSectionAsync(section);
            return;
        }

        MangaDiscoverPageHostFrame.Navigate(typeof(BrowsePage), section);
    }

    private void MangaDiscoverPageHostFrame_Navigated(
        object sender,
        NavigationEventArgs e)
    {
        if (e.Content is BrowsePage browsePage)
            AttachBrowsePageViewModel(browsePage);
    }

    private void AttachBrowsePageViewModel(BrowsePage browsePage)
    {
        if (ReferenceEquals(_browsePageViewModel, browsePage.ViewModel))
            return;

        DetachBrowsePageViewModel();
        _browsePageViewModel = browsePage.ViewModel;
        _browsePageViewModel.PropertyChanged += BrowsePageViewModel_PropertyChanged;
    }

    private void DetachBrowsePageViewModel()
    {
        if (_browsePageViewModel is null)
            return;

        _browsePageViewModel.PropertyChanged -= BrowsePageViewModel_PropertyChanged;
        _browsePageViewModel = null;
    }

    private void BrowsePageViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MangaLibraryPageViewModel.SelectedSection))
            SynchronizeBrowsePageSelection();
    }

    private void SynchronizeBrowsePageSelection()
    {
        if (_browsePageViewModel is null)
            return;

        ViewModel.SelectedSection = _browsePageViewModel.SelectedSection;
        SetSelectedNavigationItem(_browsePageViewModel.SelectedSection);
    }

    private void SetSelectedNavigationItem(MangaHomeSection section) =>
        MangaLibraryNavigationView.SelectedItem = section switch
        {
            MangaHomeSection.Discover => MangaLibraryDiscoverNavItem,
            MangaHomeSection.Browse => MangaLibrarySourcesNavItem,
            MangaHomeSection.Sources => MangaLibraryExtensionsNavItem,
            MangaHomeSection.Settings => MangaLibrarySettingsNavItem,
            _ => MangaLibraryHomeNavItem,
        };

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

    private async void MangaOnlineBrowseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ViewModel.SelectedSection = MangaHomeSection.Discover;
        await ShowMangaDiscoverPageAsync(MangaHomeSection.Discover);
        SetSelectedNavigationItem(ViewModel.SelectedSection);
    }
}
