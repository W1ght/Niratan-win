using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class ProfilesSettingsPage : Page
{
    public ProfilesSettingsPageViewModel ViewModel { get; }

    public ProfilesSettingsPage()
    {
        ViewModel = App.GetService<ProfilesSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ProfilesSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().GoBack();
    }
}
