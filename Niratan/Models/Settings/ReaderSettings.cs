using System;
using System.Globalization;
using System.IO;
using Niratan.Enums;
using Microsoft.UI.Xaml;

namespace Niratan.Models.Settings;

public class ReaderSettings
{
    // --- Theme ---
    public bool SepiaMode { get; set; } = false;
    public bool UseCustomColors { get; set; } = false;
    public string CustomBackgroundColor { get; set; } = "#FFFFFF";
    public string CustomTextColor { get; set; } = "#000000";
    public string CustomInfoColor { get; set; } = "#999999";

    // --- Text ---
    public bool VerticalWriting { get; set; } = true;
    public string SelectedFont { get; set; } = JapaneseFontCatalog.DefaultReaderCssValue;
    public string? SelectedFontFileName { get; set; }
    public int FontSize { get; set; } = 22;
    public bool HideFurigana { get; set; } = false;

    // --- Layout ---
    public bool ContinuousMode { get; set; } = false;
    public bool TwoColumnHorizontalPages { get; set; } = false;
    public bool MouseWheelPageTurn { get; set; } = true;
    public int ChapterSwipeDistance { get; set; } = 20;
    public int HorizontalPadding { get; set; } = 5;
    public int VerticalPadding { get; set; } = 0;
    public bool AvoidPageBreak { get; set; } = false;
    public bool JustifyText { get; set; } = false;
    public bool BlurImages { get; set; } = false;
    public bool LayoutAdvanced { get; set; } = false;
    public double LineHeight { get; set; } = 1.65;
    public double CharacterSpacing { get; set; } = 0.0;
    public double ParagraphSpacing { get; set; } = 0.0;

    // --- Display ---
    public bool ShowTitle { get; set; } = true;
    public bool ShowCharacters { get; set; } = true;
    public bool ShowPercentage { get; set; } = true;
    public bool ShowProgressTop { get; set; } = true;
    public bool ShowStatisticsToggle { get; set; } = true;
    public bool ShowReadingSpeed { get; set; } = true;
    public bool ShowReadingTime { get; set; } = true;
    public bool BlurUnreadGalleryImages { get; set; } = true;

    // --- Computed CSS properties ---

    public int BottomOverlapPx => VerticalWriting ? FontSize : 0;

    public string WritingModeCss => VerticalWriting ? "vertical-rl" : "horizontal-tb";

    private static string FmtCss(double value) =>
        value.ToString("F1", CultureInfo.InvariantCulture);

    public string HorizontalPaddingCss => $"{FmtCss(HorizontalPadding / 2.0)}vw";

    public string VerticalPaddingBlockCss => $"{FmtCss(VerticalPadding / 2.0)}vh";

    public string ColumnGapCss => VerticalWriting
        ? $"calc(var(--niratan-vertical-padding-gap, {VerticalPadding}vh) + {BottomOverlapPx}px)"
        : $"{HorizontalPadding}vw";

    public string PagePaddingCss =>
        $"var(--niratan-vertical-padding-block, {VerticalPaddingBlockCss}) {HorizontalPaddingCss}";

    public string BottomPaddingCss => VerticalWriting && BottomOverlapPx > 0
        ? $"calc(var(--niratan-vertical-padding-block, {VerticalPaddingBlockCss}) + {BottomOverlapPx}px)"
        : $"var(--niratan-vertical-padding-block, {VerticalPaddingBlockCss})";

    public string ImageMaxWidthFallbackCss => $"{100 - HorizontalPadding}vw";

    public string ImageMaxHeightFallbackCss =>
        $"calc(var(--page-height, 100vh) - {BottomOverlapPx}px)";

    public bool UsesTwoColumnHorizontalPages =>
        TwoColumnHorizontalPages && !VerticalWriting && !ContinuousMode;

    public string? ImportedFontUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedFontFileName))
                return null;

            var fileName = Path.GetFileName(SelectedFontFileName);
            return string.Equals(fileName, SelectedFontFileName, StringComparison.Ordinal)
                ? $"https://{ReaderFontCatalog.VirtualHostName}/{Uri.EscapeDataString(fileName)}"
                : null;
        }
    }

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
        if (UseCustomColors && TryParseColor(CustomBackgroundColor, out var custom)) return custom;
        if (SepiaMode) return 0xFFF2E2C9;
        return IsDark(themeMode) ? 0xFF000000 : 0xFFFFFFFF;
    }

    public string TextColorCss(ThemeMode themeMode)
    {
        if (UseCustomColors && TryNormalizeCssColor(CustomTextColor, out var custom)) return custom;
        if (SepiaMode) return "#332A1B";
        return IsDark(themeMode) ? "#fff" : "#000";
    }

    public string InfoColorCss(ThemeMode themeMode)
    {
        if (UseCustomColors && TryNormalizeCssColor(CustomInfoColor, out var custom)) return custom;
        if (SepiaMode) return "#74664F";
        return IsDark(themeMode) ? "#A6A6A6" : "#666666";
    }

    public uint InfoColor(ThemeMode themeMode)
    {
        if (UseCustomColors && TryParseColor(CustomInfoColor, out var custom)) return custom;
        if (SepiaMode) return 0xFF74664F;
        return IsDark(themeMode) ? 0xFFA6A6A6 : 0xFF666666;
    }

    public bool UsesDarkInterface(ThemeMode themeMode) => IsDark(themeMode);

    public bool UsesSepiaLightContent() => SepiaMode;

    private static bool TryNormalizeCssColor(string? value, out string color)
    {
        color = "";
        if (!TryParseColor(value, out var argb))
            return false;

        color = $"#{argb & 0x00FFFFFF:X6}";
        return true;
    }

    private static bool TryParseColor(string? value, out uint argb)
    {
        argb = 0;
        var hex = value?.Trim().TrimStart('#');
        if (hex is null || (hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        argb = hex.Length == 6 ? 0xFF000000 | parsed : parsed;
        return true;
    }
}
