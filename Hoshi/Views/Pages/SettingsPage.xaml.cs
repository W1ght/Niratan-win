using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsPageViewModel>();
        InitializeComponent();

        DataContext = ViewModel;
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        await ViewModel.OnNavigatedFromAsync();
    }

    private void ReaderAppearanceSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(ReaderAppearanceSettingsPage));
    }

    private void DictionarySettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(DictionarySettingsPage));
    }

    private void AnkiSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(AnkiSettingsPage));
    }

    private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(AdvancedSettingsPage));
    }
}
