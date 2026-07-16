using System.Linq.Expressions;
using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.ViewModels.Pages;
using Moq;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class VideoSettingsPageViewModelTests
{
    [Fact]
    public void LoadsVideoSettingsFromSettingsService()
    {
        var appSettings = new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                AutoPlayNextEpisode = false,
                RememberPlaybackState = false,
                SeekIntervalSeconds = 9,
                MiningHistoryLimit = 42,
                HardwareDecodingEnabled = false,
                DeinterlacingEnabled = true,
                HdrEnhancementEnabled = true,
                VideoBrightness = 10,
                VideoContrast = 20,
                VideoSaturation = 30,
                VideoGamma = 40,
                VideoHue = -10,
                SubtitleFontFamily = "Yu Gothic UI",
                SubtitleFontSize = 44,
                SubtitleFontWeight = 600,
                SubtitleShadowRadius = 4,
                SubtitleBackgroundOpacity = 0.35,
                SubtitleBackgroundDisabled = false,
                SubtitleVerticalPositionFraction = 0.24,
                SubtitleColorHex = "#FFEEDDCC",
                SubtitleLookupHighlightColorHex = "#88112233",
                SubtitleLookupHighlightTextColorHex = "#FF123456",
                SubtitleMaskEnabled = true,
                SubtitleMaskMode = VideoSubtitleMaskMode.Transparent,
                SubtitleMaskBlurRadius = 12,
                SubtitleMaskHiddenOpacity = 0.25,
            },
        };
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(appSettings);

        var viewModel = new VideoSettingsPageViewModel(settingsService.Object);

        viewModel.AutoPlayNextEpisode.Should().BeFalse();
        viewModel.RememberPlaybackState.Should().BeFalse();
        viewModel.SeekIntervalSeconds.Should().Be(9);
        viewModel.MiningHistoryLimit.Should().Be(42);
        viewModel.HardwareDecodingEnabled.Should().BeFalse();
        viewModel.DeinterlacingEnabled.Should().BeTrue();
        viewModel.HdrEnhancementEnabled.Should().BeTrue();
        viewModel.VideoBrightness.Should().Be(10);
        viewModel.VideoContrast.Should().Be(20);
        viewModel.VideoSaturation.Should().Be(30);
        viewModel.VideoGamma.Should().Be(40);
        viewModel.VideoHue.Should().Be(-10);
        viewModel.SubtitleFontFamily.Should().Be("Yu Gothic UI");
        viewModel.SubtitleFontSize.Should().Be(44);
        viewModel.SubtitleFontWeight.Should().Be(600);
        viewModel.SubtitleShadowRadius.Should().Be(4);
        viewModel.SubtitleBackgroundOpacity.Should().Be(0.35);
        viewModel.SubtitleBackgroundDisabled.Should().BeFalse();
        viewModel.SubtitleVerticalPosition.Should().Be(0.24);
        viewModel.SubtitleColorHex.Should().Be("#FFEEDDCC");
        viewModel.SubtitleLookupHighlightColorHex.Should().Be("#88112233");
        viewModel.SubtitleLookupHighlightTextColorHex.Should().Be("#FF123456");
        viewModel.SubtitleMaskEnabled.Should().BeTrue();
        viewModel.SelectedSubtitleMaskMode.Should().Be(VideoSubtitleMaskMode.Transparent);
        viewModel.SubtitleMaskBlurRadius.Should().Be(12);
        viewModel.SubtitleMaskHiddenOpacity.Should().Be(0.25);
    }

    [Fact]
    public void UpdatingSettings_SavesClampedVideoSettingsWithoutLayoutSwitch()
    {
        var appSettings = new AppSettings();
        VideoSettings? saved = null;
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(appSettings);
        settingsService
            .Setup(service => service.Set(
                It.IsAny<Expression<Func<AppSettings, VideoSettings>>>(),
                It.IsAny<VideoSettings>()))
            .Callback<Expression<Func<AppSettings, VideoSettings>>, VideoSettings>(
                (_, value) =>
                {
                    saved = value;
                    appSettings.VideoSettings = value;
                });
        settingsService.Setup(service => service.SaveAsync()).Returns(Task.CompletedTask);

        var viewModel = new VideoSettingsPageViewModel(settingsService.Object)
        {
            AutoPlayNextEpisode = false,
            RememberPlaybackState = false,
            SeekIntervalSecondsValue = 99,
            MiningHistoryLimitValue = -5,
            HardwareDecodingEnabled = false,
            DeinterlacingEnabled = true,
            HdrEnhancementEnabled = true,
            VideoBrightness = 101,
            VideoContrast = -101,
            VideoSaturation = 12,
            VideoGamma = 13,
            VideoHue = 14,
            SubtitleFontFamily = "  Meiryo  ",
            SubtitleFontSizeValue = 999,
            SubtitleFontWeightValue = 9999,
            SubtitleShadowRadius = 99,
            SubtitleBackgroundOpacity = 2,
            SubtitleBackgroundDisabled = false,
            SubtitleVerticalPosition = -999,
            SubtitleColorHex = "ffffff",
            SubtitleLookupHighlightColorHex = "#01020304",
            SubtitleLookupHighlightTextColorHex = "bad",
            SubtitleMaskEnabled = true,
            SelectedSubtitleMaskMode = VideoSubtitleMaskMode.Transparent,
            SubtitleMaskBlurRadius = 99,
            SubtitleMaskHiddenOpacity = -1,
        };

        saved.Should().NotBeNull();
        saved!.AutoPlayNextEpisode.Should().BeFalse();
        saved.RememberPlaybackState.Should().BeFalse();
        saved.SeekIntervalSeconds.Should().Be(60);
        saved.MiningHistoryLimit.Should().Be(0);
        saved.HardwareDecodingEnabled.Should().BeFalse();
        saved.DeinterlacingEnabled.Should().BeTrue();
        saved.HdrEnhancementEnabled.Should().BeTrue();
        saved.VideoBrightness.Should().Be(100);
        saved.VideoContrast.Should().Be(-100);
        saved.VideoSaturation.Should().Be(12);
        saved.VideoGamma.Should().Be(13);
        saved.VideoHue.Should().Be(14);
        saved.SubtitleFontFamily.Should().Be("Meiryo");
        saved.SubtitleFontSize.Should().Be(72);
        saved.SubtitleFontWeight.Should().Be(900);
        saved.SubtitleShadowRadius.Should().Be(10);
        saved.SubtitleBackgroundOpacity.Should().Be(1);
        saved.SubtitleBackgroundDisabled.Should().BeFalse();
        saved.SubtitleVerticalPositionFraction.Should().Be(0);
        saved.SubtitleColorHex.Should().Be("#FFFFFFFF");
        saved.SubtitleLookupHighlightColorHex.Should().Be("#01020304");
        saved.SubtitleLookupHighlightTextColorHex.Should().Be("#FFFFFFFF");
        saved.SubtitleMaskEnabled.Should().BeTrue();
        saved.SubtitleMaskMode.Should().Be(VideoSubtitleMaskMode.Transparent);
        saved.SubtitleMaskBlurRadius.Should().Be(20);
        saved.SubtitleMaskHiddenOpacity.Should().Be(0);
        typeof(VideoSettings)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(name => name.Contains("ControlBar", StringComparison.OrdinalIgnoreCase));
        settingsService.Verify(service => service.SaveAsync(), Times.AtLeastOnce);
    }

}
