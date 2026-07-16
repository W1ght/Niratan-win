using System;

namespace Niratan.Models.Settings;

public static class VideoSubtitlePositionPolicy
{
    public const double DefaultPosition = 0.9;
    public const double MinimumPosition = 0;
    public const double MaximumPosition = 1;

    private const double LegacyMaximumMagnitude = 200;

    public static double Normalize(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumPosition, MaximumPosition)
            : DefaultPosition;

    public static double MigrateLegacyPosition(double? value)
    {
        if (value is not { } legacyValue || !double.IsFinite(legacyValue))
            return DefaultPosition;

        if (legacyValue >= 0)
        {
            var progress = Math.Min(legacyValue / LegacyMaximumMagnitude, 1);
            return DefaultPosition * (1 - progress);
        }

        var negativeProgress = Math.Min(-legacyValue / LegacyMaximumMagnitude, 1);
        return DefaultPosition + (1 - DefaultPosition) * negativeProgress;
    }

    public static double OriginY(
        double viewportHeight,
        double subtitleHeight,
        double position)
    {
        if (!double.IsFinite(viewportHeight)
            || !double.IsFinite(subtitleHeight)
            || viewportHeight < 0
            || subtitleHeight < 0)
        {
            return 0;
        }

        var availableTravel = Math.Max(viewportHeight - subtitleHeight, 0);
        return availableTravel * Normalize(position);
    }

    public static double ContainerOriginY(
        double viewportHeight,
        double contentTop,
        double contentHeight,
        double position)
    {
        if (!double.IsFinite(contentTop))
            return 0;

        return OriginY(viewportHeight, contentHeight, position) - contentTop;
    }
}
