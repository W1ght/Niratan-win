using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Models.Dictionary;
using Hoshi.Services.Dictionary;
using Hoshi.Services.UI;
using Serilog;

namespace Hoshi.ViewModels.Pages;

public partial class DictionarySettingsPageViewModel : ObservableObject
{
    public DictionaryType[] AvailableDictionaryTypes { get; } = Enum.GetValues<DictionaryType>();

    public ObservableCollection<InstalledDictionary> InstalledDictionaries { get; } = [];

    [ObservableProperty]
    public partial DictionaryType SelectedDictionaryType { get; set; } = DictionaryType.Term;

    [ObservableProperty]
    public partial bool IsDictionaryListEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDictionaryOperationInProgress { get; set; }

    [ObservableProperty]
    public partial string DictionaryStatusText { get; set; } = "";

    public IAsyncRelayCommand ImportDictionaryCommand { get; }
    public IAsyncRelayCommand<string?> DeleteDictionaryCommand { get; }

    public DictionarySettingsPageViewModel()
    {
        ImportDictionaryCommand = new AsyncRelayCommand(ImportDictionaryAsync);
        DeleteDictionaryCommand = new AsyncRelayCommand<string?>(DeleteDictionaryAsync);
        _ = RefreshDictionariesAsync();
    }

    partial void OnSelectedDictionaryTypeChanged(DictionaryType value)
    {
        _ = RefreshDictionariesAsync();
    }

    public async Task RefreshDictionariesAsync()
    {
        try
        {
            var importService = App.GetService<IDictionaryImportService>();
            var dicts = await importService.GetInstalledDictionariesAsync(SelectedDictionaryType);

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            dispatcher.TryEnqueue(() =>
            {
                InstalledDictionaries.Clear();
                foreach (var dictionary in dicts)
                    InstalledDictionaries.Add(dictionary);

                IsDictionaryListEmpty = InstalledDictionaries.Count == 0;
                DictionaryStatusText = IsDictionaryListEmpty
                    ? $"No {SelectedDictionaryType.ToString().ToLowerInvariant()} dictionaries installed."
                    : $"{InstalledDictionaries.Count} {SelectedDictionaryType.ToString().ToLowerInvariant()} dictionaries installed.";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to refresh dictionary list");
        }
    }

    public async Task SaveDictionaryOrderAsync()
    {
        try
        {
            var names = InstalledDictionaries.Select(d => d.Name).ToList();
            await App.GetService<IDictionaryImportService>()
                .SaveDictionaryOrderAsync(SelectedDictionaryType, names);
            DictionaryStatusText = "Dictionary order saved.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to save dictionary order");
            App.GetService<INotificationService>().ShowError(ex.Message, "Dictionary Order");
        }
    }

    public async Task SetDictionaryEnabledAsync(string dictName, bool enabled)
    {
        try
        {
            await App.GetService<IDictionaryImportService>()
                .SetDictionaryEnabledAsync(SelectedDictionaryType, dictName, enabled);
            await RefreshDictionariesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to set dictionary enabled state");
            App.GetService<INotificationService>().ShowError(ex.Message, "Dictionary");
        }
    }

    private async Task ImportDictionaryAsync()
    {
        try
        {
            var window = App.MainWindow;
            if (window == null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var notification = App.GetService<INotificationService>();
            var importService = App.GetService<IDictionaryImportService>();

            IsDictionaryOperationInProgress = true;
            DictionaryStatusText = $"Importing {file.Name}...";
            Log.Information("[DictionarySettings] Importing dictionary from {Path}", file.Path);

            var result = await importService.ImportAsync(file.Path);
            if (result.Success)
            {
                notification.ShowSuccess(
                    $"Imported '{result.Title}': {result.TermCount} term banks, {result.FreqCount} freq banks",
                    "Dictionary Imported");
                await RefreshDictionariesAsync();
            }
            else
            {
                var errors = string.Join("\n", result.Errors);
                notification.ShowError($"Failed to import dictionary: {errors}", "Import Failed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Dictionary import failed");
            App.GetService<INotificationService>().ShowError(ex.Message, "Import Error");
        }
        finally
        {
            IsDictionaryOperationInProgress = false;
        }
    }

    private async Task DeleteDictionaryAsync(string? dictName)
    {
        if (string.IsNullOrEmpty(dictName)) return;

        try
        {
            IsDictionaryOperationInProgress = true;
            DictionaryStatusText = $"Deleting {dictName}...";
            Log.Information("[DictionarySettings] Deleting dictionary '{Dict}'", dictName);

            var importService = App.GetService<IDictionaryImportService>();
            var deleted = await importService.DeleteAsync(dictName);
            if (deleted)
            {
                App.GetService<INotificationService>()
                    .ShowSuccess($"'{dictName}' removed", "Dictionary Deleted");
                await RefreshDictionariesAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to delete dictionary '{Dict}'", dictName);
            App.GetService<INotificationService>().ShowError(ex.Message, "Delete Error");
        }
        finally
        {
            IsDictionaryOperationInProgress = false;
        }
    }
}
