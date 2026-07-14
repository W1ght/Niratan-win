using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class VideoSettingsPage : Page
{
    public VideoSettingsPageViewModel ViewModel { get; set; }

    public VideoSettingsPage()
    {
        ViewModel = App.GetService<VideoSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        VideoSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }

    private void KeyboardShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(KeyboardShortcutsSettingsPage), SettingsNavigationMode.Embedded);
    }
}
