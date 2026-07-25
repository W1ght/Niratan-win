using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Niratan.ViewModels.Dialogs;
using Windows.System;

namespace Niratan.Views.Dialogs;

public sealed partial class ZLibraryDialog : ContentDialog
{
    public ZLibraryDialogViewModel ViewModel { get; }

    public ZLibraryDialog(ZLibraryDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var rootTheme = xamlRoot.Content is FrameworkElement rootElement
            ? rootElement.ActualTheme
            : ElementTheme.Default;
        var dialog = new ZLibraryDialog(App.GetService<ZLibraryDialogViewModel>())
        {
            XamlRoot = xamlRoot,
            RequestedTheme = rootTheme,
        };
        await dialog.ShowAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await ViewModel.InitializeAsync();
            PasswordBox.Password = ViewModel.Password;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.Password = passwordBox.Password;
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
        Loaded -= OnLoaded;
        ViewModel.Dispose();
    }
}
