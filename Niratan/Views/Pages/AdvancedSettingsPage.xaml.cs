using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;

namespace Niratan.Views.Pages;

public sealed partial class AdvancedSettingsPage : Page
{
    private bool _isEmbedded;

    public AdvancedSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isEmbedded = e.Parameter is SettingsNavigationMode.Embedded;
        AdvancedSettingsBackButton.Visibility = _isEmbedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }

    private void AudioSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(AudioSettingsPage));
    }

    private void StatisticsSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(StatisticsSettingsPage));
    }

    private void SasayakiSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(SasayakiSettingsPage));
    }

    private void VideoSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(VideoSettingsPage));
    }

    private void KeyboardShortcutsSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(KeyboardShortcutsSettingsPage));
    }

    private void GameControllerSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(GameControllerSettingsPage));
    }

    private void SyncSettings_Click(object sender, RoutedEventArgs e)
    {
        NavigateSettingsSubpage(typeof(TtuSyncSettingsPage));
    }

    private void NavigateSettingsSubpage(Type pageType)
    {
        if (_isEmbedded)
        {
            Frame.Navigate(pageType, SettingsNavigationMode.Embedded);
            return;
        }

        App.GetService<INavigationService>().Navigate(pageType);
    }
}
