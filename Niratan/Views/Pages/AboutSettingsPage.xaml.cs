using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class AboutSettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }

    public AboutSettingsPage()
    {
        ViewModel = App.GetService<SettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
