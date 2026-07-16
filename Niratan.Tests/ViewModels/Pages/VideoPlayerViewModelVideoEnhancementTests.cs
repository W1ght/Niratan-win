using FluentAssertions;
using Moq;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelVideoEnhancementTests
{
    [Fact]
    public void VideoEnhancementState_FollowsNiratanHdrAndEqualizerFlow()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var setHdr = type.GetMethod("SetHDREnhancementEnabled", [typeof(bool)]);
        var setEqualizer = type.GetMethod("SetVideoEqualizer", [typeof(string), typeof(double)]);
        var resetEqualizer = type.GetMethod("ResetVideoEqualizer", [typeof(string)]);

        setHdr.Should().NotBeNull();
        setEqualizer.Should().NotBeNull();
        resetEqualizer.Should().NotBeNull();

        Get<bool>(sut, "HdrEnhancementEnabled").Should().BeFalse();
        Get<double>(sut, "VideoBrightness").Should().Be(0);
        Get<string>(sut, "VideoBrightnessText").Should().Be("0");
        Get<double>(sut, "VideoContrast").Should().Be(0);
        Get<double>(sut, "VideoSaturation").Should().Be(0);
        Get<double>(sut, "VideoGamma").Should().Be(0);
        Get<double>(sut, "VideoHue").Should().Be(0);

        setHdr!.Invoke(sut, [true]);
        Get<bool>(sut, "HdrEnhancementEnabled").Should().BeTrue();

        setEqualizer!.Invoke(sut, ["brightness", 42.6]);
        Get<double>(sut, "VideoBrightness").Should().Be(43);
        Get<string>(sut, "VideoBrightnessText").Should().Be("43");

        setEqualizer.Invoke(sut, ["contrast", 200d]);
        Get<double>(sut, "VideoContrast").Should().Be(100);
        Get<string>(sut, "VideoContrastText").Should().Be("100");

        setEqualizer.Invoke(sut, ["saturation", -200d]);
        Get<double>(sut, "VideoSaturation").Should().Be(-100);
        Get<string>(sut, "VideoSaturationText").Should().Be("-100");

        setEqualizer.Invoke(sut, ["gamma", double.NaN]);
        Get<double>(sut, "VideoGamma").Should().Be(0);
        Get<string>(sut, "VideoGammaText").Should().Be("0");

        setEqualizer.Invoke(sut, ["hue", -12.2]);
        Get<double>(sut, "VideoHue").Should().Be(-12);
        Get<string>(sut, "VideoHueText").Should().Be("-12");

        resetEqualizer!.Invoke(sut, ["brightness"]);
        Get<double>(sut, "VideoBrightness").Should().Be(0);
        Get<string>(sut, "VideoBrightnessText").Should().Be("0");
    }

    [Fact]
    public void ApplySettings_AlwaysStartsAnime4KOff()
    {
        var sut = CreateSut();

        sut.ApplySettings(new VideoSettings
        {
            VideoShaderPreset = VideoShaderPreset.Anime4KHighQuality,
        });

        sut.VideoShaderPreset.Should().Be(VideoShaderPreset.Off);
        sut.SelectedVideoShaderPresetOption!.Preset.Should().Be(VideoShaderPreset.Off);
    }

    [Fact]
    public async Task PrepareShaderPreset_EnablesOnlyCurrentSessionAfterVerifiedDownload()
    {
        var shaderService = new Mock<IAnime4KShaderService>();
        shaderService
            .Setup(service => service.EnsurePresetAvailableAsync(
                VideoShaderPreset.Anime4KFast,
                It.IsAny<IProgress<Anime4KDownloadProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Anime4KInstallResult(true, 6, 6));
        var sut = CreateSut(shaderService.Object);
        sut.SelectedVideoShaderPresetOption = sut.AvailableVideoShaderPresets
            .Single(option => option.Preset == VideoShaderPreset.Anime4KFast);

        var prepared = await sut.PrepareSelectedVideoShaderPresetAsync(
            TestContext.Current.CancellationToken);

        prepared.Should().BeTrue();
        sut.VideoShaderPreset.Should().Be(VideoShaderPreset.Anime4KFast);
        sut.VideoShaderDownloadStatus.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FailedShaderDownload_LeavesCurrentSessionOff()
    {
        var shaderService = new Mock<IAnime4KShaderService>();
        shaderService
            .Setup(service => service.EnsurePresetAvailableAsync(
                VideoShaderPreset.Anime4KHighQuality,
                It.IsAny<IProgress<Anime4KDownloadProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Anime4KInstallResult(false, 0, 6, "hash mismatch"));
        var sut = CreateSut(shaderService.Object);
        sut.SelectedVideoShaderPresetOption = sut.AvailableVideoShaderPresets
            .Single(option => option.Preset == VideoShaderPreset.Anime4KHighQuality);

        var prepared = await sut.PrepareSelectedVideoShaderPresetAsync(
            TestContext.Current.CancellationToken);

        prepared.Should().BeFalse();
        sut.VideoShaderPreset.Should().Be(VideoShaderPreset.Off);
        sut.VideoShaderDownloadStatus.Should().Contain("hash mismatch");
    }

    [Fact]
    public void SelectInstalledShaderPreset_ActivatesImmediatelyWithoutDownload()
    {
        var shaderService = new Mock<IAnime4KShaderService>();
        shaderService
            .Setup(service => service.GetInstalledShaderPaths(VideoShaderPreset.Anime4KFast))
            .Returns([@"C:\Shaders\fast.glsl"]);
        var sut = CreateSut(shaderService.Object);
        sut.SelectedVideoShaderPresetOption = sut.AvailableVideoShaderPresets
            .Single(option => option.Preset == VideoShaderPreset.Anime4KFast);

        var activated = sut.TryActivateSelectedVideoShaderPreset();

        activated.Should().BeTrue();
        sut.VideoShaderPreset.Should().Be(VideoShaderPreset.Anime4KFast);
        sut.IsVideoShaderDownloadRequired.Should().BeFalse();
        shaderService.Verify(service => service.EnsurePresetAvailableAsync(
            It.IsAny<VideoShaderPreset>(),
            It.IsAny<IProgress<Anime4KDownloadProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SelectMissingShaderPreset_RequiresDownloadAndKeepsShaderOff()
    {
        var shaderService = new Mock<IAnime4KShaderService>();
        shaderService
            .Setup(service => service.GetInstalledShaderPaths(VideoShaderPreset.Anime4KHighQuality))
            .Returns([]);
        var sut = CreateSut(shaderService.Object);
        sut.SelectedVideoShaderPresetOption = sut.AvailableVideoShaderPresets
            .Single(option => option.Preset == VideoShaderPreset.Anime4KHighQuality);

        var activated = sut.TryActivateSelectedVideoShaderPreset();

        activated.Should().BeFalse();
        sut.VideoShaderPreset.Should().Be(VideoShaderPreset.Off);
        sut.IsVideoShaderDownloadRequired.Should().BeTrue();
        sut.CanDownloadVideoShaderPreset.Should().BeTrue();
    }

    private static T Get<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"property {propertyName} should exist");
        var value = property!.GetValue(instance);
        if (value == null)
            return default!;

        return value.Should().BeAssignableTo<T>().Subject;
    }

    private static VideoPlayerViewModel CreateSut(IAnime4KShaderService? shaderService = null)
    {
        return new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>(),
            null,
            shaderService);
    }
}
