using System;

namespace Niratan.Services.Sasayaki;

internal static class ReaderLyricsAdaptiveFontPolicy
{
    internal const double LineFitHorizontalMargin = 12;
    internal const double VerticalHeightUseRatio = 0.9;
    internal const double VerticalRowHeightRatio = 1.08;
    internal const double VerticalColumnWidthRatio = 1.35;

    public static double FitHorizontal(
        double baseFontSize,
        double measuredTextWidth,
        double availableWidth)
    {
        var safeBaseFontSize = NormalizePositive(baseFontSize);
        var safeMeasuredWidth = NormalizeNonNegative(measuredTextWidth);
        var effectiveAvailableWidth = Math.Max(
            NormalizePositive(availableWidth) - LineFitHorizontalMargin * 2,
            1);
        if (safeMeasuredWidth <= effectiveAvailableWidth)
            return safeBaseFontSize;

        return Math.Clamp(
            safeBaseFontSize * effectiveAvailableWidth / Math.Max(safeMeasuredWidth, 1),
            1,
            safeBaseFontSize);
    }

    public static double FitVertical(
        double baseFontSize,
        int glyphCount,
        double availableHeight,
        double availableColumnWidth)
    {
        var safeBaseFontSize = NormalizePositive(baseFontSize);
        if (glyphCount <= 0)
            return safeBaseFontSize;

        var heightBound = Math.Max(
                NormalizePositive(availableHeight) * VerticalHeightUseRatio,
                1)
            / (glyphCount * VerticalRowHeightRatio);
        var widthBound = NormalizePositive(availableColumnWidth)
            / VerticalColumnWidthRatio;
        return Math.Clamp(
            Math.Min(heightBound, widthBound),
            1,
            safeBaseFontSize);
    }

    private static double NormalizePositive(double value) =>
        double.IsFinite(value) && value > 0 ? value : 1;

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;
}
