using System;

namespace Hoshi.Services.Video;

public readonly record struct VideoSubtitleShadowStyle(
    float BlurRadius,
    float OffsetX,
    float OffsetY,
    float Opacity);

public static class VideoSubtitleShadowLayout
{
    public static VideoSubtitleShadowStyle Create(double shadowRadius, double contentOpacity)
    {
        var blurRadius = (float)Math.Clamp(shadowRadius, 0, 10);
        var opacity = (float)(Math.Clamp(contentOpacity, 0, 1) * 0.9);
        return blurRadius <= 0 || opacity <= 0
            ? default
            : new VideoSubtitleShadowStyle(blurRadius, 0, 1, opacity);
    }
}
