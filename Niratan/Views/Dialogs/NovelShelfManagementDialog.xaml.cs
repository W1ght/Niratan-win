using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Novel;
using Niratan.ViewModels.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Niratan.Views.Dialogs;

public sealed partial class NovelShelfManagementDialog : ContentDialog
{
    public NovelShelfManagementViewModel ViewModel { get; }

    public NovelShelfManagementDialog(NovelShelfManagementViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var dialog = new NovelShelfManagementDialog(
            App.GetService<NovelShelfManagementViewModel>())
        {
            XamlRoot = xamlRoot,
        };
        await dialog.ViewModel.InitializeAsync();
        await dialog.ShowAsync();
    }

    private async void CreateShelfButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ShelfNameBox.Text))
            await ViewModel.CreateShelfCommand.ExecuteAsync(ShelfNameBox.Text);
    }

    private void ShelfList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var shelf = NovelShelfList.SelectedItem as NovelShelf;
        ShelfRenameBox.Text = shelf?.Name ?? string.Empty;
        NovelShelfRenameButton.IsEnabled = shelf is not null;
        NovelShelfDeleteButton.IsEnabled = shelf is not null;
    }

    private async void RenameShelfButton_Click(object sender, RoutedEventArgs e)
    {
        if (NovelShelfList.SelectedItem is not NovelShelf shelf
            || string.IsNullOrWhiteSpace(ShelfRenameBox.Text))
        {
            return;
        }

        await ViewModel.RenameShelfCommand.ExecuteAsync(
            new NovelShelfRenameRequest(shelf.Name, ShelfRenameBox.Text));
    }

    private void DeleteConfirmationFlyout_Opening(
        object sender,
        object e)
    {
        if (NovelShelfList.SelectedItem is not NovelShelf shelf)
            return;

        NovelShelfDeleteConfirmationText.Text = ResourceStringHelper.FormatString(
            "NovelShelfDeleteConfirmation/Content",
            "Delete '{0}'? Books will become unshelved.",
            shelf.Name);
    }

    private async void ConfirmDeleteShelfButton_Click(object sender, RoutedEventArgs e)
    {
        if (NovelShelfList.SelectedItem is NovelShelf shelf)
            await ViewModel.DeleteShelfCommand.ExecuteAsync(shelf.Name);
        NovelShelfDeleteConfirmationFlyout.Hide();
    }

    private void CancelDeleteShelfButton_Click(object sender, RoutedEventArgs e)
    {
        NovelShelfDeleteConfirmationFlyout.Hide();
    }

    private async void ShelfList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        var names = sender.Items
            .OfType<NovelShelf>()
            .Select(shelf => shelf.Name)
            .ToList();
        if (names.Count > 0)
            await ViewModel.ReorderShelvesCommand.ExecuteAsync(names);
    }
}
