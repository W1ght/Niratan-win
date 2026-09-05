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

    private async void SaveTmdbTokenButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveProviderTokenAsync("tmdb", TmdbTokenBox.Password);
        TmdbTokenBox.Password = string.Empty;
    }

    private async void ClearTmdbTokenButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearProviderTokenAsync("tmdb");
        TmdbTokenBox.Password = string.Empty;
    }

    private async void SaveJimakuTokenButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveProviderTokenAsync("jimaku", JimakuTokenBox.Password);
        JimakuTokenBox.Password = string.Empty;
    }

    private async void ClearJimakuTokenButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearProviderTokenAsync("jimaku");
        JimakuTokenBox.Password = string.Empty;
    }

    private async void SaveAniDbCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAniDbCredentialsAsync(AniDbUsernameBox.Text, AniDbPasswordBox.Password);
        AniDbPasswordBox.Password = string.Empty;
    }

    private async void ClearAniDbCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ClearAniDbCredentialsAsync();
        AniDbUsernameBox.Text = string.Empty;
        AniDbPasswordBox.Password = string.Empty;
    }

    private async void TestAniDbLoginButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.TestAniDbLoginAsync();

    private async void SyncAniDbMyListButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.SyncAniDbMyListAsync();
}
