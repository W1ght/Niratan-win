using FluentAssertions;
using Moq;
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

    private static T Get<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"property {propertyName} should exist");
        var value = property!.GetValue(instance);
        if (value == null)
            return default!;

        return value.Should().BeAssignableTo<T>().Subject;
    }

    private static VideoPlayerViewModel CreateSut()
    {
        return new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());
    }
}
