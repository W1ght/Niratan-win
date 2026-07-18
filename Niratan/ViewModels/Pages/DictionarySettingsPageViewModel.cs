using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.UI;
using Serilog;

namespace Niratan.ViewModels.Pages;

public partial class DictionarySettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IGlobalSelectionLookupService _globalLookupService;
    private bool _isLoadingDisplaySettings;

    public DictionaryCollapseMode[] AvailableCollapseModes { get; } = Enum.GetValues<DictionaryCollapseMode>();

    public ObservableCollection<DictionarySettingsItemViewModel> InstalledDictionaries { get; } = [];

    [ObservableProperty]
    public partial DictionaryType SelectedDictionaryType { get; set; } = DictionaryType.Term;

    [ObservableProperty]
    public partial bool IsDictionaryListEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDictionaryOperationInProgress { get; set; }

    [ObservableProperty]
    public partial string DictionaryStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool ScanNonJapaneseText { get; set; } = true;

    [ObservableProperty]
    public partial bool GlobalLookupEnabled { get; set; }

    [ObservableProperty]
    public partial string GlobalLookupStatusText { get; set; } = "";

    [ObservableProperty]
    public partial int MaxResults { get; set; } = 16;

    [ObservableProperty]
    public partial int ScanLength { get; set; } = 16;

    [ObservableProperty]
    public partial DictionaryCollapseMode CollapseMode { get; set; } = DictionaryCollapseMode.ExpandAll;

    [ObservableProperty]
    public partial bool ExpandFirstDictionary { get; set; }

    [ObservableProperty]
    public partial bool CompactGlossaries { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowExpressionTags { get; set; }

    [ObservableProperty]
    public partial bool HarmonicFrequency { get; set; }

    [ObservableProperty]
    public partial bool DeduplicatePitchAccents { get; set; }

    [ObservableProperty]
    public partial bool CompactPitchAccents { get; set; } = true;

    public bool IsTermSelected => SelectedDictionaryType == DictionaryType.Term;
    public bool IsFrequencySelected => SelectedDictionaryType == DictionaryType.Frequency;
    public bool IsPitchSelected => SelectedDictionaryType == DictionaryType.Pitch;
    public bool IsExpandFirstDictionaryVisible => CollapseMode != DictionaryCollapseMode.ExpandAll;
    public IAsyncRelayCommand ImportDictionaryCommand { get; }
    public IAsyncRelayCommand<string?> DeleteDictionaryCommand { get; }
    public IRelayCommand IncreaseMaxResultsCommand { get; }
    public IRelayCommand DecreaseMaxResultsCommand { get; }
    public IRelayCommand IncreaseScanLengthCommand { get; }
    public IRelayCommand DecreaseScanLengthCommand { get; }
    public DictionarySettingsPageViewModel()
    {
        _settingsService = App.GetService<ISettingsService>();
        _globalLookupService = App.GetService<IGlobalSelectionLookupService>();
        _globalLookupService.StatusChanged += OnGlobalLookupStatusChanged;
        LoadDisplaySettings();
        ImportDictionaryCommand = new AsyncRelayCommand(ImportDictionaryAsync);
        DeleteDictionaryCommand = new AsyncRelayCommand<string?>(DeleteDictionaryAsync);
        IncreaseMaxResultsCommand = new RelayCommand(() => MaxResults = Clamp(MaxResults + 1, 1, 50));
        DecreaseMaxResultsCommand = new RelayCommand(() => MaxResults = Clamp(MaxResults - 1, 1, 50));
        IncreaseScanLengthCommand = new RelayCommand(() => ScanLength = Clamp(ScanLength + 1, 1, 64));
        DecreaseScanLengthCommand = new RelayCommand(() => ScanLength = Clamp(ScanLength - 1, 1, 64));
        _ = RefreshDictionariesAsync();
    }

    partial void OnSelectedDictionaryTypeChanged(DictionaryType value)
    {
        OnPropertyChanged(nameof(IsTermSelected));
        OnPropertyChanged(nameof(IsFrequencySelected));
        OnPropertyChanged(nameof(IsPitchSelected));
        _ = RefreshDictionariesAsync();
    }

    partial void OnScanNonJapaneseTextChanged(bool value) =>
        UpdateDisplaySettings(current => current with { ScanNonJapaneseText = value });

    partial void OnGlobalLookupEnabledChanged(bool value)
    {
        if (_isLoadingDisplaySettings)
            return;

        _ = ApplyGlobalLookupEnabledAsync(value);
    }

    private async Task ApplyGlobalLookupEnabledAsync(bool value)
    {
        _settingsService.Current.GlobalLookup.Enabled = value;
        _settingsService.Set(s => s.GlobalLookup, _settingsService.Current.GlobalLookup);
        await _settingsService.SaveAsync();
        await _globalLookupService.InitializeAsync();
        GlobalLookupStatusText = _globalLookupService.StatusText;
    }

    partial void OnMaxResultsChanged(int value) =>
        UpdateDisplaySettings(current => current with { MaxResults = Clamp(value, 1, 50) });

    partial void OnScanLengthChanged(int value) =>
        UpdateDisplaySettings(current => current with { ScanLength = Clamp(value, 1, 64) });

    partial void OnCollapseModeChanged(DictionaryCollapseMode value)
    {
        OnPropertyChanged(nameof(IsExpandFirstDictionaryVisible));
        UpdateDisplaySettings(current => current with { CollapseMode = value });
    }

    partial void OnExpandFirstDictionaryChanged(bool value) =>
        UpdateDisplaySettings(current => current with { ExpandFirstDictionary = value });

    partial void OnCompactGlossariesChanged(bool value) =>
        UpdateDisplaySettings(current => current with { CompactGlossaries = value });

    partial void OnShowExpressionTagsChanged(bool value) =>
        UpdateDisplaySettings(current => current with { ShowExpressionTags = value });

    partial void OnHarmonicFrequencyChanged(bool value) =>
        UpdateDisplaySettings(current => current with { HarmonicFrequency = value });

    partial void OnDeduplicatePitchAccentsChanged(bool value) =>
        UpdateDisplaySettings(current => current with { DeduplicatePitchAccents = value });

    partial void OnCompactPitchAccentsChanged(bool value) =>
        UpdateDisplaySettings(current => current with { CompactPitchAccents = value });

    private void LoadDisplaySettings()
    {
        _isLoadingDisplaySettings = true;
        try
        {
            var settings = _settingsService.Current.DictionaryDisplaySettings;
            GlobalLookupEnabled = _settingsService.Current.GlobalLookup.Enabled;
            GlobalLookupStatusText = _globalLookupService.StatusText;
            ScanNonJapaneseText = settings.ScanNonJapaneseText;
            MaxResults = Clamp(settings.MaxResults, 1, 50);
            ScanLength = Clamp(settings.ScanLength, 1, 64);
            CollapseMode = settings.CollapseMode;
            ExpandFirstDictionary = settings.ExpandFirstDictionary;
            CompactGlossaries = settings.CompactGlossaries;
            ShowExpressionTags = settings.ShowExpressionTags;
            HarmonicFrequency = settings.HarmonicFrequency;
            DeduplicatePitchAccents = settings.DeduplicatePitchAccents;
            CompactPitchAccents = settings.CompactPitchAccents;
        }
        finally
        {
            _isLoadingDisplaySettings = false;
        }
    }

    private void OnGlobalLookupStatusChanged(object? sender, EventArgs e)
    {
        GlobalLookupStatusText = _globalLookupService.StatusText;
    }

    private void UpdateDisplaySettings(Func<DictionaryDisplaySettings, DictionaryDisplaySettings> update)
    {
        var current = _settingsService.Current.DictionaryDisplaySettings;
        _settingsService.Set(s => s.DictionaryDisplaySettings, update(current));
        _ = _settingsService.SaveAsync();
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

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
                    InstalledDictionaries.Add(new DictionarySettingsItemViewModel(dictionary));

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
        var dictionary = InstalledDictionaries.FirstOrDefault(item => item.Name == dictName);
        if (dictionary == null)
            return;

        var previousValue = dictionary.IsEnabled;
        if (!dictionary.TrySetEnabled(enabled))
            return;

        try
        {
            await App.GetService<IDictionaryImportService>()
                .SetDictionaryEnabledAsync(SelectedDictionaryType, dictName, enabled);
        }
        catch (Exception ex)
        {
            dictionary.IsEnabled = previousValue;
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

            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;

            var notification = App.GetService<INotificationService>();
            var importService = App.GetService<IDictionaryImportService>();
            var successfulImports = new List<DictionaryImportResult>();
            var failures = new List<string>();

            IsDictionaryOperationInProgress = true;
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                DictionaryStatusText = $"Importing {index + 1} of {files.Count}: {file.Name}...";
                Log.Information(
                    "[DictionarySettings] Importing dictionary {Index}/{Count} from {Path} (Name={FileName})",
                    index + 1, files.Count, file.Path, file.Name);

                try
                {
                    var result = await importService.ImportAsync(file.Path);
                    Log.Information(
                        "[DictionarySettings] ImportAsync returned Success={Success}, Title={Title}, Errors={Errors}",
                        result.Success, result.Title, string.Join("; ", result.Errors));

                    if (result.Success)
                    {
                        successfulImports.Add(result);
                    }
                    else
                    {
                        var error = string.Join("; ", result.Errors);
                        failures.Add($"{file.Name}: {(string.IsNullOrWhiteSpace(error) ? "Unknown import error." : error)}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex,
                        "[DictionarySettings] Failed to import dictionary {Index}/{Count} from {Path}",
                        index + 1, files.Count, file.Path);
                    failures.Add($"{file.Name}: [{ex.GetType().Name}] {ex.Message}");
                }
            }

            if (successfulImports.Count > 0)
            {
                await RefreshDictionariesAsync();
            }

            if (failures.Count == 0)
            {
                var message = successfulImports.Count == 1
                    ? $"Imported '{successfulImports[0].Title}': {successfulImports[0].TermCount} term banks, {successfulImports[0].FreqCount} freq banks"
                    : $"Imported {successfulImports.Count} dictionaries.";
                notification.ShowSuccess(message,
                    successfulImports.Count == 1 ? "Dictionary Imported" : "Dictionaries Imported");
            }
            else
            {
                var failureDetails = string.Join("\n", failures);
                if (successfulImports.Count > 0)
                {
                    notification.ShowWarning(
                        $"Imported {successfulImports.Count} of {files.Count} dictionaries. Failed:\n{failureDetails}",
                        "Dictionary Import Partially Completed");
                }
                else
                {
                    DictionaryStatusText = $"Failed to import {failures.Count} dictionaries.";
                    notification.ShowError($"Failed to import dictionaries:\n{failureDetails}", "Import Failed");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Import failed: Type={ExceptionType}, Message={Message}",
                ex.GetType().FullName, ex.Message);
            if (ex.InnerException != null)
                Log.Error(ex.InnerException, "[DictionarySettings] Inner exception: {InnerType}: {InnerMessage}",
                    ex.InnerException.GetType().FullName, ex.InnerException.Message);
            App.GetService<INotificationService>().ShowError($"[{ex.GetType().Name}] {ex.Message}", "Import Error");
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
            var deleted = await importService.DeleteAsync(SelectedDictionaryType, dictName);
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

public partial class DictionarySettingsItemViewModel : ObservableObject
{
    public string Name { get; }
    public DictionaryType Type { get; }
    public int Order { get; }
    public string Revision { get; }
    public string DisplayTitle { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public DictionarySettingsItemViewModel(InstalledDictionary dictionary)
    {
        Name = dictionary.Name;
        Type = dictionary.Type;
        IsEnabled = dictionary.IsEnabled;
        Order = dictionary.Order;
        Revision = dictionary.Revision;
        DisplayTitle = dictionary.DisplayTitle;
    }

    public bool TrySetEnabled(bool value)
    {
        if (IsEnabled == value)
            return false;

        IsEnabled = value;
        return true;
    }
}
