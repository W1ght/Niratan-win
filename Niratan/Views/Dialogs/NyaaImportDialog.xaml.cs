using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Niratan.ViewModels.Dialogs;
using Windows.System;

namespace Niratan.Views.Dialogs;

public sealed partial class NyaaImportDialog : ContentDialog
{
    public NyaaImportDialogViewModel ViewModel { get; }

    public NyaaImportDialog(NyaaImportDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Closed += OnClosed;
    }

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var rootTheme = xamlRoot.Content is FrameworkElement rootElement
            ? rootElement.ActualTheme
            : ElementTheme.Default;
        var dialog = new NyaaImportDialog(App.GetService<NyaaImportDialogViewModel>())
        {
            XamlRoot = xamlRoot,
            RequestedTheme = rootTheme,
        };
        await dialog.ShowAsync();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.CanSearch)
            return;
        e.Handled = true;
        ViewModel.SearchCommand.Execute(null);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Closed -= OnClosed;
        ViewModel.Dispose();
    }
}
