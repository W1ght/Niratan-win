using System.Globalization;
using Hoshi.Enums;

namespace Hoshi.Models.Settings;

public class ReaderSettings
{
    // --- Theme ---
    public ReaderTheme Theme { get; set; } = ReaderTheme.System;
    public bool EInkMode { get; set; } = false;
    public bool SystemLightSepia { get; set; } = false;
    public bool SepiaInvertInDark { get; set; } = false;

    // --- Text ---
    public bool VerticalWriting { get; set; } = true;
    public string SelectedFont { get; set; } = "system-ui, sans-serif";
    public int FontSize { get; set; } = 22;
    public bool HideFurigana { get; set; } = false;

    // --- Layout ---
    public bool ContinuousMode { get; set; } = false;
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

    public uint BackgroundColor(bool systemDark) => Theme switch
    {
        ReaderTheme.System => systemDark ? 0xFF000000 : (SystemLightSepia ? 0xFFF2E2C9 : 0xFFFFFFFF),
        ReaderTheme.Dark => 0xFF000000,
        ReaderTheme.Sepia => SepiaInvertInDark && systemDark ? 0xFF18150C : 0xFFF2E2C9,
        ReaderTheme.Light => 0xFFFFFFFF,
        _ => 0xFFFFFFFF,
    };

    public string TextColorCss(bool systemDark) => Theme switch
    {
        ReaderTheme.System => systemDark ? "#fff" : (SystemLightSepia ? "#332A1B" : "#000"),
        ReaderTheme.Dark => "#fff",
        ReaderTheme.Sepia => SepiaInvertInDark && systemDark ? "#F2E2C9" : "#332A1B",
        ReaderTheme.Light => "#000",
        _ => "#000",
    };

    public bool UsesDarkInterface(bool systemDark) => Theme switch
    {
        ReaderTheme.System => systemDark,
        ReaderTheme.Light => false,
        ReaderTheme.Dark => true,
        ReaderTheme.Sepia => SepiaInvertInDark && systemDark,
        _ => false,
    };

    public bool UsesSepiaLightContent(bool systemDark) =>
        !EInkMode && (
            Theme == ReaderTheme.Sepia && !(SepiaInvertInDark && systemDark) ||
            Theme == ReaderTheme.System && SystemLightSepia && !systemDark
        );
}
