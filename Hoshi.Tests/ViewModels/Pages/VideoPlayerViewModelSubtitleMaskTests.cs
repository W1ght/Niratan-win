using FluentAssertions;
using Moq;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class VideoPlayerViewModelSubtitleMaskTests
{
    [Fact]
    public void SubtitleMask_DefaultsMatchNiratanMaskBaseline()
    {
        var sut = CreateSut();

        Get<bool>(sut, "SubtitleMaskEnabled").Should().BeFalse();
        Get<string>(sut, "SubtitleMaskMode").Should().Be("Blur");
        Get<double>(sut, "SubtitleMaskBlurRadius").Should().Be(10);
        Get<string>(sut, "SubtitleMaskBlurRadiusText").Should().Be("10 px");
        Get<double>(sut, "SubtitleMaskHiddenOpacity").Should().Be(0);
        Get<string>(sut, "SubtitleMaskHiddenOpacityText").Should().Be("0%");
    }

    [Fact]
    public void SetSubtitleMaskMode_NormalizesModeAndValues()
    {
        var sut = CreateSut();
        var setMode = sut.GetType().GetMethod("SetSubtitleMaskMode", [typeof(string)]);
        var setBlurRadius = sut.GetType().GetMethod("SetSubtitleMaskBlurRadius", [typeof(double)]);

        setMode.Should().NotBeNull();
        setBlurRadius.Should().NotBeNull();

        setMode!.Invoke(sut, ["Transparent"]);
        Get<string>(sut, "SubtitleMaskMode").Should().Be("Transparent");

        setMode.Invoke(sut, ["nope"]);
        Get<string>(sut, "SubtitleMaskMode").Should().Be("Blur");

        setBlurRadius!.Invoke(sut, [22.3]);
        Get<double>(sut, "SubtitleMaskBlurRadius").Should().Be(20);
        Get<string>(sut, "SubtitleMaskBlurRadiusText").Should().Be("20 px");

        setBlurRadius.Invoke(sut, [7.6]);
        Get<double>(sut, "SubtitleMaskBlurRadius").Should().Be(8);
        Get<string>(sut, "SubtitleMaskBlurRadiusText").Should().Be("8 px");
    }

    [Fact]
    public void SubtitleMask_RevealsOnHoverLookupPopupOrPausedPlayback()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var opacity = type.GetMethod(
            "CalculateSubtitleMaskOpacity",
            [typeof(bool), typeof(bool), typeof(bool)]);
        var blurRadius = type.GetMethod(
            "CalculateSubtitleMaskBlurRadius",
            [typeof(bool), typeof(bool), typeof(bool)]);

        opacity.Should().NotBeNull();
        blurRadius.Should().NotBeNull();

        sut.SubtitleMaskEnabled = true;
        sut.SubtitleMaskHiddenOpacity = 0.35;
        type.GetMethod("SetSubtitleMaskMode", [typeof(string)])!.Invoke(sut, ["Transparent"]);

        Invoke<double>(opacity!, sut, false, false, false).Should().BeApproximately(0.35, 0.0001);
        Invoke<double>(opacity, sut, true, false, false).Should().Be(1);
        Invoke<double>(opacity, sut, false, true, false).Should().Be(1);
        Invoke<double>(opacity, sut, false, false, true).Should().Be(1);

        type.GetMethod("SetSubtitleMaskMode", [typeof(string)])!.Invoke(sut, ["Blur"]);
        type.GetMethod("SetSubtitleMaskBlurRadius", [typeof(double)])!.Invoke(sut, [12]);

        Invoke<double>(opacity, sut, false, false, false).Should().Be(1);
        Invoke<double>(opacity, sut, true, false, false).Should().Be(1);
        Invoke<double>(opacity, sut, false, true, false).Should().Be(1);
        Invoke<double>(opacity, sut, false, false, true).Should().Be(1);
        Invoke<double>(blurRadius!, sut, false, false, false).Should().Be(12);
        Invoke<double>(blurRadius, sut, true, false, false).Should().Be(0);
        Invoke<double>(blurRadius, sut, false, true, false).Should().Be(0);
        Invoke<double>(blurRadius, sut, false, false, true).Should().Be(0);
    }

    private static T Invoke<T>(
        System.Reflection.MethodInfo method,
        object instance,
        bool isHovering,
        bool isLookupPopupVisible,
        bool isPlaybackPaused)
    {
        var value = method.Invoke(instance, [isHovering, isLookupPopupVisible, isPlaybackPaused]);
        return value.Should().BeAssignableTo<T>().Subject;
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
            Mock.Of<IDictionaryLookupService>(),
            Mock.Of<ISettingsService>(service => service.Current == new AppSettings()));
    }
}
