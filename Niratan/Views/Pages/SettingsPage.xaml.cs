using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (SettingsContentFrame.Content == null)
            NavigateEmbeddedSettingsPage(typeof(ReaderAppearanceSettingsPage));
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        await ViewModel.OnNavigatedFromAsync();
    }

    private void SettingsSecondaryNavigationView_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is not string tag)
            return;

        if (Uri.TryCreate(tag, UriKind.Absolute, out var uri))
        {
            _ = Launcher.LaunchUriAsync(uri);
            return;
        }

        var pageType = Type.GetType(tag);
        if (pageType != null)
            NavigateEmbeddedSettingsPage(pageType);
    }

    private void SettingsSecondaryNavigationView_BackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args)
    {
        if (SettingsContentFrame.CanGoBack)
            SettingsContentFrame.GoBack();
    }

    private void SettingsContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        SettingsSecondaryNavigationView.IsBackEnabled = SettingsContentFrame.CanGoBack;
        SelectNavigationItem(e.SourcePageType);
    }

    private void NavigateEmbeddedSettingsPage(Type pageType)
    {
        if (SettingsContentFrame.CurrentSourcePageType != pageType)
            SettingsContentFrame.Navigate(pageType, SettingsNavigationMode.Embedded);

        SelectNavigationItem(pageType);
    }

    private void SelectNavigationItem(Type pageType)
    {
        var selectedItem = SettingsSecondaryNavigationView
            .MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => Type.GetType(item.Tag?.ToString() ?? "") == pageType);

        if (selectedItem != null)
            SettingsSecondaryNavigationView.SelectedItem = selectedItem;
    }
}
