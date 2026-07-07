using System;
using System.Collections.Generic;

namespace Hoshi.Services.Video;

public readonly record struct VideoSubtitleShadowOffset(double X, double Y, double Opacity);

public static class VideoSubtitleShadowLayout
{
    public const int LayerCount = 8;

    public static IReadOnlyList<VideoSubtitleShadowOffset> CreateOffsets(double shadowThickness, double contentOpacity)
    {
        var opacity = Math.Clamp(contentOpacity, 0, 1) * 0.9;
        var radius = Math.Clamp(shadowThickness, 0, 10);
        if (radius <= 0 || opacity <= 0)
            return CreateHiddenOffsets();

        var spread = Math.Round(radius * 2 / 3, 2);
        if (spread <= 0)
            spread = radius;

        return
        [
            new(0, -spread, opacity),
            new(spread, 0, opacity),
            new(0, spread, opacity),
            new(-spread, 0, opacity),
            new(-spread, -spread, opacity),
            new(spread, -spread, opacity),
            new(spread, spread, opacity),
            new(-spread, spread, opacity),
        ];
    }

    private static IReadOnlyList<VideoSubtitleShadowOffset> CreateHiddenOffsets() =>
    [
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
        new(0, 0, 0),
    ];
}
