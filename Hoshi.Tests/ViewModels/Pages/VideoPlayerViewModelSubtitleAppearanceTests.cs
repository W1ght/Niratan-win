using System;
using System.Linq.Expressions;
using FluentAssertions;
using Moq;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class VideoPlayerViewModelSubtitleAppearanceTests
{
    [Fact]
    public void SubtitleAppearance_DefaultsMatchAsbplayerAndNiratanBaseline()
    {
        var sut = CreateSut();

        Get<double>(sut, "SubtitleFontSize").Should().Be(52);
        Get<int>(sut, "SubtitleFontWeight").Should().Be(700);
        Get<double>(sut, "SubtitleShadowRadius").Should().Be(10);
        Get<string>(sut, "SubtitleShadowRadiusText").Should().Be("10.0");
        Get<string>(sut, "SubtitleFontFamily").Should().Be("Noto Serif CJK JP");
        Get<string>(sut, "SubtitleFontFamilyText").Should().Be("Noto Serif CJK JP");
        Get<double>(sut, "SubtitleVerticalPosition").Should().Be(-51);
        Get<string>(sut, "SubtitleColorHex").Should().Be("#FFFFFFFF");
        Get<string>(sut, "SubtitleLookupHighlightColorHex").Should().Be("#3EB5C1CB");
        Get<string>(sut, "SubtitleLookupHighlightTextColorHex").Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void SetSubtitleAppearance_NormalizesShadowAndColors()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var setShadow = type.GetMethod("SetSubtitleShadowRadius", [typeof(double)]);
        var setFontFamily = type.GetMethod("SetSubtitleFontFamily", [typeof(string)]);
        var setSubtitleColor = type.GetMethod("SetSubtitleColor", [typeof(string)]);
        var setHighlightColor = type.GetMethod("SetSubtitleLookupHighlightColor", [typeof(string)]);
        var setHighlightTextColor = type.GetMethod("SetSubtitleLookupHighlightTextColor", [typeof(string)]);

        setShadow.Should().NotBeNull();
        setFontFamily.Should().NotBeNull();
        setSubtitleColor.Should().NotBeNull();
        setHighlightColor.Should().NotBeNull();
        setHighlightTextColor.Should().NotBeNull();

        setShadow!.Invoke(sut, [12.2]);
        Get<double>(sut, "SubtitleShadowRadius").Should().Be(10);
        Get<string>(sut, "SubtitleShadowRadiusText").Should().Be("10.0");

        setShadow.Invoke(sut, [4.75]);
        Get<double>(sut, "SubtitleShadowRadius").Should().Be(5);
        Get<string>(sut, "SubtitleShadowRadiusText").Should().Be("5.0");

        setShadow.Invoke(sut, [double.NaN]);
        Get<double>(sut, "SubtitleShadowRadius").Should().Be(10);
        Get<string>(sut, "SubtitleShadowRadiusText").Should().Be("10.0");

        setFontFamily!.Invoke(sut, ["  Yu Gothic UI  "]);
        Get<string>(sut, "SubtitleFontFamily").Should().Be("Yu Gothic UI");
        Get<string>(sut, "SubtitleFontFamilyText").Should().Be("Yu Gothic UI");

        setFontFamily.Invoke(sut, ["  "]);
        Get<string>(sut, "SubtitleFontFamily").Should().Be("");
        Get<string>(sut, "SubtitleFontFamilyText").Should().Be("System Default");

        setSubtitleColor!.Invoke(sut, ["#112233"]);
        Get<string>(sut, "SubtitleColorHex").Should().Be("#FF112233");

        setHighlightColor!.Invoke(sut, ["#80112233"]);
        Get<string>(sut, "SubtitleLookupHighlightColorHex").Should().Be("#80112233");

        setHighlightTextColor!.Invoke(sut, ["not-a-color"]);
        Get<string>(sut, "SubtitleLookupHighlightTextColorHex").Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void ResetSubtitleAppearance_RestoresSubtitleVisualDefaults()
    {
        var sut = CreateSut();
        sut.GetType().GetMethod("SetSubtitleShadowRadius", [typeof(double)])!.Invoke(sut, [8.5]);
        sut.GetType().GetMethod("SetSubtitleFontFamily", [typeof(string)])!.Invoke(sut, ["Yu Gothic UI"]);
        sut.GetType().GetMethod("SetSubtitleColor", [typeof(string)])!.Invoke(sut, ["#FF223344"]);
        sut.SubtitleFontSize = 48;
        sut.SubtitleFontWeight = 300;
        sut.SubtitleVerticalPosition = 42;

        var reset = sut.GetType().GetMethod("ResetSubtitleAppearance");
        reset.Should().NotBeNull();
        reset!.Invoke(sut, []);

        Get<double>(sut, "SubtitleFontSize").Should().Be(52);
        Get<int>(sut, "SubtitleFontWeight").Should().Be(700);
        Get<string>(sut, "SubtitleFontFamily").Should().Be("Noto Serif CJK JP");
        Get<string>(sut, "SubtitleFontFamilyText").Should().Be("Noto Serif CJK JP");
        Get<double>(sut, "SubtitleShadowRadius").Should().Be(10);
        Get<double>(sut, "SubtitleVerticalPosition").Should().Be(-51);
        Get<string>(sut, "SubtitleColorHex").Should().Be("#FFFFFFFF");
    }

    [Fact]
    public void RefreshSubtitlePanelHeight_LeavesRoomForLargeSubtitleAndEffects()
    {
        var sut = CreateSut();
        sut.HasCurrentSubtitle = true;
        sut.SubtitleFontSize = 72;
        sut.SetSubtitleShadowRadius(10);
        sut.SetSubtitleMaskBlurRadius(20);

        sut.RefreshSubtitlePanelHeight();

        sut.SubtitlePanelHeight.Should().BeGreaterThan(240);
    }

    [Fact]
    public void SubtitleAppearanceChanges_PersistAndAreRestoredForNextPlayer()
    {
        var appSettings = new AppSettings { VideoSettings = new VideoSettings { SeekIntervalSeconds = 9 } };
        VideoSettings? saved = null;
        var settingsService = CreateSettingsService(appSettings, value => saved = value);
        var first = CreateSut(settingsService.Object);

        first.SetSubtitleFontFamily("Yu Gothic");
        first.SubtitleFontSize = 43;
        first.SubtitleFontWeight = 600;
        first.SetSubtitleShadowRadius(8.5);
        first.SubtitleVerticalPosition = -24;
        first.SetSubtitleColor("#112233");

        saved.Should().NotBeNull();
        saved!.SeekIntervalSeconds.Should().Be(9);
        saved.SubtitleVerticalPosition.Should().Be(-24);

        var second = CreateSut(settingsService.Object);
        second.SubtitleFontFamily.Should().Be("Yu Gothic");
        second.SubtitleFontSize.Should().Be(43);
        second.SubtitleShadowRadius.Should().Be(8.5);
        second.SubtitleVerticalPosition.Should().Be(-24);
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

    private static Mock<ISettingsService> CreateSettingsService(
        AppSettings appSettings,
        Action<VideoSettings>? onSaved = null)
    {
        var service = new Mock<ISettingsService>();
        service.SetupGet(candidate => candidate.Current).Returns(appSettings);
        service.Setup(candidate => candidate.Set(
                It.IsAny<Expression<Func<AppSettings, VideoSettings>>>(),
                It.IsAny<VideoSettings>()))
            .Callback<Expression<Func<AppSettings, VideoSettings>>, VideoSettings>(
                (_, value) =>
                {
                    appSettings.VideoSettings = value;
                    onSaved?.Invoke(value);
                });
        service.Setup(candidate => candidate.SaveAsync()).Returns(Task.CompletedTask);
        return service;
    }

    private static VideoPlayerViewModel CreateSut(ISettingsService? settingsService = null) =>
        new(new SubtitleParserService(), Mock.Of<IDictionaryPopupRequestService>(), settingsService);
}
