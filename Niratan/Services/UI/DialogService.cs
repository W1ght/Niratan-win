using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Niratan.Services.UI;

internal class DialogService : IDialogService
{
    private XamlRoot? _xamlRoot;
    private ElementTheme AppTheme =>
        _xamlRoot?.Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;

    public void Initialize(XamlRoot root) => _xamlRoot = root;

    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(title, message, "Delete", "Cancel");

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        string secondaryButtonText)
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var dialog = new ContentDialog
        {
            Title = title,
            Content = string.IsNullOrWhiteSpace(message) ? null : message,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = _xamlRoot,
            RequestedTheme = AppTheme,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptTextAsync(
        string title,
        string placeholder,
        string primaryButtonText,
        string secondaryButtonText)
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            MinWidth = 360,
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _xamlRoot,
            RequestedTheme = AppTheme,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? textBox.Text
            : null;
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

    public async Task<string?> SaveFilePickerAsync(
        string suggestedFileName,
        string fileTypeDescription,
        string fileExtension)
    {
        if (_xamlRoot == null)
            throw new InvalidOperationException("XamlRoot must be initialized.");

        var picker = new FileSavePicker(_xamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = fileExtension,
            ShowOverwritePrompt = true,
        };
        picker.FileTypeChoices.Add(
            fileTypeDescription,
            new List<string> { fileExtension });
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
