using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class BackupSettingsPage : Page
{
    public BackupSettingsPageViewModel ViewModel { get; }

    public BackupSettingsPage()
    {
        ViewModel = App.GetService<BackupSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += (_, _) => App.GetService<IDialogService>().Initialize(XamlRoot);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BackupSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }
}
