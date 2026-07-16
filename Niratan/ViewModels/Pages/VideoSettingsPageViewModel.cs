using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Settings;
using Niratan.Services.Settings;

namespace Niratan.ViewModels.Pages;

public sealed record VideoSubtitleMaskModeOption(VideoSubtitleMaskMode Mode, string Label);

public partial class VideoSettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private bool _isInitializing = true;

    public IReadOnlyList<JapaneseFontOption> AvailableSubtitleFonts { get; } = JapaneseFontCatalog.Fonts;

    public IReadOnlyList<VideoSubtitleMaskModeOption> AvailableSubtitleMaskModes { get; } =
    [
        new(
            VideoSubtitleMaskMode.Blur,
            ResourceStringHelper.GetString("VideoSubtitleMaskModeBlur", "Blur")
        ),
        new(
            VideoSubtitleMaskMode.Transparent,
            ResourceStringHelper.GetString("VideoSubtitleMaskModeTransparent", "Transparent")
        ),
    ];

    [ObservableProperty]
    public partial bool AutoPlayNextEpisode { get; set; }

    [ObservableProperty]
    public partial bool RememberPlaybackState { get; set; }

    [ObservableProperty]
    public partial int SeekIntervalSeconds { get; set; } = 5;

    public double SeekIntervalSecondsValue
    {
        get => SeekIntervalSeconds;
        set => SeekIntervalSeconds = RoundInt(value, SeekIntervalSeconds);
    }

    [ObservableProperty]
    public partial int MiningHistoryLimit { get; set; } = 25;

    public double MiningHistoryLimitValue
    {
        get => MiningHistoryLimit;
        set => MiningHistoryLimit = RoundInt(value, MiningHistoryLimit);
    }

    [ObservableProperty]
    public partial bool HardwareDecodingEnabled { get; set; }

    [ObservableProperty]
    public partial bool DeinterlacingEnabled { get; set; }

    [ObservableProperty]
    public partial bool HdrEnhancementEnabled { get; set; }

    [ObservableProperty]
    public partial double VideoBrightness { get; set; }

    [ObservableProperty]
    public partial string VideoBrightnessText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoContrast { get; set; }

    [ObservableProperty]
    public partial string VideoContrastText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoSaturation { get; set; }

    [ObservableProperty]
    public partial string VideoSaturationText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoGamma { get; set; }

    [ObservableProperty]
    public partial string VideoGammaText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoHue { get; set; }

    [ObservableProperty]
    public partial string VideoHueText { get; set; } = "0";

    [ObservableProperty]
    public partial string SubtitleFontFamily { get; set; } = "";

    [ObservableProperty]
    public partial double SubtitleFontSize { get; set; } = 36;

    public double SubtitleFontSizeValue
    {
        get => SubtitleFontSize;
        set => SubtitleFontSize = value;
    }

    [ObservableProperty]
    public partial string SubtitleFontSizeText { get; set; } = "36 px";

    [ObservableProperty]
    public partial int SubtitleFontWeight { get; set; } = 700;

    public double SubtitleFontWeightValue
    {
        get => SubtitleFontWeight;
        set => SubtitleFontWeight = RoundInt(value, SubtitleFontWeight);
    }

    [ObservableProperty]
    public partial double SubtitleShadowRadius { get; set; } = 3;

    [ObservableProperty]
    public partial string SubtitleShadowRadiusText { get; set; } = "3.0";

    [ObservableProperty]
    public partial double SubtitleBackgroundOpacity { get; set; }

    [ObservableProperty]
    public partial string SubtitleBackgroundOpacityText { get; set; } = "0%";

    [ObservableProperty]
    public partial bool SubtitleBackgroundDisabled { get; set; } = true;

    [ObservableProperty]
    public partial double SubtitleVerticalPosition { get; set; }

    [ObservableProperty]
    public partial string SubtitleColorHex { get; set; } = "#FFFFFFFF";

    [ObservableProperty]
    public partial string SubtitleLookupHighlightColorHex { get; set; } = "#3EB5C1CB";

    [ObservableProperty]
    public partial string SubtitleLookupHighlightTextColorHex { get; set; } = "#FFFFFFFF";

    [ObservableProperty]
    public partial bool SubtitleMaskEnabled { get; set; }

    [ObservableProperty]
    public partial VideoSubtitleMaskMode SelectedSubtitleMaskMode { get; set; } = VideoSubtitleMaskMode.Blur;

    [ObservableProperty]
    public partial double SubtitleMaskBlurRadius { get; set; } = 10;

    [ObservableProperty]
    public partial string SubtitleMaskBlurRadiusText { get; set; } = "10 px";

    [ObservableProperty]
    public partial double SubtitleMaskHiddenOpacity { get; set; }

    [ObservableProperty]
    public partial string SubtitleMaskHiddenOpacityText { get; set; } = "0%";

    public VideoSettingsPageViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current.VideoSettings ?? new VideoSettings();

        AutoPlayNextEpisode = settings.AutoPlayNextEpisode;
        RememberPlaybackState = settings.RememberPlaybackState;
        SeekIntervalSeconds = settings.SeekIntervalSeconds;
        MiningHistoryLimit = settings.MiningHistoryLimit;
        HardwareDecodingEnabled = settings.HardwareDecodingEnabled;
        DeinterlacingEnabled = settings.DeinterlacingEnabled;
        HdrEnhancementEnabled = settings.HdrEnhancementEnabled;
        VideoBrightness = settings.VideoBrightness;
        VideoContrast = settings.VideoContrast;
        VideoSaturation = settings.VideoSaturation;
        VideoGamma = settings.VideoGamma;
        VideoHue = settings.VideoHue;
        SubtitleFontFamily = settings.SubtitleFontFamily;
        SubtitleFontSize = settings.SubtitleFontSize;
        SubtitleFontWeight = settings.SubtitleFontWeight;
        SubtitleShadowRadius = settings.SubtitleShadowRadius;
        SubtitleBackgroundOpacity = settings.SubtitleBackgroundOpacity;
        SubtitleBackgroundDisabled = settings.SubtitleBackgroundDisabled;
        SubtitleVerticalPosition = settings.SubtitleVerticalPositionFraction;
        SubtitleColorHex = settings.SubtitleColorHex;
        SubtitleLookupHighlightColorHex = settings.SubtitleLookupHighlightColorHex;
        SubtitleLookupHighlightTextColorHex = settings.SubtitleLookupHighlightTextColorHex;
        SubtitleMaskEnabled = settings.SubtitleMaskEnabled;
        SelectedSubtitleMaskMode = settings.SubtitleMaskMode;
        SubtitleMaskBlurRadius = settings.SubtitleMaskBlurRadius;
        SubtitleMaskHiddenOpacity = settings.SubtitleMaskHiddenOpacity;
    }

    private void SaveSettings()
    {
        if (_isInitializing)
            return;

        _settingsService.Set(
            settings => settings.VideoSettings,
            new VideoSettings
            {
                AutoPlayNextEpisode = AutoPlayNextEpisode,
                RememberPlaybackState = RememberPlaybackState,
                SeekIntervalSeconds = SeekIntervalSeconds,
                MiningHistoryLimit = MiningHistoryLimit,
                HardwareDecodingEnabled = HardwareDecodingEnabled,
                DeinterlacingEnabled = DeinterlacingEnabled,
                HdrEnhancementEnabled = HdrEnhancementEnabled,
                VideoShaderPreset = VideoShaderPreset.Off,
                VideoBrightness = VideoBrightness,
                VideoContrast = VideoContrast,
                VideoSaturation = VideoSaturation,
                VideoGamma = VideoGamma,
                VideoHue = VideoHue,
                SubtitleFontFamily = SubtitleFontFamily,
                SubtitleFontSize = SubtitleFontSize,
                SubtitleFontWeight = SubtitleFontWeight,
                SubtitleShadowRadius = SubtitleShadowRadius,
                SubtitleBackgroundOpacity = SubtitleBackgroundOpacity,
                SubtitleBackgroundDisabled = SubtitleBackgroundDisabled,
                SubtitleVerticalPositionFraction = SubtitleVerticalPosition,
                SubtitleColorHex = SubtitleColorHex,
                SubtitleLookupHighlightColorHex = SubtitleLookupHighlightColorHex,
                SubtitleLookupHighlightTextColorHex = SubtitleLookupHighlightTextColorHex,
                SubtitleMaskEnabled = SubtitleMaskEnabled,
                SubtitleMaskMode = SelectedSubtitleMaskMode,
                SubtitleMaskBlurRadius = SubtitleMaskBlurRadius,
                SubtitleMaskHiddenOpacity = SubtitleMaskHiddenOpacity,
            });
        _ = _settingsService.SaveAsync();
    }

    partial void OnAutoPlayNextEpisodeChanged(bool value) => SaveSettings();
    partial void OnRememberPlaybackStateChanged(bool value) => SaveSettings();
    partial void OnSeekIntervalSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(SeekIntervalSecondsValue));
        SaveSettings();
    }

    partial void OnMiningHistoryLimitChanged(int value)
    {
        OnPropertyChanged(nameof(MiningHistoryLimitValue));
        SaveSettings();
    }

    partial void OnHardwareDecodingEnabledChanged(bool value) => SaveSettings();
    partial void OnDeinterlacingEnabledChanged(bool value) => SaveSettings();
    partial void OnHdrEnhancementEnabledChanged(bool value) => SaveSettings();

    partial void OnVideoBrightnessChanged(double value)
    {
        VideoBrightnessText = FormatEqualizer(value);
        SaveSettings();
    }

    partial void OnVideoContrastChanged(double value)
    {
        VideoContrastText = FormatEqualizer(value);
        SaveSettings();
    }

    partial void OnVideoSaturationChanged(double value)
    {
        VideoSaturationText = FormatEqualizer(value);
        SaveSettings();
    }

    partial void OnVideoGammaChanged(double value)
    {
        VideoGammaText = FormatEqualizer(value);
        SaveSettings();
    }

    partial void OnVideoHueChanged(double value)
    {
        VideoHueText = FormatEqualizer(value);
        SaveSettings();
    }

    partial void OnSubtitleFontFamilyChanged(string value) => SaveSettings();
    partial void OnSubtitleFontSizeChanged(double value)
    {
        SubtitleFontSizeText = $"{Math.Clamp(value, 12, 72):0} px";
        OnPropertyChanged(nameof(SubtitleFontSizeValue));
        SaveSettings();
    }

    partial void OnSubtitleFontWeightChanged(int value)
    {
        OnPropertyChanged(nameof(SubtitleFontWeightValue));
        SaveSettings();
    }

    partial void OnSubtitleShadowRadiusChanged(double value)
    {
        SubtitleShadowRadiusText = $"{Math.Clamp(value, 0, 10):0.0}";
        SaveSettings();
    }

    partial void OnSubtitleBackgroundOpacityChanged(double value)
    {
        SubtitleBackgroundOpacityText = $"{Math.Clamp(value, 0, 1) * 100:0}%";
        SaveSettings();
    }

    partial void OnSubtitleBackgroundDisabledChanged(bool value) => SaveSettings();

    partial void OnSubtitleVerticalPositionChanged(double value)
    {
        var normalized = VideoSubtitlePositionPolicy.Normalize(value);
        if (value != normalized)
        {
            SubtitleVerticalPosition = normalized;
            return;
        }

        SaveSettings();
    }

    partial void OnSubtitleColorHexChanged(string value) => SaveSettings();
    partial void OnSubtitleLookupHighlightColorHexChanged(string value) => SaveSettings();
    partial void OnSubtitleLookupHighlightTextColorHexChanged(string value) => SaveSettings();
    partial void OnSubtitleMaskEnabledChanged(bool value) => SaveSettings();
    partial void OnSelectedSubtitleMaskModeChanged(VideoSubtitleMaskMode value) => SaveSettings();
    partial void OnSubtitleMaskBlurRadiusChanged(double value)
    {
        SubtitleMaskBlurRadiusText = $"{Math.Clamp(value, 0, 20):0} px";
        SaveSettings();
    }

    partial void OnSubtitleMaskHiddenOpacityChanged(double value)
    {
        SubtitleMaskHiddenOpacityText = $"{Math.Clamp(value, 0, 1) * 100:0}%";
        SaveSettings();
    }

    public void OnNavigatedFrom() => SaveSettings();

    private static int RoundInt(double value, int fallback) =>
        double.IsFinite(value)
            ? (int)Math.Round(value)
            : fallback;

    private static string FormatEqualizer(double value) =>
        $"{Math.Clamp(double.IsFinite(value) ? Math.Round(value) : 0, -100, 100):0}";
}
