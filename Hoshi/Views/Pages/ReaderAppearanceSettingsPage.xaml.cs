using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class ReaderAppearanceSettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; set; }

    public ReaderAppearanceSettingsPage()
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ReaderAppearanceBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().GoBack();
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
