using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
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
    private readonly IDictionaryCatalogService _catalogService;
    private readonly IReaderFontService _readerFontService;
    private bool _isLoadingDisplaySettings;

    public DictionaryCollapseMode[] AvailableCollapseModes { get; } = Enum.GetValues<DictionaryCollapseMode>();

    public ObservableCollection<DictionarySettingsItemViewModel> InstalledDictionaries { get; } = [];
    public ObservableCollection<RecommendedDictionarySelectionItemViewModel> RecommendedDictionaries { get; } = [];
    public ObservableCollection<DictionaryUpdateSelectionItemViewModel> AvailableUpdates { get; } = [];
    public ObservableCollection<CollapsedDictionaryItemViewModel> CollapsedDictionaryItems { get; } = [];
    public ObservableCollection<JapaneseFontOption> AvailableDictionaryFonts { get; } = [];

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

    [ObservableProperty]
    public partial bool DictionaryTabDefault { get; set; }

    [ObservableProperty]
    public partial bool TwoColumnLayout { get; set; }

    [ObservableProperty]
    public partial bool UpdateAutomatically { get; set; } = true;

    [ObservableProperty]
    public partial DictionaryUpdateInterval UpdateInterval { get; set; } = DictionaryUpdateInterval.Weekly;

    public int UpdateIntervalIndex
    {
        get => (int)UpdateInterval;
        set
        {
            if (Enum.IsDefined(typeof(DictionaryUpdateInterval), value))
                UpdateInterval = (DictionaryUpdateInterval)value;
        }
    }

    [ObservableProperty]
    public partial string LastUpdateText { get; set; } = "Never";

    [ObservableProperty]
    public partial string CustomCssDraft { get; set; } = "";

    [ObservableProperty]
    public partial JapaneseFontOption SelectedDictionaryFont { get; set; } = null!;

    public bool CanDeleteSelectedDictionaryFont => SelectedDictionaryFont?.IsImported == true;

    public bool IsTermSelected => SelectedDictionaryType == DictionaryType.Term;
    public bool IsFrequencySelected => SelectedDictionaryType == DictionaryType.Frequency;
    public bool IsPitchSelected => SelectedDictionaryType == DictionaryType.Pitch;
    public bool IsExpandFirstDictionaryVisible => CollapseMode != DictionaryCollapseMode.ExpandAll;
    public bool IsCustomCollapseMode => CollapseMode == DictionaryCollapseMode.Custom;
    public IAsyncRelayCommand ImportDictionaryCommand { get; }
    public IAsyncRelayCommand<string?> DeleteDictionaryCommand { get; }
    public IRelayCommand IncreaseMaxResultsCommand { get; }
    public IRelayCommand DecreaseMaxResultsCommand { get; }
    public IRelayCommand IncreaseScanLengthCommand { get; }
    public IRelayCommand DecreaseScanLengthCommand { get; }
    public IAsyncRelayCommand ImportDictionaryFontCommand { get; }
    public IAsyncRelayCommand DeleteSelectedDictionaryFontCommand { get; }
    public DictionarySettingsPageViewModel()
    {
        _settingsService = App.GetService<ISettingsService>();
        _globalLookupService = App.GetService<IGlobalSelectionLookupService>();
        _catalogService = App.GetService<IDictionaryCatalogService>();
        _readerFontService = App.GetService<IReaderFontService>();
        _globalLookupService.StatusChanged += OnGlobalLookupStatusChanged;
        LoadDisplaySettings();
        ImportDictionaryCommand = new AsyncRelayCommand(ImportDictionaryAsync);
        DeleteDictionaryCommand = new AsyncRelayCommand<string?>(DeleteDictionaryAsync);
        IncreaseMaxResultsCommand = new RelayCommand(() => MaxResults = Clamp(MaxResults + 1, 1, 50));
        DecreaseMaxResultsCommand = new RelayCommand(() => MaxResults = Clamp(MaxResults - 1, 1, 50));
        IncreaseScanLengthCommand = new RelayCommand(() => ScanLength = Clamp(ScanLength + 1, 1, 64));
        DecreaseScanLengthCommand = new RelayCommand(() => ScanLength = Clamp(ScanLength - 1, 1, 64));
        ImportDictionaryFontCommand = new AsyncRelayCommand(ImportDictionaryFontAsync);
        DeleteSelectedDictionaryFontCommand = new AsyncRelayCommand(DeleteSelectedDictionaryFontAsync);
        _ = RefreshDictionariesAsync();
        LoadRecommendations();
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
        OnPropertyChanged(nameof(IsCustomCollapseMode));
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

    partial void OnDictionaryTabDefaultChanged(bool value) =>
        UpdateDisplaySettings(current => current with { DictionaryTabDefault = value });

    partial void OnSelectedDictionaryFontChanged(JapaneseFontOption value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedDictionaryFont));
        if (value is null)
            return;

        UpdateDisplaySettings(current => current with
        {
            FontFamily = value.SubtitleFontFamily,
            FontFileName = value.ImportedFileName,
        });
    }

    partial void OnTwoColumnLayoutChanged(bool value) =>
        UpdateDisplaySettings(current => current with { TwoColumnLayout = value });

    partial void OnUpdateAutomaticallyChanged(bool value) => UpdateDictionaryUpdateSettings();

    partial void OnUpdateIntervalChanged(DictionaryUpdateInterval value)
    {
        OnPropertyChanged(nameof(UpdateIntervalIndex));
        UpdateDictionaryUpdateSettings();
    }

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
            DictionaryTabDefault = settings.DictionaryTabDefault;
            TwoColumnLayout = settings.TwoColumnLayout;
            CustomCssDraft = settings.CustomCSS;
            RefreshAvailableDictionaryFonts();
            SelectedDictionaryFont = AvailableDictionaryFonts.FirstOrDefault(font =>
                                         string.Equals(font.SubtitleFontFamily, settings.FontFamily, StringComparison.Ordinal)
                                         && string.Equals(font.ImportedFileName, settings.FontFileName, StringComparison.Ordinal))
                                     ?? JapaneseFontCatalog.FindBySubtitleFontFamily(settings.FontFamily)
                                     ?? AvailableDictionaryFonts[0];
            var updates = _settingsService.Current.DictionaryUpdateSettings;
            UpdateAutomatically = updates.UpdateAutomatically;
            UpdateInterval = updates.Interval;
            LastUpdateText = updates.LastUpdate?.ToLocalTime().ToString("g")
                ?? ResourceStringHelper.GetString("DictionaryNever", "Never");
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
        if (_isLoadingDisplaySettings)
            return;

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

    private void UpdateDictionaryUpdateSettings()
    {
        if (_isLoadingDisplaySettings)
            return;

        var settings = _settingsService.Current.DictionaryUpdateSettings;
        settings.UpdateAutomatically = UpdateAutomatically;
        settings.Interval = UpdateInterval;
        _settingsService.Set(s => s.DictionaryUpdateSettings, settings);
        _ = _settingsService.SaveAsync();
    }

    private void LoadRecommendations()
    {
        RecommendedDictionaries.Clear();
        foreach (var recommendation in _catalogService.GetRecommendations())
            RecommendedDictionaries.Add(new(
                recommendation,
                recommendation.Name,
                LocalizeDictionaryType(recommendation.Type)));
    }

    private static string LocalizeDictionaryType(DictionaryType type) => type switch
    {
        DictionaryType.Frequency => ResourceStringHelper.GetString("DictionaryTypeFrequencyLabel", "Frequency"),
        DictionaryType.Pitch => ResourceStringHelper.GetString("DictionaryTypePitchLabel", "Pitch"),
        _ => ResourceStringHelper.GetString("DictionaryTypeTermLabel", "Term"),
    };

    public void PrepareRecommendedDictionaries()
    {
        LoadRecommendations();
        foreach (var item in RecommendedDictionaries)
            item.IsSelected = true;
    }

    public async Task DownloadRecommendedDictionariesAsync()
    {
        var selected = RecommendedDictionaries
            .Where(item => item.IsSelected)
            .Select(item => item.Value)
            .ToList();
        if (selected.Count == 0)
            return;

        await RunCatalogOperationAsync(
            "Downloading dictionaries...",
            progress => _catalogService.DownloadRecommendationsAsync(selected, progress),
            "Dictionaries Downloaded");
    }

    public async Task CheckForUpdatesAsync()
    {
        IsDictionaryOperationInProgress = true;
        try
        {
            var progress = new Progress<string>(value => DictionaryStatusText = value);
            var result = await _catalogService.CheckForUpdatesAsync(progress);
            AvailableUpdates.Clear();
            foreach (var update in result.Updates)
            {
                AvailableUpdates.Add(new(
                    update,
                    update.Dictionary.DisplayTitle,
                    $"{update.Dictionary.Revision} → {update.RemoteRevision}"));
            }

            if (result.Failures.Count > 0)
            {
                App.GetService<INotificationService>().ShowWarning(
                    string.Join("\n", result.Failures),
                    "Dictionary Update Check");
            }

            DictionaryStatusText = AvailableUpdates.Count == 0
                ? "All dictionaries are already up to date."
                : $"{AvailableUpdates.Count} dictionary updates available.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Update check failed");
            App.GetService<INotificationService>().ShowError(ex.Message, "Dictionary Update Check");
        }
        finally
        {
            IsDictionaryOperationInProgress = false;
        }
    }

    public async Task UpdateSelectedDictionariesAsync()
    {
        var selected = AvailableUpdates
            .Where(item => item.IsSelected)
            .Select(item => item.Value)
            .ToList();
        if (selected.Count == 0)
            return;

        await RunCatalogOperationAsync(
            "Updating dictionaries...",
            progress => _catalogService.UpdateDictionariesAsync(selected, progress),
            "Dictionaries Updated");
        LastUpdateText = _settingsService.Current.DictionaryUpdateSettings.LastUpdate?
            .ToLocalTime().ToString("g") ?? "Never";
    }

    public async Task PrepareCollapsedDictionariesAsync()
    {
        var termDictionaries = await App.GetService<IDictionaryImportService>()
            .GetInstalledDictionariesAsync(DictionaryType.Term);
        var collapsed = _settingsService.Current.DictionaryDisplaySettings.CollapsedDictionariesOrDefault;
        CollapsedDictionaryItems.Clear();
        foreach (var dictionary in termDictionaries)
        {
            CollapsedDictionaryItems.Add(new(
                dictionary.DisplayTitle,
                collapsed.Contains(dictionary.DisplayTitle)));
        }
    }

    public void SaveCollapsedDictionaries()
    {
        var collapsed = CollapsedDictionaryItems
            .Where(item => item.IsCollapsed)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        UpdateDisplaySettings(current => current with { CollapsedDictionaries = collapsed });
    }

    public void LoadCustomCssDraft() =>
        CustomCssDraft = _settingsService.Current.DictionaryDisplaySettings.CustomCSS;

    public void SaveCustomCss() =>
        UpdateDisplaySettings(current => current with { CustomCSS = CustomCssDraft });

    private void RefreshAvailableDictionaryFonts()
    {
        AvailableDictionaryFonts.Clear();
        foreach (var font in _readerFontService.GetAvailableFonts())
            AvailableDictionaryFonts.Add(font);
    }

    private async Task ImportDictionaryFontAsync()
    {
        try
        {
            var filePath = await App.GetService<IDialogService>().OpenFilePickerAsync(
                ".ttf", ".otf", ".woff", ".woff2");
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var imported = await _readerFontService.ImportAsync(filePath);
            RefreshAvailableDictionaryFonts();
            SelectedDictionaryFont = AvailableDictionaryFonts.First(font =>
                string.Equals(font.ImportedFileName, imported.ImportedFileName, StringComparison.Ordinal));
            App.GetService<INotificationService>().ShowSuccess(
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString("DictionaryFontImportedMessage", "Imported '{0}'."),
                    imported.Name),
                ResourceStringHelper.GetString("DictionaryFontNotificationTitle", "Dictionary Font"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to import dictionary font");
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("DictionaryFontNotificationTitle", "Dictionary Font"));
        }
    }

    private async Task DeleteSelectedDictionaryFontAsync()
    {
        var selected = SelectedDictionaryFont;
        if (selected?.ImportedFileName is not { } fileName)
            return;

        try
        {
            var confirmed = await App.GetService<IDialogService>().ConfirmAsync(
                ResourceStringHelper.GetString("DictionaryFontDeleteDialogTitle", "Delete Dictionary Font"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString(
                        "DictionaryFontDeleteDialogMessage",
                        "Delete '{0}'? The font file will be removed from Niratan."),
                    selected.Name));
            if (!confirmed)
                return;

            await _readerFontService.DeleteAsync(fileName);
            RefreshAvailableDictionaryFonts();
            SelectedDictionaryFont = AvailableDictionaryFonts[0];
            App.GetService<INotificationService>().ShowSuccess(
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString("DictionaryFontDeletedMessage", "Deleted '{0}'."),
                    selected.Name),
                ResourceStringHelper.GetString("DictionaryFontNotificationTitle", "Dictionary Font"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Failed to delete dictionary font {Font}", fileName);
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("DictionaryFontNotificationTitle", "Dictionary Font"));
        }
    }

    private async Task RunCatalogOperationAsync(
        string initialStatus,
        Func<IProgress<string>, Task<DictionaryBatchOperationResult>> operation,
        string successTitle)
    {
        IsDictionaryOperationInProgress = true;
        DictionaryStatusText = initialStatus;
        try
        {
            var progress = new Progress<string>(value => DictionaryStatusText = value);
            var result = await operation(progress);
            if (result.Succeeded.Count > 0)
            {
                App.GetService<INotificationService>().ShowSuccess(
                    $"Completed {result.Succeeded.Count} dictionaries.",
                    successTitle);
                await RefreshDictionariesAsync();
            }
            if (result.Failures.Count > 0)
            {
                App.GetService<INotificationService>().ShowWarning(
                    string.Join("\n", result.Failures),
                    "Dictionary Operation");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DictionarySettings] Catalog operation failed");
            App.GetService<INotificationService>().ShowError(ex.Message, "Dictionary Operation");
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

public partial class RecommendedDictionarySelectionItemViewModel : ObservableObject
{
    public DictionaryRecommendation Value { get; }
    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public RecommendedDictionarySelectionItemViewModel(
        DictionaryRecommendation value,
        string name,
        string detail)
    {
        Value = value;
        Name = name;
        Detail = detail;
    }
}

public partial class DictionaryUpdateSelectionItemViewModel : ObservableObject
{
    public DictionaryUpdateCandidate Value { get; }
    public string Name { get; }
    public string Detail { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public DictionaryUpdateSelectionItemViewModel(
        DictionaryUpdateCandidate value,
        string name,
        string detail)
    {
        Value = value;
        Name = name;
        Detail = detail;
    }
}

public partial class CollapsedDictionaryItemViewModel : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    public CollapsedDictionaryItemViewModel(string name, bool isCollapsed)
    {
        Name = name;
        IsCollapsed = isCollapsed;
    }
}
