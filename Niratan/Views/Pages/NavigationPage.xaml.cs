using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Messages;
using Niratan.Services.UI;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class NavigationPage : Page
{
    public NavigationPageViewModel ViewModel { get; set; }

    public NavigationPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NavigationPageViewModel>();
        DataContext = ViewModel;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetTitleBar(AppTitleBar);
        App.GetService<INavigationService>().Initialize(ContentFrame);
        App.GetService<IDialogService>().Initialize(XamlRoot);

        if (ContentFrame.Content == null)
        {
            var defaultPage = App.GetService<ISettingsService>()
                .Current.DictionaryDisplaySettings.DictionaryTabDefault
                ? typeof(NovelLookupPage)
                : typeof(NovelLibraryPage);
            ViewModel.NavigateCommand.Execute(new NavigateMessage(defaultPage, null));
        }

        _ = App.GetService<IDictionaryCatalogService>().TryAutoUpdateAsync();
    }

    private void NavigationViewControl_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args
    )
    {
        var tag = args.IsSettingsInvoked
            ? "Niratan.Views.Pages.SettingsPage"
            : args.InvokedItemContainer?.Tag?.ToString();
        if (string.IsNullOrEmpty(tag))
            return;

        var pageType = Type.GetType(tag);
        if (pageType != null)
            ViewModel.NavigateCommand.Execute(new NavigateMessage(pageType, null));
    }

    private void NavigationViewControl_BackRequested(
        NavigationView sender,
        NavigationViewBackRequestedEventArgs args
    ) => ViewModel.BackCommand.Execute(null);

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is NovelReaderPage readerPage)
        {
            App.MainWindow?.SetTitleBar(readerPage.ReaderTitleBarElement);
        }
        else
        {
            App.MainWindow?.SetTitleBar(AppTitleBar);
        }

        var selectedMenuItem = NavigationViewControl
            .MenuItems.OfType<NavigationViewItem>()
            .Concat(NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>())
            .FirstOrDefault(item => item.Tag.ToString() == ContentFrame.SourcePageType.FullName);

        if (selectedMenuItem != null)
            NavigationViewControl.SelectedItem = selectedMenuItem;
    }

}
