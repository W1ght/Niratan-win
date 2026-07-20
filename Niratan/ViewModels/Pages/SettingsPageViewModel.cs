using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Enums;
using Niratan.Helpers;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Serilog;

namespace Niratan.ViewModels.Pages;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IReaderSettingsService _readerSettingsService;
    private readonly IReaderFontService _readerFontService;
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly IMessenger _messenger;

    private bool _isInitializing = true;

    public string NiratanVersion { get; } = AppInfoHelper.Version;

    // --- App settings ---
    public ThemeMode[] AvailableThemeModes { get; } = Enum.GetValues<ThemeMode>();

    [ObservableProperty]
    public partial ThemeMode SelectedThemeMode { get; set; }

    // --- Reader settings: Theme ---
    [ObservableProperty]
    public partial bool SepiaMode { get; set; }

    [ObservableProperty]
    public partial bool UseCustomReaderColors { get; set; }

    [ObservableProperty]
    public partial Windows.UI.Color CustomReaderBackgroundColor { get; set; }

    [ObservableProperty]
    public partial Windows.UI.Color CustomReaderTextColor { get; set; }

    [ObservableProperty]
    public partial Windows.UI.Color CustomReaderInfoColor { get; set; }

    public bool AreCustomReaderColorsVisible => UseCustomReaderColors;

    // --- Reader settings: Text ---
    [ObservableProperty]
    public partial bool VerticalWriting { get; set; }

    public ObservableCollection<JapaneseFontOption> AvailableReaderFonts { get; } =
        new(JapaneseFontCatalog.Fonts);

    [ObservableProperty]
    public partial JapaneseFontOption SelectedReaderFont { get; set; } = null!;

    [ObservableProperty]
    public partial int FontSize { get; set; }

    [ObservableProperty]
    public partial bool HideFurigana { get; set; }

    public bool CanDeleteSelectedReaderFont => SelectedReaderFont?.IsImported == true;

    // --- Reader settings: Layout ---
    [ObservableProperty]
    public partial bool ContinuousMode { get; set; }

    [ObservableProperty]
    public partial bool TwoColumnHorizontalPages { get; set; }

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
    public partial bool BlurImages { get; set; }

    [ObservableProperty]
    public partial bool LayoutAdvanced { get; set; }

    [ObservableProperty]
    public partial double LineHeight { get; set; }

    [ObservableProperty]
    public partial double CharacterSpacing { get; set; }

    [ObservableProperty]
    public partial double ParagraphSpacing { get; set; }

    public bool IsSwipeDistanceVisible => ContinuousMode;
    public bool IsTwoColumnHorizontalPagesVisible => !ContinuousMode && !VerticalWriting;
    public bool IsLineHeightVisible => LayoutAdvanced;
    public bool IsCharacterSpacingVisible => LayoutAdvanced;
    public bool IsParagraphSpacingVisible => LayoutAdvanced;

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

    // --- Popup appearance ---
    [ObservableProperty]
    public partial int PopupMaxWidth { get; set; }

    [ObservableProperty]
    public partial int PopupMaxHeight { get; set; }

    [ObservableProperty]
    public partial double PopupScale { get; set; }

    [ObservableProperty]
    public partial bool PopupActionBar { get; set; }

    [ObservableProperty]
    public partial bool PopupFullWidth { get; set; }

    public string PopupMaxWidthText => $"{PopupMaxWidth} px";
    public string PopupMaxHeightText => $"{PopupMaxHeight} px";
    public string PopupScaleText => PopupScale.ToString("0.00", CultureInfo.InvariantCulture);

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
    public IAsyncRelayCommand ImportReaderFontCommand { get; }
    public IAsyncRelayCommand DeleteSelectedReaderFontCommand { get; }

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IReaderSettingsService readerSettingsService,
        IReaderFontService readerFontService,
        IProfileRuntimeService profileRuntime,
        IMessenger messenger
    )
    {
        _settingsService = settingsService;
        _readerSettingsService = readerSettingsService;
        _readerFontService = readerFontService;
        _profileRuntime = profileRuntime;
        _messenger = messenger;

        ImportDictionaryCommand = new AsyncRelayCommand(ImportDictionaryAsync);
        DeleteDictionaryCommand = new AsyncRelayCommand<string?>(DeleteDictionaryAsync);
        ImportReaderFontCommand = new AsyncRelayCommand(ImportReaderFontAsync);
        DeleteSelectedReaderFontCommand = new AsyncRelayCommand(DeleteSelectedReaderFontAsync);

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
        UseCustomReaderColors = s.UseCustomColors;
        CustomReaderBackgroundColor = ParseColor(s.CustomBackgroundColor, 0xFFFFFFFF);
        CustomReaderTextColor = ParseColor(s.CustomTextColor, 0xFF000000);
        CustomReaderInfoColor = ParseColor(s.CustomInfoColor, 0xFF999999);

        VerticalWriting = s.VerticalWriting;
        RefreshAvailableReaderFonts();
        SelectedReaderFont = AvailableReaderFonts.FirstOrDefault(font =>
                                 string.Equals(font.ReaderCssValue, s.SelectedFont, StringComparison.Ordinal)
                                 && string.Equals(font.ImportedFileName, s.SelectedFontFileName, StringComparison.Ordinal))
                             ?? JapaneseFontCatalog.FindByReaderCssValue(s.SelectedFont)
                             ?? AvailableReaderFonts[0];
        FontSize = s.FontSize;
        HideFurigana = s.HideFurigana;

        ContinuousMode = s.ContinuousMode;
        TwoColumnHorizontalPages = s.TwoColumnHorizontalPages;
        MouseWheelPageTurn = s.MouseWheelPageTurn;
        ChapterSwipeDistance = s.ChapterSwipeDistance;
        HorizontalPadding = s.HorizontalPadding;
        VerticalPadding = s.VerticalPadding;
        AvoidPageBreak = s.AvoidPageBreak;
        JustifyText = s.JustifyText;
        BlurImages = s.BlurImages;
        LayoutAdvanced = s.LayoutAdvanced;
        LineHeight = s.LineHeight;
        CharacterSpacing = s.CharacterSpacing;
        ParagraphSpacing = s.ParagraphSpacing;

        ShowTitle = s.ShowTitle;
        ShowCharacters = s.ShowCharacters;
        ShowPercentage = s.ShowPercentage;
        ShowProgressTop = s.ShowProgressTop;
        ShowStatisticsToggle = s.ShowStatisticsToggle;
        ShowReadingSpeed = s.ShowReadingSpeed;
        ShowReadingTime = s.ShowReadingTime;

        var popup = _settingsService.Current.DictionaryDisplaySettings;
        PopupMaxWidth = DictionaryPopupAppearanceConstraints.NormalizeWidth(popup.PopupMaxWidth);
        PopupMaxHeight = DictionaryPopupAppearanceConstraints.NormalizeHeight(popup.PopupMaxHeight);
        PopupScale = DictionaryPopupAppearanceConstraints.NormalizeScale(popup.PopupScale);
        PopupActionBar = popup.PopupActionBar;
        PopupFullWidth = popup.PopupFullWidth;

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

    private void ApplyPopupSetting(Func<DictionaryDisplaySettings, DictionaryDisplaySettings> update)
    {
        if (_isInitializing) return;
        var current = _settingsService.Current.DictionaryDisplaySettings;
        _settingsService.Set(s => s.DictionaryDisplaySettings, update(current));
    }

    partial void OnSelectedThemeModeChanged(ThemeMode value) => ApplySetting(s => s.Theme, value);

    partial void OnSepiaModeChanged(bool value) => ApplyReaderSetting(s => s.SepiaMode, value);

    partial void OnUseCustomReaderColorsChanged(bool value)
    {
        ApplyReaderSetting(s => s.UseCustomColors, value);
        OnPropertyChanged(nameof(AreCustomReaderColorsVisible));
    }

    partial void OnCustomReaderBackgroundColorChanged(Windows.UI.Color value) =>
        ApplyReaderSetting(s => s.CustomBackgroundColor, ToHex(value));

    partial void OnCustomReaderTextColorChanged(Windows.UI.Color value) =>
        ApplyReaderSetting(s => s.CustomTextColor, ToHex(value));

    partial void OnCustomReaderInfoColorChanged(Windows.UI.Color value) =>
        ApplyReaderSetting(s => s.CustomInfoColor, ToHex(value));

    partial void OnVerticalWritingChanged(bool value)
    {
        ApplyReaderSetting(s => s.VerticalWriting, value);
        OnPropertyChanged(nameof(IsTwoColumnHorizontalPagesVisible));
    }

    partial void OnSelectedReaderFontChanged(JapaneseFontOption value)
    {
        if (value is null) return;
        ApplyReaderSetting(s => s.SelectedFont, value.ReaderCssValue);
        ApplyReaderSetting(s => s.SelectedFontFileName, value.ImportedFileName);
        OnPropertyChanged(nameof(CanDeleteSelectedReaderFont));
    }
    partial void OnFontSizeChanged(int value) => ApplyReaderSetting(s => s.FontSize, value);
    partial void OnHideFuriganaChanged(bool value) => ApplyReaderSetting(s => s.HideFurigana, value);

    partial void OnContinuousModeChanged(bool value)
    {
        ApplyReaderSetting(s => s.ContinuousMode, value);
        OnPropertyChanged(nameof(IsSwipeDistanceVisible));
        OnPropertyChanged(nameof(IsTwoColumnHorizontalPagesVisible));
    }

    partial void OnTwoColumnHorizontalPagesChanged(bool value) =>
        ApplyReaderSetting(s => s.TwoColumnHorizontalPages, value);

    partial void OnMouseWheelPageTurnChanged(bool value) => ApplyReaderSetting(s => s.MouseWheelPageTurn, value);

    partial void OnChapterSwipeDistanceChanged(int value) => ApplyReaderSetting(s => s.ChapterSwipeDistance, value);
    partial void OnHorizontalPaddingChanged(int value) => ApplyReaderSetting(s => s.HorizontalPadding, value);
    partial void OnVerticalPaddingChanged(int value) => ApplyReaderSetting(s => s.VerticalPadding, value);
    partial void OnAvoidPageBreakChanged(bool value) => ApplyReaderSetting(s => s.AvoidPageBreak, value);
    partial void OnJustifyTextChanged(bool value) => ApplyReaderSetting(s => s.JustifyText, value);
    partial void OnBlurImagesChanged(bool value) => ApplyReaderSetting(s => s.BlurImages, value);

    partial void OnLayoutAdvancedChanged(bool value)
    {
        ApplyReaderSetting(s => s.LayoutAdvanced, value);
        OnPropertyChanged(nameof(IsLineHeightVisible));
        OnPropertyChanged(nameof(IsCharacterSpacingVisible));
        OnPropertyChanged(nameof(IsParagraphSpacingVisible));
    }

    partial void OnLineHeightChanged(double value) => ApplyReaderSetting(s => s.LineHeight, value);
    partial void OnCharacterSpacingChanged(double value) => ApplyReaderSetting(s => s.CharacterSpacing, value);
    partial void OnParagraphSpacingChanged(double value) => ApplyReaderSetting(s => s.ParagraphSpacing, value);

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

    partial void OnPopupMaxWidthChanged(int value)
    {
        OnPropertyChanged(nameof(PopupMaxWidthText));
        ApplyPopupSetting(current => current with
        {
            PopupMaxWidth = DictionaryPopupAppearanceConstraints.NormalizeWidth(value),
        });
    }

    partial void OnPopupMaxHeightChanged(int value)
    {
        OnPropertyChanged(nameof(PopupMaxHeightText));
        ApplyPopupSetting(current => current with
        {
            PopupMaxHeight = DictionaryPopupAppearanceConstraints.NormalizeHeight(value),
        });
    }

    partial void OnPopupScaleChanged(double value)
    {
        OnPropertyChanged(nameof(PopupScaleText));
        ApplyPopupSetting(current => current with
        {
            PopupScale = DictionaryPopupAppearanceConstraints.NormalizeScale(value),
        });
    }

    partial void OnPopupActionBarChanged(bool value) =>
        ApplyPopupSetting(current => current with { PopupActionBar = value });

    partial void OnPopupFullWidthChanged(bool value) =>
        ApplyPopupSetting(current => current with { PopupFullWidth = value });

    private void RefreshAvailableReaderFonts()
    {
        AvailableReaderFonts.Clear();
        foreach (var font in _readerFontService.GetAvailableFonts())
            AvailableReaderFonts.Add(font);
    }

    private async Task ImportReaderFontAsync()
    {
        try
        {
            var filePath = await App.GetService<IDialogService>().OpenFilePickerAsync(
                ".ttf", ".otf", ".woff", ".woff2");
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var imported = await _readerFontService.ImportAsync(filePath);
            RefreshAvailableReaderFonts();
            SelectedReaderFont = AvailableReaderFonts.First(font =>
                string.Equals(font.ImportedFileName, imported.ImportedFileName, StringComparison.Ordinal));
            App.GetService<INotificationService>().ShowSuccess(
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString(
                        "ReaderFontImportedMessage",
                        "Imported '{0}'."),
                    imported.Name),
                ResourceStringHelper.GetString("ReaderFontNotificationTitle", "Reader Font"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to import reader font");
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("ReaderFontNotificationTitle", "Reader Font"));
        }
    }

    private async Task DeleteSelectedReaderFontAsync()
    {
        var selected = SelectedReaderFont;
        if (selected?.ImportedFileName is not { } fileName) return;

        try
        {
            var confirmed = await App.GetService<IDialogService>().ConfirmAsync(
                ResourceStringHelper.GetString("ReaderFontDeleteDialogTitle", "Delete Reader Font"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString(
                        "ReaderFontDeleteDialogMessage",
                        "Delete '{0}'? The font file will be removed from Niratan."),
                    selected.Name));
            if (!confirmed) return;

            await _readerFontService.DeleteAsync(fileName);
            RefreshAvailableReaderFonts();
            SelectedReaderFont = JapaneseFontCatalog.FindByReaderCssValue(
                JapaneseFontCatalog.DefaultReaderCssValue) ?? AvailableReaderFonts[0];
            App.GetService<INotificationService>().ShowSuccess(
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceStringHelper.GetString(
                        "ReaderFontDeletedMessage",
                        "Deleted '{0}'."),
                    selected.Name),
                ResourceStringHelper.GetString("ReaderFontNotificationTitle", "Reader Font"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Settings] Failed to delete reader font {Font}", fileName);
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("ReaderFontNotificationTitle", "Reader Font"));
        }
    }

    private static string ToHex(Windows.UI.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Windows.UI.Color ParseColor(string? value, uint fallback)
    {
        var hex = value?.Trim().TrimStart('#');
        if (hex is not null
            && (hex.Length == 6 || hex.Length == 8)
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            var argb = hex.Length == 6 ? 0xFF000000 | parsed : parsed;
            return Windows.UI.Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }

        return Windows.UI.Color.FromArgb(
            (byte)(fallback >> 24),
            (byte)(fallback >> 16),
            (byte)(fallback >> 8),
            (byte)fallback);
    }

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
                Log.Information("[Settings] Importing dictionary {Index}/{Count} from {Path}",
                    index + 1, files.Count, file.Path);

                try
                {
                    var result = await importService.ImportAsync(file.Path);
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
                    Log.Error(ex, "[Settings] Failed to import dictionary {Index}/{Count} from {Path}",
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
