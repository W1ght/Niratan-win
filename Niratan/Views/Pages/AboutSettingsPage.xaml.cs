using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class AboutSettingsPage : Page
{
    public AboutSettingsPageViewModel ViewModel { get; set; }

    public AboutSettingsPage()
    {
        ViewModel = App.GetService<AboutSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
