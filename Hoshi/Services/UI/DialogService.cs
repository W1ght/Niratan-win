using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Hoshi.Services.UI;

internal class DialogService : IDialogService
{
    private XamlRoot? _xamlRoot;
    private ElementTheme AppTheme =>
        _xamlRoot?.Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;

    public void Initialize(XamlRoot root) => _xamlRoot = root;

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Delete",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = _xamlRoot,
            RequestedTheme = AppTheme,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> OpenFilePickerAsync(params string[] fileTypeFilters)
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var picker = new FileOpenPicker(_xamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        var filters = fileTypeFilters.Length == 0 ? ["*"] : fileTypeFilters;
        foreach (var filter in filters.Where(filter => !string.IsNullOrWhiteSpace(filter)).Distinct())
            picker.FileTypeFilter.Add(filter);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> OpenFolderPickerAsync()
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var picker = new FolderPicker(_xamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
