using System;

namespace Niratan.Models.Settings;

public enum GalGameOverlayHorizontalAlignment
{
    Center,
    Left,
}

public enum GalGameOverlayVerticalAlignment
{
    Center,
    Top,
}

public sealed class GalGameSettings
{
    public GalGameOverlayAppearanceSettings OverlayAppearance { get; set; } = new();
}

public sealed class GalGameOverlayAppearanceSettings
{
    public const double FontSizeMin = 12;
    public const double FontSizeMax = 72;
    public const double LetterSpacingMin = -2;
    public const double LetterSpacingMax = 12;
    public const double LineHeightMin = 0.8;
    public const double LineHeightMax = 2;
    public const double OutlineWidthMin = 0;
    public const double OutlineWidthMax = 6;
    public const double PaddingMin = 0;
    public const double PaddingMax = 80;
    public const double CornerRadiusMin = 0;
    public const double CornerRadiusMax = 40;

    public string FontFamily { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 30;
    public double LetterSpacing { get; set; }
    public double LineHeight { get; set; } = 1;
    public bool Bold { get; set; } = true;
    public GalGameOverlayHorizontalAlignment HorizontalAlignment { get; set; } =
        GalGameOverlayHorizontalAlignment.Center;
    public GalGameOverlayVerticalAlignment VerticalAlignment { get; set; } =
        GalGameOverlayVerticalAlignment.Center;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string BackgroundColor { get; set; } = "#FF000000";
    public double BackgroundOpacity { get; set; }
    public string OutlineColor { get; set; } = "#E0000000";
    public double OutlineWidth { get; set; } = 1.6;
    public double Padding { get; set; } = 20;
    public double CornerRadius { get; set; } = 14;

    public GalGameOverlayAppearanceSettings Normalize() => new()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Yu Gothic UI" : FontFamily.Trim(),
        FontSize = ClampFinite(FontSize, FontSizeMin, FontSizeMax, 30),
        LetterSpacing = ClampFinite(LetterSpacing, LetterSpacingMin, LetterSpacingMax, 0),
        LineHeight = ClampFinite(LineHeight, LineHeightMin, LineHeightMax, 1),
        Bold = Bold,
        HorizontalAlignment = Enum.IsDefined(HorizontalAlignment)
            ? HorizontalAlignment
            : GalGameOverlayHorizontalAlignment.Center,
        VerticalAlignment = Enum.IsDefined(VerticalAlignment)
            ? VerticalAlignment
            : GalGameOverlayVerticalAlignment.Center,
        TextColor = NormalizeColor(TextColor, "#FFFFFFFF"),
        BackgroundColor = NormalizeColor(BackgroundColor, "#FF000000"),
        BackgroundOpacity = ClampFinite(BackgroundOpacity, 0, 1, 0),
        OutlineColor = NormalizeColor(OutlineColor, "#E0000000"),
        OutlineWidth = ClampFinite(OutlineWidth, OutlineWidthMin, OutlineWidthMax, 1.6),
        Padding = ClampFinite(Padding, PaddingMin, PaddingMax, 20),
        CornerRadius = ClampFinite(CornerRadius, CornerRadiusMin, CornerRadiusMax, 14),
    };

    public GalGameOverlayAppearanceSettings Clone() => new()
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
        LetterSpacing = LetterSpacing,
        LineHeight = LineHeight,
        Bold = Bold,
        HorizontalAlignment = HorizontalAlignment,
        VerticalAlignment = VerticalAlignment,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
        BackgroundOpacity = BackgroundOpacity,
        OutlineColor = OutlineColor,
        OutlineWidth = OutlineWidth,
        Padding = Padding,
        CornerRadius = CornerRadius,
    };

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var text = value.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length == 6)
            text = "FF" + text;
        return text.Length == 8 && uint.TryParse(
            text,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out _)
                ? $"#{text.ToUpperInvariant()}"
                : fallback;
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
