using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hoshi.ViewModels.Dialogs;

namespace Hoshi.Views.Dialogs;

public sealed partial class ReaderAppearanceDialog : ContentDialog
{
    public ReaderAppearanceViewModel ViewModel { get; }

    public ReaderAppearanceDialog(ReaderAppearanceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.LoadFromSettings();
    }

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var viewModel = App.GetService<ReaderAppearanceViewModel>();
        var dialog = new ReaderAppearanceDialog(viewModel)
        {
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
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
