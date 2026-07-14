using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class TtuSyncSettingsPage : Page
{
    public TtuSyncSettingsPageViewModel ViewModel { get; set; }

    public TtuSyncSettingsPage()
    {
        ViewModel = App.GetService<TtuSyncSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TtuSyncSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }
}
