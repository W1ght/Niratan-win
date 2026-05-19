using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Enums;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;

namespace Hoshi.ViewModels.Dialogs;

public sealed record ReaderFontOption(string Name, string CssValue);

public partial class ReaderAppearanceViewModel : ObservableObject
{
    private readonly IReaderSettingsService _readerSettingsService;
    private bool _isInitializing = true;

    // --- Theme ---
    [ObservableProperty]
    public partial ReaderTheme SelectedTheme { get; set; }

    [ObservableProperty]
    public partial bool EInkMode { get; set; }

    [ObservableProperty]
    public partial bool SystemLightSepia { get; set; }

    [ObservableProperty]
    public partial bool SepiaInvertInDark { get; set; }

    // --- Text ---
    [ObservableProperty]
    public partial bool VerticalWriting { get; set; }

    [ObservableProperty]
    public partial ReaderFontOption SelectedFont { get; set; } = null!;

    [ObservableProperty]
    public partial int FontSize { get; set; }

    [ObservableProperty]
    public partial bool HideFurigana { get; set; }

    // --- Layout ---
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

    // --- Display ---
    [ObservableProperty]
    public partial bool ShowTitle { get; set; }

    [ObservableProperty]
    public partial bool ShowCharacters { get; set; }

    [ObservableProperty]
    public partial bool ShowPercentage { get; set; }

    [ObservableProperty]
    public partial bool ShowProgressTop { get; set; }

    // --- Visibility gating ---
    public bool IsSystemLightSepiaVisible => SelectedTheme == ReaderTheme.System;
    public bool IsSepiaInvertVisible => SelectedTheme == ReaderTheme.Sepia;
    public bool IsSwipeDistanceVisible => ContinuousMode;
    public bool IsLineHeightVisible => LayoutAdvanced;
    public bool IsCharacterSpacingVisible => LayoutAdvanced;
    public bool IsProgressPositionVisible => ShowCharacters || ShowPercentage;

    // --- Options ---
    public List<ReaderTheme> AvailableThemes { get; } = new()
    {
        ReaderTheme.System, ReaderTheme.Light, ReaderTheme.Dark, ReaderTheme.Sepia,
    };

    public List<ReaderFontOption> AvailableFonts { get; } = new()
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

    public ReaderAppearanceViewModel(IReaderSettingsService readerSettingsService)
    {
        _readerSettingsService = readerSettingsService;
    }

    public void LoadFromSettings()
    {
        _isInitializing = true;

        var s = _readerSettingsService.Current;
        SelectedTheme = s.Theme;
        EInkMode = s.EInkMode;
        SystemLightSepia = s.SystemLightSepia;
        SepiaInvertInDark = s.SepiaInvertInDark;

        VerticalWriting = s.VerticalWriting;
        SelectedFont = AvailableFonts.FirstOrDefault(f => f.CssValue == s.SelectedFont)
                       ?? AvailableFonts[0];
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

    private void ApplySetting<T>(Expression<Func<ReaderSettings, T>> selector, T value)
    {
        if (_isInitializing)
            return;
        _readerSettingsService.Set(selector, value);
    }

    partial void OnSelectedThemeChanged(ReaderTheme value)
    {
        ApplySetting(s => s.Theme, value);
        OnPropertyChanged(nameof(IsSystemLightSepiaVisible));
        OnPropertyChanged(nameof(IsSepiaInvertVisible));
    }

    partial void OnEInkModeChanged(bool value) => ApplySetting(s => s.EInkMode, value);
    partial void OnSystemLightSepiaChanged(bool value) => ApplySetting(s => s.SystemLightSepia, value);
    partial void OnSepiaInvertInDarkChanged(bool value) => ApplySetting(s => s.SepiaInvertInDark, value);

    partial void OnVerticalWritingChanged(bool value) => ApplySetting(s => s.VerticalWriting, value);
    partial void OnSelectedFontChanged(ReaderFontOption value) => ApplySetting(s => s.SelectedFont, value.CssValue);
    partial void OnFontSizeChanged(int value) => ApplySetting(s => s.FontSize, value);
    partial void OnHideFuriganaChanged(bool value) => ApplySetting(s => s.HideFurigana, value);

    partial void OnContinuousModeChanged(bool value)
    {
        ApplySetting(s => s.ContinuousMode, value);
        OnPropertyChanged(nameof(IsSwipeDistanceVisible));
    }

    partial void OnChapterSwipeDistanceChanged(int value) => ApplySetting(s => s.ChapterSwipeDistance, value);
    partial void OnHorizontalPaddingChanged(int value) => ApplySetting(s => s.HorizontalPadding, value);
    partial void OnVerticalPaddingChanged(int value) => ApplySetting(s => s.VerticalPadding, value);
    partial void OnAvoidPageBreakChanged(bool value) => ApplySetting(s => s.AvoidPageBreak, value);
    partial void OnJustifyTextChanged(bool value) => ApplySetting(s => s.JustifyText, value);

    partial void OnLayoutAdvancedChanged(bool value)
    {
        ApplySetting(s => s.LayoutAdvanced, value);
        OnPropertyChanged(nameof(IsLineHeightVisible));
        OnPropertyChanged(nameof(IsCharacterSpacingVisible));
    }

    partial void OnLineHeightChanged(double value) => ApplySetting(s => s.LineHeight, value);
    partial void OnCharacterSpacingChanged(double value) => ApplySetting(s => s.CharacterSpacing, value);

    partial void OnShowTitleChanged(bool value) => ApplySetting(s => s.ShowTitle, value);
    partial void OnShowCharactersChanged(bool value)
    {
        ApplySetting(s => s.ShowCharacters, value);
        OnPropertyChanged(nameof(IsProgressPositionVisible));
    }

    partial void OnShowPercentageChanged(bool value)
    {
        ApplySetting(s => s.ShowPercentage, value);
        OnPropertyChanged(nameof(IsProgressPositionVisible));
    }

    partial void OnShowProgressTopChanged(bool value) => ApplySetting(s => s.ShowProgressTop, value);

    [RelayCommand]
    private void Done() { }
}
