using System;
using System.Linq;

namespace Niratan.Models.Settings;

public enum VideoSubtitleMaskMode
{
    Blur,
    Transparent,
}

public sealed class VideoSettings
{
    private int _seekIntervalSeconds = 5;
    private int _miningHistoryLimit = 25;
    private double _videoBrightness;
    private double _videoContrast;
    private double _videoSaturation;
    private double _videoGamma;
    private double _videoHue;
    private string _subtitleFontFamily = JapaneseFontCatalog.DefaultSubtitleFontFamily;
    private double _subtitleFontSize = 52;
    private int _subtitleFontWeight = 700;
    private double _subtitleShadowRadius = 10;
    private double _subtitleBackgroundOpacity;
    private double _subtitleVerticalPosition;
    private double? _subtitleVerticalPositionFraction;
    private string _subtitleColorHex = "#FFFFFFFF";
    private string _subtitleLookupHighlightColorHex = "#3EB5C1CB";
    private string _subtitleLookupHighlightTextColorHex = "#FFFFFFFF";
    private double _subtitleMaskBlurRadius = 10;
    private double _subtitleMaskHiddenOpacity;

    public bool AutoPlayNextEpisode { get; set; } = true;
    public bool RememberPlaybackState { get; set; } = true;

    public int SeekIntervalSeconds
    {
        get => _seekIntervalSeconds;
        set => _seekIntervalSeconds = Clamp(value, 1, 60);
    }

    public int MiningHistoryLimit
    {
        get => _miningHistoryLimit;
        set => _miningHistoryLimit = Math.Max(0, value);
    }

    public bool HardwareDecodingEnabled { get; set; } = true;
    public bool DeinterlacingEnabled { get; set; }
    public bool HdrEnhancementEnabled { get; set; }
    public VideoShaderPreset VideoShaderPreset { get; set; } = VideoShaderPreset.Off;

    public double VideoBrightness
    {
        get => _videoBrightness;
        set => _videoBrightness = ClampEqualizer(value);
    }

    public double VideoContrast
    {
        get => _videoContrast;
        set => _videoContrast = ClampEqualizer(value);
    }

    public double VideoSaturation
    {
        get => _videoSaturation;
        set => _videoSaturation = ClampEqualizer(value);
    }

    public double VideoGamma
    {
        get => _videoGamma;
        set => _videoGamma = ClampEqualizer(value);
    }

    public double VideoHue
    {
        get => _videoHue;
        set => _videoHue = ClampEqualizer(value);
    }

    public string SubtitleFontFamily
    {
        get => _subtitleFontFamily;
        set => _subtitleFontFamily = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    public double SubtitleFontSize
    {
        get => _subtitleFontSize;
        set => _subtitleFontSize = ClampFinite(value, 12, 72, 52);
    }

    public int SubtitleFontWeight
    {
        get => _subtitleFontWeight;
        set => _subtitleFontWeight = Clamp(value, 100, 900);
    }

    public double SubtitleShadowRadius
    {
        get => _subtitleShadowRadius;
        set => _subtitleShadowRadius = ClampRoundedHalf(value, 0, 10, 10);
    }

    public double SubtitleBackgroundOpacity
    {
        get => _subtitleBackgroundOpacity;
        set => _subtitleBackgroundOpacity = ClampFinite(value, 0, 1, 0);
    }

    public bool SubtitleBackgroundDisabled { get; set; } = true;

    public double SubtitleVerticalPosition
    {
        get => _subtitleVerticalPosition;
        set => _subtitleVerticalPosition = ClampFinite(value, -200, 200, 0);
    }

    public double SubtitleVerticalPositionFraction
    {
        get => _subtitleVerticalPositionFraction
            ?? VideoSubtitlePositionPolicy.MigrateLegacyPosition(_subtitleVerticalPosition);
        set => _subtitleVerticalPositionFraction = VideoSubtitlePositionPolicy.Normalize(value);
    }

    public string SubtitleColorHex
    {
        get => _subtitleColorHex;
        set => _subtitleColorHex = NormalizeColorHex(value, "#FFFFFFFF");
    }

    public string SubtitleLookupHighlightColorHex
    {
        get => _subtitleLookupHighlightColorHex;
        set => _subtitleLookupHighlightColorHex = NormalizeColorHex(value, "#3EB5C1CB");
    }

    public string SubtitleLookupHighlightTextColorHex
    {
        get => _subtitleLookupHighlightTextColorHex;
        set => _subtitleLookupHighlightTextColorHex = NormalizeColorHex(value, "#FFFFFFFF");
    }

    public bool SubtitleMaskEnabled { get; set; }
    public VideoSubtitleMaskMode SubtitleMaskMode { get; set; } = VideoSubtitleMaskMode.Blur;

    public double SubtitleMaskBlurRadius
    {
        get => _subtitleMaskBlurRadius;
        set => _subtitleMaskBlurRadius = ClampRounded(value, 0, 20, 10);
    }

    public double SubtitleMaskHiddenOpacity
    {
        get => _subtitleMaskHiddenOpacity;
        set => _subtitleMaskHiddenOpacity = ClampFinite(value, 0, 1, 0);
    }

    public VideoSettings Clone() => (VideoSettings)MemberwiseClone();

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double ClampEqualizer(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), -100, 100)
            : 0;

    private static double ClampRounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), min, max)
            : fallback;

    private static double ClampRoundedHalf(double value, double min, double max, double fallback) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, min, max)
            : fallback;

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static string NormalizeColorHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6)
            hex = "FF" + hex;

        if (hex.Length != 8 || hex.Any(character => !Uri.IsHexDigit(character)))
            return fallback;

        return "#" + hex.ToUpperInvariant();
    }
}
