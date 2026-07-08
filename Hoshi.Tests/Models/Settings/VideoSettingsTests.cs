using FluentAssertions;
using Hoshi.Models.Settings;

namespace Hoshi.Tests.Models.Settings;

public sealed class VideoSettingsTests
{
    [Fact]
    public void Defaults_AreAlignedWithNiratanVideoSettings()
    {
        var settings = new VideoSettings();

        settings.AutoPlayNextEpisode.Should().BeTrue();
        settings.RememberPlaybackState.Should().BeTrue();
        settings.SeekIntervalSeconds.Should().Be(5);
        settings.MiningHistoryLimit.Should().Be(25);
        settings.HardwareDecodingEnabled.Should().BeTrue();
        settings.DeinterlacingEnabled.Should().BeFalse();
        settings.HdrEnhancementEnabled.Should().BeFalse();
        settings.VideoBrightness.Should().Be(0);
        settings.VideoContrast.Should().Be(0);
        settings.VideoSaturation.Should().Be(0);
        settings.VideoGamma.Should().Be(0);
        settings.VideoHue.Should().Be(0);
        settings.SubtitleFontFamily.Should().Be("");
        settings.SubtitleFontSize.Should().Be(36);
        settings.SubtitleFontWeight.Should().Be(700);
        settings.SubtitleShadowRadius.Should().Be(3);
        settings.SubtitleBackgroundOpacity.Should().Be(0);
        settings.SubtitleBackgroundDisabled.Should().BeTrue();
        settings.SubtitleVerticalPosition.Should().Be(0);
        settings.SubtitleColorHex.Should().Be("#FFFFFFFF");
        settings.SubtitleLookupHighlightColorHex.Should().Be("#3EB5C1CB");
        settings.SubtitleLookupHighlightTextColorHex.Should().Be("#FFFFFFFF");
        settings.SubtitleMaskEnabled.Should().BeFalse();
        settings.SubtitleMaskMode.Should().Be(VideoSubtitleMaskMode.Blur);
        settings.SubtitleMaskBlurRadius.Should().Be(10);
        settings.SubtitleMaskHiddenOpacity.Should().Be(0);

        typeof(VideoSettings)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(name =>
                name.Contains("ControlBar", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Values_AreClampedToNiratanRanges()
    {
        var settings = new VideoSettings
        {
            SeekIntervalSeconds = 999,
            MiningHistoryLimit = -10,
            VideoBrightness = double.NaN,
            VideoContrast = 250,
            VideoSaturation = -250,
            VideoGamma = 101,
            VideoHue = -101,
            SubtitleFontSize = 1,
            SubtitleFontWeight = 9999,
            SubtitleShadowRadius = 99,
            SubtitleBackgroundOpacity = 2,
            SubtitleVerticalPosition = -999,
            SubtitleColorHex = "ffffff",
            SubtitleLookupHighlightColorHex = "not-a-color",
            SubtitleLookupHighlightTextColorHex = "#11223344",
            SubtitleMaskBlurRadius = 99,
            SubtitleMaskHiddenOpacity = -1,
        };

        settings.SeekIntervalSeconds.Should().Be(60);
        settings.MiningHistoryLimit.Should().Be(0);
        settings.VideoBrightness.Should().Be(0);
        settings.VideoContrast.Should().Be(100);
        settings.VideoSaturation.Should().Be(-100);
        settings.VideoGamma.Should().Be(100);
        settings.VideoHue.Should().Be(-100);
        settings.SubtitleFontSize.Should().Be(12);
        settings.SubtitleFontWeight.Should().Be(900);
        settings.SubtitleShadowRadius.Should().Be(10);
        settings.SubtitleBackgroundOpacity.Should().Be(1);
        settings.SubtitleVerticalPosition.Should().Be(-200);
        settings.SubtitleColorHex.Should().Be("#FFFFFFFF");
        settings.SubtitleLookupHighlightColorHex.Should().Be("#3EB5C1CB");
        settings.SubtitleLookupHighlightTextColorHex.Should().Be("#11223344");
        settings.SubtitleMaskBlurRadius.Should().Be(20);
        settings.SubtitleMaskHiddenOpacity.Should().Be(0);
    }
}
