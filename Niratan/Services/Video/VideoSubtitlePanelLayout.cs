using System;
using System.Globalization;
using System.Text;
using Windows.Foundation;

namespace Niratan.Services.Video;

public readonly record struct VideoSubtitlePanelLayoutResult(
    double FontSize,
    double PanelHeight,
    int EstimatedLineCount);

public static class VideoSubtitlePanelLayout
{
    private const double MinimumFontSize = 12;
    private const double MaximumFontSize = 72;
    private const double LineHeightMultiplier = 1.25;
    private const double MinimumLineReserve = 3.2;
    private const double VerticalPadding = 16;
    private const double ConservativeWidthFactor = 0.88;

    public static VideoSubtitlePanelLayoutResult Calculate(
        string? text,
        double requestedFontSize,
        double effectRadius,
        double canvasWidth,
        double viewportHeight)
    {
        if (string.IsNullOrEmpty(text))
            return default;

        var fontSize = Math.Clamp(
            double.IsFinite(requestedFontSize) ? requestedFontSize : 36,
            MinimumFontSize,
            MaximumFontSize);
        var normalizedEffectRadius = Math.Clamp(
            double.IsFinite(effectRadius) ? effectRadius : 0,
            0,
            20);
        var availableHeight = double.IsFinite(viewportHeight)
            ? Math.Max(0, viewportHeight)
            : 0;
        var textWidth = VideoSubtitleCanvasRenderer.CalculateLayoutBounds(
            new Size(Math.Max(1, canvasWidth), 1)).Width;

        var lineCount = 1;
        var requiredHeight = 0d;
        while (true)
        {
            lineCount = EstimateLineCount(text, fontSize, textWidth);
            requiredHeight = CalculateRequiredHeight(
                fontSize,
                lineCount,
                normalizedEffectRadius);
            if (availableHeight <= 0
                || requiredHeight <= availableHeight
                || fontSize <= MinimumFontSize)
            {
                break;
            }

            fontSize = Math.Max(MinimumFontSize, fontSize - 1);
        }

        var panelHeight = availableHeight > 0
            ? Math.Min(requiredHeight, availableHeight)
            : requiredHeight;
        return new VideoSubtitlePanelLayoutResult(fontSize, panelHeight, lineCount);
    }

    internal static int EstimateLineCount(string text, double fontSize, double textWidth)
    {
        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var usableWidth = Math.Max(1, textWidth * ConservativeWidthFactor);
        var lines = 0;

        foreach (var explicitLine in normalizedText.Split('\n'))
        {
            lines++;
            var occupiedWidth = 0d;
            foreach (var rune in explicitLine.EnumerateRunes())
            {
                var advance = EstimateAdvance(rune, fontSize);
                if (occupiedWidth > 0 && occupiedWidth + advance > usableWidth)
                {
                    lines++;
                    occupiedWidth = 0;
                }

                occupiedWidth += advance;
            }
        }

        return Math.Max(1, lines);
    }

    private static double CalculateRequiredHeight(
        double fontSize,
        int lineCount,
        double effectRadius)
    {
        var textHeight = Math.Max(
            fontSize * MinimumLineReserve,
            lineCount * fontSize * LineHeightMultiplier);
        return Math.Max(48, textHeight + (effectRadius * 2) + VerticalPadding);
    }

    private static double EstimateAdvance(Rune rune, double fontSize)
    {
        if (Rune.IsWhiteSpace(rune))
            return fontSize * 0.4;

        if (IsWideRune(rune.Value))
            return fontSize;

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format
            ? 0
            : fontSize * 0.64;
    }

    private static bool IsWideRune(int value) =>
        value is >= 0x1100 and <= 0x115F
        || value is >= 0x2E80 and <= 0xA4CF
        || value is >= 0xAC00 and <= 0xD7A3
        || value is >= 0xF900 and <= 0xFAFF
        || value is >= 0xFE10 and <= 0xFE6F
        || value is >= 0xFF00 and <= 0xFF60
        || value is >= 0xFFE0 and <= 0xFFE6
        || value is >= 0x1F300 and <= 0x1FAFF
        || value is >= 0x20000 and <= 0x3FFFD;
}
