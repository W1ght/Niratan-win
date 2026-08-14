using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using Niratan.Enums;
using Microsoft.UI.Xaml;

namespace Niratan.Models.Settings;

public class ReaderSettings
{
    // --- Theme ---
    public ReaderTheme? Theme { get; set; }
    public ThemeMode? CustomInterfaceTheme { get; set; }
    public bool SystemLightSepia { get; set; } = false;
    public bool SepiaInvertInDark { get; set; } = false;

    // Retained as a migration bridge for settings written by v0.6.x. A null Theme
    // resolves from these flags so existing selections survive the unified picker.
    public bool SepiaMode { get; set; } = false;
    public bool UseCustomColors { get; set; } = false;
    public string CustomBackgroundColor { get; set; } = "#FFFFFF";
    public string CustomTextColor { get; set; } = "#000000";
    public string CustomInfoColor { get; set; } = "#999999";

    [JsonIgnore]
    public ReaderTheme EffectiveTheme => Theme
        ?? (UseCustomColors
            ? ReaderTheme.Custom
            : SepiaMode ? ReaderTheme.Sepia : ReaderTheme.System);

    [JsonIgnore]
    public bool UsesCustomColors => EffectiveTheme == ReaderTheme.Custom;

    public ReaderTheme ResolveUnifiedTheme(ThemeMode legacyAppTheme)
    {
        if (Theme.HasValue || SepiaMode || UseCustomColors)
            return EffectiveTheme;

        return legacyAppTheme switch
        {
            ThemeMode.Light => ReaderTheme.Light,
            ThemeMode.Dark => ReaderTheme.Dark,
            _ => ReaderTheme.System,
        };
    }

    public ThemeMode ResolveInterfaceTheme(ThemeMode customFallback)
    {
        return EffectiveTheme switch
        {
            ReaderTheme.Light => ThemeMode.Light,
            ReaderTheme.Dark => ThemeMode.Dark,
            ReaderTheme.Sepia => SepiaInvertInDark ? ThemeMode.System : ThemeMode.Light,
            ReaderTheme.Custom => CustomInterfaceTheme ?? customFallback,
            _ => ThemeMode.System,
        };
    }

    // --- Text ---
    public bool VerticalWriting { get; set; } = true;
    public string SelectedFont { get; set; } = JapaneseFontCatalog.DefaultReaderCssValue;
    public string? SelectedFontFileName { get; set; }
    public int FontWeight { get; set; } = 400;
    public int FontSize { get; set; } = 22;
    public bool HideFurigana { get; set; } = false;

    // --- Layout ---
    public bool ContinuousMode { get; set; } = false;
    public bool VisualNovelMode { get; set; } = false;
    public int VisualNovelRevealSpeed { get; set; } = 45;
    public VisualNovelScreenMode VisualNovelScreenMode { get; set; } = VisualNovelScreenMode.Block;
    public int VisualNovelSentencesPerScreen { get; set; } = 1;
    public bool VisualNovelPreserveDialogue { get; set; } = false;
    public bool VisualNovelClickAdvance { get; set; } = false;
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
        TwoColumnHorizontalPages && !VerticalWriting && !ContinuousMode && !VisualNovelMode;

    public int NormalizedVisualNovelRevealSpeed => Math.Clamp(VisualNovelRevealSpeed, 0, 120);

    public int NormalizedFontWeight => Math.Clamp(FontWeight, 100, 900);

    public int NormalizedVisualNovelSentencesPerScreen =>
        Math.Clamp(VisualNovelSentencesPerScreen, 1, 12);

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

    private bool UsesInvertedSepia(ThemeMode themeMode) =>
        EffectiveTheme == ReaderTheme.Sepia && SepiaInvertInDark && IsDark(themeMode);

    private bool UsesSystemLightSepia(ThemeMode themeMode) =>
        EffectiveTheme == ReaderTheme.System && SystemLightSepia && !IsDark(themeMode);

    public uint BackgroundColor(ThemeMode themeMode) => EffectiveTheme switch
    {
        ReaderTheme.Light => 0xFFFFFFFF,
        ReaderTheme.Dark => 0xFF000000,
        ReaderTheme.Sepia => UsesInvertedSepia(themeMode) ? 0xFF18150C : 0xFFF2E2C9,
        ReaderTheme.Custom when TryParseColor(CustomBackgroundColor, out var custom) => custom,
        ReaderTheme.Custom => 0xFFFFFFFF,
        ReaderTheme.System when UsesSystemLightSepia(themeMode) => 0xFFF2E2C9,
        _ => IsDark(themeMode) ? 0xFF000000 : 0xFFFFFFFF,
    };

    public string TextColorCss(ThemeMode themeMode) => EffectiveTheme switch
    {
        ReaderTheme.Light => "#000",
        ReaderTheme.Dark => "#fff",
        ReaderTheme.Sepia => UsesInvertedSepia(themeMode) ? "#F2E2C9" : "#332A1B",
        ReaderTheme.Custom when TryNormalizeCssColor(CustomTextColor, out var custom) => custom,
        ReaderTheme.Custom => "#000",
        ReaderTheme.System when UsesSystemLightSepia(themeMode) => "#332A1B",
        _ => IsDark(themeMode) ? "#fff" : "#000",
    };

    public string InfoColorCss(ThemeMode themeMode) => EffectiveTheme switch
    {
        ReaderTheme.Light => "#666666",
        ReaderTheme.Dark => "#A6A6A6",
        ReaderTheme.Sepia => UsesInvertedSepia(themeMode) ? "#F2E2C9" : "#74664F",
        ReaderTheme.Custom when TryNormalizeCssColor(CustomInfoColor, out var custom) => custom,
        ReaderTheme.Custom => "#999999",
        ReaderTheme.System when UsesSystemLightSepia(themeMode) => "#74664F",
        _ => IsDark(themeMode) ? "#A6A6A6" : "#666666",
    };

    public uint InfoColor(ThemeMode themeMode) => EffectiveTheme switch
    {
        ReaderTheme.Light => 0xFF666666,
        ReaderTheme.Dark => 0xFFA6A6A6,
        ReaderTheme.Sepia => UsesInvertedSepia(themeMode) ? 0xFFF2E2C9 : 0xFF74664F,
        ReaderTheme.Custom when TryParseColor(CustomInfoColor, out var custom) => custom,
        ReaderTheme.Custom => 0xFF999999,
        ReaderTheme.System when UsesSystemLightSepia(themeMode) => 0xFF74664F,
        _ => IsDark(themeMode) ? 0xFFA6A6A6 : 0xFF666666,
    };

    public bool UsesDarkInterface(ThemeMode themeMode) => EffectiveTheme switch
    {
        ReaderTheme.Light => false,
        ReaderTheme.Dark => true,
        ReaderTheme.Sepia => UsesInvertedSepia(themeMode),
        _ => IsDark(themeMode),
    };

    public bool UsesSepiaLightContent(ThemeMode themeMode) =>
        EffectiveTheme == ReaderTheme.Sepia && !UsesInvertedSepia(themeMode)
        || UsesSystemLightSepia(themeMode);

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
