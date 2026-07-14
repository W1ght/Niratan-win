using System;

namespace Niratan.Models.Settings;

internal static class DictionaryPopupAppearanceConstraints
{
    public const int MinWidth = 100;
    public const int MaxWidth = 1400;
    public const int WidthStep = 10;
    public const int DefaultWidth = 320;
    public const int MinHeight = 100;
    public const int MaxHeight = 800;
    public const int HeightStep = 10;
    public const int DefaultHeight = 250;
    public const double MinScale = 0.8;
    public const double MaxScale = 1.5;
    public const double ScaleStep = 0.05;
    public const double DefaultScale = 1.0;

    public static int NormalizeWidth(int value) => Math.Clamp(value, MinWidth, MaxWidth);

    public static int NormalizeHeight(int value) => Math.Clamp(value, MinHeight, MaxHeight);

    public static double NormalizeScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : DefaultScale;
}
