using System.Globalization;
using Hoshi.Enums;
using Microsoft.UI.Xaml;

namespace Hoshi.Models.Settings;

public class ReaderSettings
{
    // --- Theme ---
    public bool SepiaMode { get; set; } = false;

    // --- Text ---
    public bool VerticalWriting { get; set; } = true;
    public string SelectedFont { get; set; } = JapaneseFontCatalog.DefaultReaderCssValue;
    public int FontSize { get; set; } = 22;
    public bool HideFurigana { get; set; } = false;

    // --- Layout ---
    public bool ContinuousMode { get; set; } = false;
    public bool MouseWheelPageTurn { get; set; } = true;
    public int ChapterSwipeDistance { get; set; } = 20;
    public int HorizontalPadding { get; set; } = 5;
    public int VerticalPadding { get; set; } = 0;
    public bool AvoidPageBreak { get; set; } = false;
    public bool JustifyText { get; set; } = false;
    public bool LayoutAdvanced { get; set; } = false;
    public double LineHeight { get; set; } = 1.65;
    public double CharacterSpacing { get; set; } = 0.0;

    // --- Display ---
    public bool ShowTitle { get; set; } = true;
    public bool ShowCharacters { get; set; } = true;
    public bool ShowPercentage { get; set; } = true;
    public bool ShowProgressTop { get; set; } = true;
    public bool ShowStatisticsToggle { get; set; } = false;
    public bool ShowReadingSpeed { get; set; } = false;
    public bool ShowReadingTime { get; set; } = false;

    // --- Computed CSS properties ---

    public int BottomOverlapPx => VerticalWriting ? FontSize : 0;

    public string WritingModeCss => VerticalWriting ? "vertical-rl" : "horizontal-tb";

    private static string FmtCss(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture);

    public string HorizontalPaddingCss => $"{FmtCss(HorizontalPadding / 2.0)}vw";

    public string VerticalPaddingBlockCss => $"{FmtCss(VerticalPadding / 2.0)}vh";

    public string ColumnGapCss => VerticalWriting
        ? $"calc(var(--hoshi-vertical-padding-gap, {VerticalPadding}vh) + {BottomOverlapPx}px)"
        : $"{HorizontalPadding}vw";

    public string PagePaddingCss =>
        $"var(--hoshi-vertical-padding-block, {VerticalPaddingBlockCss}) {HorizontalPaddingCss}";

    public string BottomPaddingCss => VerticalWriting && BottomOverlapPx > 0
        ? $"calc(var(--hoshi-vertical-padding-block, {VerticalPaddingBlockCss}) + {BottomOverlapPx}px)"
        : $"var(--hoshi-vertical-padding-block, {VerticalPaddingBlockCss})";

    public string ImageMaxWidthFallbackCss => $"{100 - HorizontalPadding}vw";

    public string ImageMaxHeightFallbackCss =>
        $"calc(var(--page-height, 100vh) - {BottomOverlapPx}px)";

    // --- Color methods ---

    private static bool IsDark(ThemeMode themeMode)
    {
        if (themeMode == ThemeMode.Dark) return true;
        if (themeMode == ThemeMode.Light) return false;
        // System: follow OS
        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    public uint BackgroundColor(ThemeMode themeMode)
    {
        if (SepiaMode) return 0xFFF2E2C9;
        return IsDark(themeMode) ? 0xFF000000 : 0xFFFFFFFF;
    }

    public string TextColorCss(ThemeMode themeMode)
    {
        if (SepiaMode) return "#332A1B";
        return IsDark(themeMode) ? "#fff" : "#000";
    }

    public bool UsesDarkInterface(ThemeMode themeMode) => IsDark(themeMode);

    public bool UsesSepiaLightContent() => SepiaMode;
}
