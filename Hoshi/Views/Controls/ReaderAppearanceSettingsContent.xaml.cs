using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Controls;

public sealed partial class ReaderAppearanceSettingsContent : UserControl
{
    public SettingsPageViewModel ViewModel { get; }

    public ReaderAppearanceSettingsContent()
    {
        ViewModel = App.GetService<SettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
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
