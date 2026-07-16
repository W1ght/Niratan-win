using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Messages;
using Niratan.Services.UI;
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
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.SetTitleBar(AppTitleBar);
        if (App.MainWindow is { } mainWindow)
            mainWindow.Activated += MainWindow_Activated;
        App.GetService<INavigationService>().Initialize(ContentFrame);
        App.GetService<IDialogService>().Initialize(XamlRoot);

        await ViewModel.ActivateGlobalProfileAsync();

        if (ContentFrame.Content == null)
            ViewModel.NavigateCommand.Execute(new NavigateMessage(typeof(NovelLibraryPage), null));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is { } mainWindow)
            mainWindow.Activated -= MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            await ViewModel.ActivateGlobalProfileAsync();
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

    private async void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        await ViewModel.ActivateGlobalProfileAsync();
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
