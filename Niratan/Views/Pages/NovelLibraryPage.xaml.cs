using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Niratan.Helpers;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Niratan.Views.Dialogs;

namespace Niratan.Views.Pages;

public sealed partial class NovelLibraryPage : Page
{
    public NovelLibraryPageViewModel ViewModel { get; set; }

    public NovelLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void NovelLibrary_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
    }

    private async void NovelLibrary_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync().AsTask();
        var filePaths = items
            .OfType<StorageFile>()
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (filePaths.Count > 0)
            await ViewModel.ImportDroppedNovelsCommand.ExecuteAsync(filePaths);
    }

    private async void ShelfManagementButton_Click(object sender, RoutedEventArgs e)
    {
        await NovelShelfManagementDialog.ShowAsync(XamlRoot);
        await ViewModel.InitializeAsync();
    }

    private async void MoveNovelToShelfMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not NovelBookItemViewModel novelItem)
            return;

        var targets = ViewModel.ShelfSections
            .Where(section => !section.IsDerived)
            .Select(section => new NovelShelfTarget(section.DisplayName, section.DisplayName))
            .Prepend(new NovelShelfTarget(
                ResourceStringHelper.GetString(
                    "NovelShelfUnshelvedLabel/Text",
                    "Unshelved"),
                null))
            .ToList();
        var targetPicker = new ComboBox
        {
            ItemsSource = targets,
            DisplayMemberPath = nameof(NovelShelfTarget.DisplayName),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStringHelper.GetString(
                "NovelBookMoveDialog/Title",
                "Move to shelf"),
            Content = targetPicker,
            PrimaryButtonText = ResourceStringHelper.GetString(
                "NovelBookMoveDialog/PrimaryButtonText",
                "Move"),
            CloseButtonText = ResourceStringHelper.GetString(
                "NovelBookMoveDialog/CloseButtonText",
                "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && targetPicker.SelectedItem is NovelShelfTarget target)
        {
            await ViewModel.MoveBookCommand.ExecuteAsync(
                new NovelBookShelfMoveRequest(novelItem.Book.Id, target.ShelfName));
        }
    }

    private void NovelBookContextFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout)
            return;

        var automaticItem = flyout.Items
            .OfType<MenuFlyoutItem>()
            .FirstOrDefault(item =>
                AutomationProperties.GetAutomationId(item) == "NovelBookSyncMenuItem");
        var manualSubmenu = flyout.Items
            .OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(item =>
                AutomationProperties.GetAutomationId(item) == "NovelBookSyncSubmenu");

        if (automaticItem != null)
        {
            automaticItem.Visibility = ViewModel.ShowAutomaticBookSyncAction
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (manualSubmenu != null)
        {
            manualSubmenu.Visibility = ViewModel.ShowManualBookSyncAction
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private async void SyncNovelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
            await ViewModel.SyncNovelCommand.ExecuteAsync(novelItem);
    }

    private async void ImportNovelFromTtuMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
            await ViewModel.ImportNovelFromTtuCommand.ExecuteAsync(novelItem);
    }

    private async void ExportNovelToTtuMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
            await ViewModel.ExportNovelToTtuCommand.ExecuteAsync(novelItem);
    }

    private async void DeleteNovelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not NovelBookItemViewModel novelItem)
            return;

        await ViewModel.DeleteNovelCommand.ExecuteAsync(novelItem);
    }

    private async void MarkReadNovelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not NovelBookItemViewModel novelItem)
            return;

        await ViewModel.MarkReadNovelCommand.ExecuteAsync(novelItem);
    }

    private async void ExportNovelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not NovelBookItemViewModel novelItem)
            return;

        await ViewModel.ExportNovelCommand.ExecuteAsync(novelItem);
    }

    private async void DeleteRemoteNovelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not RemoteNovelBookItemViewModel remoteItem)
            return;

        await ViewModel.DeleteRemoteBookCommand.ExecuteAsync(remoteItem);
    }

    private sealed record NovelShelfTarget(string DisplayName, string? ShelfName);
}
