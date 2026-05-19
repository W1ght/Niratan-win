using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }

    public SettingsPage()
    {
        InitializeComponent();

        ViewModel = App.GetService<SettingsPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        await ViewModel.OnNavigatedFromAsync();
    }

    private void FontSizeDecrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FontSize > 16)
            ViewModel.FontSize--;
    }

    private void FontSizeIncrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FontSize < 60)
            ViewModel.FontSize++;
    }

    private void HPaddingDecrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HorizontalPadding > 0)
            ViewModel.HorizontalPadding--;
    }

    private void HPaddingIncrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HorizontalPadding < 50)
            ViewModel.HorizontalPadding++;
    }

    private void VPaddingDecrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.VerticalPadding > 0)
            ViewModel.VerticalPadding--;
    }

    private void VPaddingIncrease_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.VerticalPadding < 50)
            ViewModel.VerticalPadding++;
    }
}
