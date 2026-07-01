using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hoshi.ViewModels.Pages;
using Serilog;

namespace Hoshi.Views.Dialogs;

public sealed partial class ReaderAppearanceDialog : ContentDialog
{
    private readonly SettingsPageViewModel _viewModel;

    public ReaderAppearanceDialog(SettingsPageViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Closed += OnClosed;
    }

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var viewModel = App.GetService<SettingsPageViewModel>();
        var dialog = new ReaderAppearanceDialog(viewModel)
        {
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Closed -= OnClosed;

        try
        {
            await _viewModel.OnNavigatedFromAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ReaderAppearanceDialog] Failed to persist reader appearance settings");
        }
    }
}
