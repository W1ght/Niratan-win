using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Helpers;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Profiles;
using Hoshi.Services.Settings;
using Hoshi.Services.UI;
using Serilog;

namespace Hoshi.ViewModels.Pages;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IReaderSettingsService _readerSettingsService;
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly IMessenger _messenger;

    private bool _isInitializing = true;

    public string HoshiVersion { get; } = AppInfoHelper.Version;

    // --- App settings ---
    public ThemeMode[] AvailableThemeModes { get; } = Enum.GetValues<ThemeMode>();

    [ObservableProperty]
    public partial ThemeMode SelectedThemeMode { get; set; }

    // --- Reader settings: Theme ---
    [ObservableProperty]
    public partial bool SepiaMode { get; set; }

    // --- Reader settings: Text ---
    [ObservableProperty]
    public partial bool VerticalWriting { get; set; }

    public IReadOnlyList<JapaneseFontOption> AvailableReaderFonts { get; } = JapaneseFontCatalog.Fonts;

    [ObservableProperty]
    public partial JapaneseFontOption SelectedReaderFont { get; set; } = null!;

    [ObservableProperty]
    public partial int FontSize { get; set; }

    [ObservableProperty]
    public partial bool HideFurigana { get; set; }

    // --- Reader settings: Layout ---
    [ObservableProperty]
    public partial bool ContinuousMode { get; set; }

    [ObservableProperty]
    public partial bool MouseWheelPageTurn { get; set; }

    [ObservableProperty]
    public partial int ChapterSwipeDistance { get; set; }

    [ObservableProperty]
    public partial int HorizontalPadding { get; set; }

    [ObservableProperty]
    public partial int VerticalPadding { get; set; }

    [ObservableProperty]
    public partial bool AvoidPageBreak { get; set; }

    [ObservableProperty]
    public partial bool JustifyText { get; set; }

    [ObservableProperty]
    public partial bool LayoutAdvanced { get; set; }

    [ObservableProperty]
    public partial double LineHeight { get; set; }

    [ObservableProperty]
    public partial double CharacterSpacing { get; set; }

    public bool IsSwipeDistanceVisible => ContinuousMode;
    public bool IsLineHeightVisible => LayoutAdvanced;
    public bool IsCharacterSpacingVisible => LayoutAdvanced;

    // --- Reader settings: Display ---
    [ObservableProperty]
    public partial bool ShowTitle { get; set; }

    [ObservableProperty]
    public partial bool ShowCharacters { get; set; }

    [ObservableProperty]
    public partial bool ShowPercentage { get; set; }

    [ObservableProperty]
    public partial bool ShowProgressTop { get; set; }

    [ObservableProperty]
    public partial bool ShowStatisticsToggle { get; set; }

    [ObservableProperty]
    public partial bool ShowReadingSpeed { get; set; }

    [ObservableProperty]
    public partial bool ShowReadingTime { get; set; }

    public bool IsProgressPositionVisible => ShowCharacters || ShowPercentage;

    // --- Dictionaries ---
    public ObservableCollection<InstalledDictionary> InstalledDictionaries { get; } = [];

    [ObservableProperty]
    public partial bool IsDictionaryListEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDictionaryOperationInProgress { get; set; }

    [ObservableProperty]
    public partial string DictionaryStatusText { get; set; } = "";

    public IAsyncRelayCommand ImportDictionaryCommand { get; }
    public IAsyncRelayCommand<string?> DeleteDictionaryCommand { get; }

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IReaderSettingsService readerSettingsService,
        IProfileRuntimeService profileRuntime,
        IMessenger messenger
    )
    {
        _settingsService = settingsService;
        _readerSettingsService = readerSettingsService;
        _profileRuntime = profileRuntime;
        _messenger = messenger;

        ImportDictionaryCommand = new AsyncRelayCommand(ImportDictionaryAsync);
        DeleteDictionaryCommand = new AsyncRelayCommand<string?>(DeleteDictionaryAsync);

        _ = InitializeAsync();
    }

    public async Task OnNavigatedFromAsync()
    {
        await _settingsService.SaveAsync();
        await _readerSettingsService.SaveAsync();
        await _profileRuntime.SaveActiveSettingsAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to load settings");
        }

        try
        {
            await RefreshDictionariesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to refresh dictionaries");
        }
    }

    private async Task LoadSettingsAsync()
    {
        SelectedThemeMode = _settingsService.Current.Theme;

        var s = _readerSettingsService.Current;

        SepiaMode = s.SepiaMode;

        VerticalWriting = s.VerticalWriting;
        SelectedReaderFont = JapaneseFontCatalog.FindByReaderCssValue(s.SelectedFont)
                             ?? AvailableReaderFonts[0];
        FontSize = s.FontSize;
        HideFurigana = s.HideFurigana;

        ContinuousMode = s.ContinuousMode;
        MouseWheelPageTurn = s.MouseWheelPageTurn;
        ChapterSwipeDistance = s.ChapterSwipeDistance;
        HorizontalPadding = s.HorizontalPadding;
        VerticalPadding = s.VerticalPadding;
        AvoidPageBreak = s.AvoidPageBreak;
        JustifyText = s.JustifyText;
        LayoutAdvanced = s.LayoutAdvanced;
        LineHeight = s.LineHeight;
        CharacterSpacing = s.CharacterSpacing;

        ShowTitle = s.ShowTitle;
        ShowCharacters = s.ShowCharacters;
        ShowPercentage = s.ShowPercentage;
        ShowProgressTop = s.ShowProgressTop;
        ShowStatisticsToggle = s.ShowStatisticsToggle;
        ShowReadingSpeed = s.ShowReadingSpeed;
        ShowReadingTime = s.ShowReadingTime;

        _isInitializing = false;
    }

    private void ApplySetting<T>(Expression<Func<AppSettings, T>> selector, T value)
    {
        if (_isInitializing) return;
        _settingsService.Set(selector, value);
    }

    private void ApplyReaderSetting<T>(Expression<Func<ReaderSettings, T>> selector, T value)
    {
        if (_isInitializing) return;
        _readerSettingsService.Set(selector, value);
    }

    partial void OnSelectedThemeModeChanged(ThemeMode value) => ApplySetting(s => s.Theme, value);

    partial void OnSepiaModeChanged(bool value) => ApplyReaderSetting(s => s.SepiaMode, value);

    partial void OnVerticalWritingChanged(bool value) => ApplyReaderSetting(s => s.VerticalWriting, value);
    partial void OnSelectedReaderFontChanged(JapaneseFontOption value) => ApplyReaderSetting(s => s.SelectedFont, value.ReaderCssValue);
    partial void OnFontSizeChanged(int value) => ApplyReaderSetting(s => s.FontSize, value);
    partial void OnHideFuriganaChanged(bool value) => ApplyReaderSetting(s => s.HideFurigana, value);

    partial void OnContinuousModeChanged(bool value)
    {
        ApplyReaderSetting(s => s.ContinuousMode, value);
        OnPropertyChanged(nameof(IsSwipeDistanceVisible));
    }

    partial void OnMouseWheelPageTurnChanged(bool value) => ApplyReaderSetting(s => s.MouseWheelPageTurn, value);

    partial void OnChapterSwipeDistanceChanged(int value) => ApplyReaderSetting(s => s.ChapterSwipeDistance, value);
    partial void OnHorizontalPaddingChanged(int value) => ApplyReaderSetting(s => s.HorizontalPadding, value);
    partial void OnVerticalPaddingChanged(int value) => ApplyReaderSetting(s => s.VerticalPadding, value);
    partial void OnAvoidPageBreakChanged(bool value) => ApplyReaderSetting(s => s.AvoidPageBreak, value);
    partial void OnJustifyTextChanged(bool value) => ApplyReaderSetting(s => s.JustifyText, value);

    partial void OnLayoutAdvancedChanged(bool value)
    {
        ApplyReaderSetting(s => s.LayoutAdvanced, value);
        OnPropertyChanged(nameof(IsLineHeightVisible));
        OnPropertyChanged(nameof(IsCharacterSpacingVisible));
    }

    partial void OnLineHeightChanged(double value) => ApplyReaderSetting(s => s.LineHeight, value);
    partial void OnCharacterSpacingChanged(double value) => ApplyReaderSetting(s => s.CharacterSpacing, value);

    partial void OnShowTitleChanged(bool value) => ApplyReaderSetting(s => s.ShowTitle, value);
    partial void OnShowCharactersChanged(bool value)
    {
        ApplyReaderSetting(s => s.ShowCharacters, value);
        OnPropertyChanged(nameof(IsProgressPositionVisible));
    }
    partial void OnShowPercentageChanged(bool value)
    {
        ApplyReaderSetting(s => s.ShowPercentage, value);
        OnPropertyChanged(nameof(IsProgressPositionVisible));
    }
    partial void OnShowProgressTopChanged(bool value) => ApplyReaderSetting(s => s.ShowProgressTop, value);
    partial void OnShowStatisticsToggleChanged(bool value) => ApplyReaderSetting(s => s.ShowStatisticsToggle, value);
    partial void OnShowReadingSpeedChanged(bool value) => ApplyReaderSetting(s => s.ShowReadingSpeed, value);
    partial void OnShowReadingTimeChanged(bool value) => ApplyReaderSetting(s => s.ShowReadingTime, value);

    public async Task RefreshDictionariesAsync()
    {
        try
        {
            var importService = App.GetService<IDictionaryImportService>();
            var dicts = await importService.GetInstalledDictionariesAsync();

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    InstalledDictionaries.Clear();
                    foreach (var d in dicts)
                        InstalledDictionaries.Add(d);

                    IsDictionaryListEmpty = InstalledDictionaries.Count == 0;
                    DictionaryStatusText = IsDictionaryListEmpty
                        ? "No dictionaries installed."
                        : $"{InstalledDictionaries.Count} dictionaries installed.";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Settings] Failed to update dictionary list UI");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to refresh dictionary list");
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
            Log.Information("[Settings] Importing dictionary from {Path}", file.Path);

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
                notification.ShowError(
                    $"Failed to import dictionary: {errors}",
                    "Import Failed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Import failed: Type={ExceptionType}, Message={Message}",
                ex.GetType().FullName, ex.Message);
            if (ex.InnerException != null)
                Log.Error(ex.InnerException, "[Settings] Inner exception: {InnerType}: {InnerMessage}",
                    ex.InnerException.GetType().FullName, ex.InnerException.Message);
            App.GetService<INotificationService>()
                .ShowError($"[{ex.GetType().Name}] {ex.Message}", "Import Error");
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
            Log.Information("[Settings] Deleting dictionary '{Dict}'", dictName);

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
            Log.Error(ex, "[Settings] Failed to delete dictionary '{Dict}'", dictName);
            App.GetService<INotificationService>()
                .ShowError(ex.Message, "Delete Error");
        }
        finally
        {
            IsDictionaryOperationInProgress = false;
        }
    }

    public async Task SaveDictionaryOrderAsync()
    {
        try
        {
            var names = InstalledDictionaries.Select(d => d.Name).ToList();
            await App.GetService<IDictionaryImportService>().SaveDictionaryOrderAsync(DictionaryType.Term, names);
            DictionaryStatusText = "Dictionary order saved.";
            Log.Information("[Settings] Dictionary order saved: {Order}", string.Join(", ", names));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to save dictionary order");
            App.GetService<INotificationService>()
                .ShowError(ex.Message, "Dictionary Order");
        }
    }

}
