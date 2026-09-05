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
                Metadata = new VideoMetadataSettings
                {
                    AniDbClientId = "udp-client",
                    AniDbClientVersion = 2,
                    AniDbHttpClientId = "http-client",
                    AniDbHttpClientVersion = 3,
                    AniDbUdpServerHost = "94.130.237.200",
                    AniDbUdpServerPort = 9001,
                    AniDbUdpBindAddress = "192.168.1.88",
                    AniDbUdpLocalPort = 45501,
                },
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
        viewModel.AniDbClientId.Should().Be("udp-client");
        viewModel.AniDbClientVersion.Should().Be(2);
        viewModel.AniDbHttpClientId.Should().Be("http-client");
        viewModel.AniDbHttpClientVersion.Should().Be(3);
        viewModel.AniDbUdpServerHost.Should().Be("94.130.237.200");
        viewModel.AniDbUdpServerPort.Should().Be(9001);
        viewModel.AniDbUdpBindAddress.Should().Be("192.168.1.88");
        viewModel.AniDbUdpLocalPort.Should().Be(45501);
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
            AniDbClientId = "  udp-client  ",
            AniDbClientVersion = 2,
            AniDbHttpClientId = "  http-client  ",
            AniDbHttpClientVersion = 3,
            AniDbUdpServerHost = "  94.130.237.200  ",
            AniDbUdpServerPort = 70000,
            AniDbUdpBindAddress = "  192.168.1.88  ",
            AniDbUdpLocalPort = 80,
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
        saved!.Metadata.AniDbClientId.Should().Be("udp-client");
        saved.Metadata.AniDbClientVersion.Should().Be(2);
        saved.Metadata.AniDbHttpClientId.Should().Be("http-client");
        saved.Metadata.AniDbHttpClientVersion.Should().Be(3);
        saved.Metadata.AniDbUdpServerHost.Should().Be("94.130.237.200");
        saved.Metadata.AniDbUdpServerPort.Should().Be(65535);
        saved.Metadata.AniDbUdpBindAddress.Should().Be("192.168.1.88");
        saved.Metadata.AniDbUdpLocalPort.Should().Be(1024);
        saved.AutoPlayNextEpisode.Should().BeFalse();
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

    [Fact]
    public void Updating_discovery_preferences_preserves_Nyaa_subscriptions()
    {
        var appSettings = new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                ExploreProviderOrder = ["bangumi", "tmdb"],
                NyaaSubscriptions =
                [
                    new NyaaVideoSubscription
                    {
                        Key = "anilist:123",
                        ProviderId = "anilist",
                        ProviderItemId = "123",
                        Title = "Test Anime",
                        Query = "Test Anime",
                        ReleaseGroup = "Group",
                        Resolution = "1080p",
                    },
                ],
            },
        };
        DiscoverySettings? savedDiscovery = null;
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(appSettings);
        settingsService.Setup(service => service.Set(
                It.IsAny<Expression<Func<AppSettings, DiscoverySettings>>>(),
                It.IsAny<DiscoverySettings>()))
            .Callback<Expression<Func<AppSettings, DiscoverySettings>>, DiscoverySettings>((_, value) =>
            {
                savedDiscovery = value;
                appSettings.DiscoverySettings = value;
            });
        settingsService.Setup(service => service.SaveAsync()).Returns(Task.CompletedTask);
        var viewModel = new VideoSettingsPageViewModel(settingsService.Object);

        viewModel.DiscoveryProviderOrderText.Should().Be("tmdb");
        viewModel.DiscoveryProviderOrderText = "bangumi,anilist,tmdb";
        viewModel.TmdbRecommendationsEnabled = false;

        savedDiscovery.Should().NotBeNull();
        savedDiscovery!.ExploreProviderOrder.Should().Equal("anilist", "tmdb");
        savedDiscovery.NyaaSubscriptions.Should().ContainSingle()
            .Which.Key.Should().Be("anilist:123");
    }

}
