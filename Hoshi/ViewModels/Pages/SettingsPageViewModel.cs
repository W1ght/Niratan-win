using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Helpers;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;

namespace Hoshi.ViewModels.Pages;

public sealed record ReaderFontOption(string Name, string CssValue);

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IReaderSettingsService _readerSettingsService;
    private readonly IMessenger _messenger;

    private bool _isInitializing = true;

    public string HoshiVersion { get; } = AppInfoHelper.Version;

    // --- App settings ---
    public ThemeMode[] AvailableThemeModes { get; } = Enum.GetValues<ThemeMode>();

    [ObservableProperty]
    public partial ThemeMode SelectedThemeMode { get; set; }

    // --- Reader settings: Theme ---
    public List<ReaderTheme> AvailableReaderThemes { get; } = new()
    {
        ReaderTheme.System, ReaderTheme.Light, ReaderTheme.Dark, ReaderTheme.Sepia,
    };

    [ObservableProperty]
    public partial ReaderTheme SelectedReaderTheme { get; set; }

    [ObservableProperty]
    public partial bool EInkMode { get; set; }

    [ObservableProperty]
    public partial bool SystemLightSepia { get; set; }

    [ObservableProperty]
    public partial bool SepiaInvertInDark { get; set; }

    public bool IsSystemSepiaLightVisible => SelectedReaderTheme == ReaderTheme.System;
    public bool IsSepiaInvertVisible => SelectedReaderTheme == ReaderTheme.Sepia;

    // --- Reader settings: Text ---
    [ObservableProperty]
    public partial bool VerticalWriting { get; set; }

    public List<ReaderFontOption> AvailableReaderFonts { get; } = new()
    {
        new ReaderFontOption("System Default", "system-ui, sans-serif"),
        new ReaderFontOption("Noto Serif CJK JP", "'Noto Serif CJK JP', serif"),
        new ReaderFontOption("Noto Sans CJK JP", "'Noto Sans CJK JP', sans-serif"),
        new ReaderFontOption("Yu Mincho", "'Yu Mincho', serif"),
        new ReaderFontOption("Yu Gothic", "'Yu Gothic', sans-serif"),
        new ReaderFontOption("MS Mincho", "'MS Mincho', serif"),
        new ReaderFontOption("MS Gothic", "'MS Gothic', sans-serif"),
        new ReaderFontOption("SimSun", "SimSun, serif"),
        new ReaderFontOption("Microsoft YaHei", "'Microsoft YaHei', sans-serif"),
    };

    [ObservableProperty]
    public partial ReaderFontOption SelectedReaderFont { get; set; } = null!;

    [ObservableProperty]
    public partial int FontSize { get; set; }

    [ObservableProperty]
    public partial bool HideFurigana { get; set; }

    // --- Reader settings: Layout ---
    [ObservableProperty]
    public partial bool ContinuousMode { get; set; }

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

    public bool IsProgressPositionVisible => ShowCharacters || ShowPercentage;

    public SettingsPageViewModel(
        ISettingsService settingsService,
        IReaderSettingsService readerSettingsService,
        IMessenger messenger
    )
    {
        _settingsService = settingsService;
        _readerSettingsService = readerSettingsService;
        _messenger = messenger;

        _ = LoadSettingsAsync();
    }

    public async Task OnNavigatedFromAsync()
    {
        await _settingsService.SaveAsync();
        await _readerSettingsService.SaveAsync();
    }

    private async Task LoadSettingsAsync()
    {
        SelectedThemeMode = _settingsService.Current.Theme;

        var s = _readerSettingsService.Current;

        SelectedReaderTheme = s.Theme;
        EInkMode = s.EInkMode;
        SystemLightSepia = s.SystemLightSepia;
        SepiaInvertInDark = s.SepiaInvertInDark;

        VerticalWriting = s.VerticalWriting;
        SelectedReaderFont = AvailableReaderFonts.FirstOrDefault(f => f.CssValue == s.SelectedFont)
                             ?? AvailableReaderFonts[0];
        FontSize = s.FontSize;
        HideFurigana = s.HideFurigana;

        ContinuousMode = s.ContinuousMode;
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

    partial void OnSelectedReaderThemeChanged(ReaderTheme value)
    {
        ApplyReaderSetting(s => s.Theme, value);
        OnPropertyChanged(nameof(IsSystemSepiaLightVisible));
        OnPropertyChanged(nameof(IsSepiaInvertVisible));
    }

    partial void OnEInkModeChanged(bool value) => ApplyReaderSetting(s => s.EInkMode, value);
    partial void OnSystemLightSepiaChanged(bool value) => ApplyReaderSetting(s => s.SystemLightSepia, value);
    partial void OnSepiaInvertInDarkChanged(bool value) => ApplyReaderSetting(s => s.SepiaInvertInDark, value);

    partial void OnVerticalWritingChanged(bool value) => ApplyReaderSetting(s => s.VerticalWriting, value);
    partial void OnSelectedReaderFontChanged(ReaderFontOption value) => ApplyReaderSetting(s => s.SelectedFont, value.CssValue);
    partial void OnFontSizeChanged(int value) => ApplyReaderSetting(s => s.FontSize, value);
    partial void OnHideFuriganaChanged(bool value) => ApplyReaderSetting(s => s.HideFurigana, value);

    partial void OnContinuousModeChanged(bool value)
    {
        ApplyReaderSetting(s => s.ContinuousMode, value);
        OnPropertyChanged(nameof(IsSwipeDistanceVisible));
    }

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
}
