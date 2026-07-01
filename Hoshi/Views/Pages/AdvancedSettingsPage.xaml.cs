using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hoshi.Services.UI;

namespace Hoshi.Views.Pages;

public sealed partial class AdvancedSettingsPage : Page
{
    public AdvancedSettingsPage()
    {
        InitializeComponent();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }

    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(AudioSettingsPage));
    }

    private void StatisticsSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(StatisticsSettingsPage));
    }

    private void SasayakiSettings_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().Navigate(typeof(SasayakiSettingsPage));
    }
}
