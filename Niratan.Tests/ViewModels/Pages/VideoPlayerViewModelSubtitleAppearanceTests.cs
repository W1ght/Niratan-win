using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

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
        sut.SubtitleBackgroundOpacity = 0.75;
        sut.SubtitleBackgroundDisabled = false;
        sut.SetSubtitleLookupHighlightColor("#112233");
        sut.SetSubtitleLookupHighlightTextColor("#445566");
        sut.SubtitleMaskEnabled = true;
        sut.SetSubtitleMaskMode("transparent");
        sut.SetSubtitleMaskBlurRadius(16);
        sut.SubtitleMaskHiddenOpacity = 0.6;

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
        Get<double>(sut, "SubtitleBackgroundOpacity").Should().Be(0);
        Get<bool>(sut, "SubtitleBackgroundDisabled").Should().BeTrue();
        Get<string>(sut, "SubtitleLookupHighlightColorHex").Should().Be("#3EB5C1CB");
        Get<string>(sut, "SubtitleLookupHighlightTextColorHex").Should().Be("#FFFFFFFF");
        Get<bool>(sut, "SubtitleMaskEnabled").Should().BeFalse();
        Get<string>(sut, "SubtitleMaskMode").Should().Be("Blur");
        Get<double>(sut, "SubtitleMaskBlurRadius").Should().Be(10);
        Get<double>(sut, "SubtitleMaskHiddenOpacity").Should().Be(0);
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
        first.SubtitleBackgroundOpacity = 0.4;
        first.SubtitleBackgroundDisabled = false;
        first.SubtitleVerticalPosition = -24;
        first.SetSubtitleColor("#112233");
        first.SetSubtitleLookupHighlightColor("#445566");
        first.SetSubtitleLookupHighlightTextColor("#778899");
        first.SubtitleMaskEnabled = true;
        first.SetSubtitleMaskMode("transparent");
        first.SetSubtitleMaskBlurRadius(8.5);
        first.SubtitleMaskHiddenOpacity = 0.25;

        saved.Should().NotBeNull();
        saved!.SeekIntervalSeconds.Should().Be(9);
        saved.SubtitleFontFamily.Should().Be("Yu Gothic");
        saved.SubtitleFontSize.Should().Be(43);
        saved.SubtitleFontWeight.Should().Be(600);
        saved.SubtitleShadowRadius.Should().Be(8.5);
        saved.SubtitleBackgroundOpacity.Should().Be(0.4);
        saved.SubtitleBackgroundDisabled.Should().BeFalse();
        saved.SubtitleVerticalPosition.Should().Be(-24);
        saved.SubtitleColorHex.Should().Be("#FF112233");
        saved.SubtitleLookupHighlightColorHex.Should().Be("#FF445566");
        saved.SubtitleLookupHighlightTextColorHex.Should().Be("#FF778899");
        saved.SubtitleMaskEnabled.Should().BeTrue();
        saved.SubtitleMaskMode.Should().Be(VideoSubtitleMaskMode.Transparent);
        saved.SubtitleMaskBlurRadius.Should().Be(8);
        saved.SubtitleMaskHiddenOpacity.Should().Be(0.25);

        var second = CreateSut(settingsService.Object);
        second.SubtitleFontFamily.Should().Be("Yu Gothic");
        second.SubtitleFontSize.Should().Be(43);
        second.SubtitleFontWeight.Should().Be(600);
        second.SubtitleShadowRadius.Should().Be(8.5);
        second.SubtitleBackgroundOpacity.Should().Be(0.4);
        second.SubtitleBackgroundDisabled.Should().BeFalse();
        second.SubtitleVerticalPosition.Should().Be(-24);
        second.SubtitleColorHex.Should().Be("#FF112233");
        second.SubtitleLookupHighlightColorHex.Should().Be("#FF445566");
        second.SubtitleLookupHighlightTextColorHex.Should().Be("#FF778899");
        second.SubtitleMaskEnabled.Should().BeTrue();
        second.SubtitleMaskMode.Should().Be("Transparent");
        second.SubtitleMaskBlurRadius.Should().Be(8);
        second.SubtitleMaskHiddenOpacity.Should().Be(0.25);
    }

    [Fact]
    public void ConstructingFromPersistedSubtitleAppearance_DoesNotWriteSettings()
    {
        var appSettings = new AppSettings
        {
            VideoSettings = new VideoSettings
            {
                SubtitleBackgroundOpacity = 0.5,
                SubtitleBackgroundDisabled = false,
                SubtitleLookupHighlightColorHex = "#112233",
                SubtitleLookupHighlightTextColorHex = "#445566",
                SubtitleMaskEnabled = true,
                SubtitleMaskMode = (VideoSubtitleMaskMode)999,
                SubtitleMaskBlurRadius = 7,
                SubtitleMaskHiddenOpacity = 0.3,
            },
        };
        var settingsService = CreateSettingsService(appSettings);

        var sut = CreateSut(settingsService.Object);

        sut.SubtitleBackgroundOpacity.Should().Be(0.5);
        sut.SubtitleBackgroundDisabled.Should().BeFalse();
        sut.SubtitleLookupHighlightColorHex.Should().Be("#FF112233");
        sut.SubtitleLookupHighlightTextColorHex.Should().Be("#FF445566");
        sut.SubtitleMaskEnabled.Should().BeTrue();
        sut.SubtitleMaskMode.Should().Be("Blur");
        sut.SubtitleMaskBlurRadius.Should().Be(7);
        sut.SubtitleMaskHiddenOpacity.Should().Be(0.3);
        settingsService.Verify(candidate => candidate.Set(
            It.IsAny<Expression<Func<AppSettings, VideoSettings>>>(),
            It.IsAny<VideoSettings>()), Times.Never);
        settingsService.Verify(candidate => candidate.SaveAsync(), Times.Never);
    }

    [Fact]
    public async Task RapidSubtitleAppearanceChanges_DoNotOverlapSavesAndRestoreLatestValues()
    {
        var appSettings = new AppSettings();
        VideoSettings? persistedSettings = null;
        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSavesToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronization = new object();
        var activeSaves = 0;
        var maximumConcurrentSaves = 0;
        var saveCount = 0;
        var settingsService = CreateSettingsService(appSettings);
        settingsService.Setup(candidate => candidate.SaveAsync()).Returns(async () =>
        {
            var snapshot = appSettings.VideoSettings.Clone();
            lock (synchronization)
            {
                activeSaves++;
                maximumConcurrentSaves = Math.Max(maximumConcurrentSaves, activeSaves);
                saveCount++;
            }

            firstSaveStarted.TrySetResult();
            await allowSavesToComplete.Task;

            lock (synchronization)
            {
                persistedSettings = snapshot;
                activeSaves--;
            }
        });
        var first = CreateSut(settingsService.Object);

        first.SubtitleFontSize = 41;
        await firstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.SetSubtitleFontFamily("Yu Gothic");
        first.SubtitleBackgroundOpacity = 0.7;
        first.SubtitleBackgroundDisabled = false;
        first.SetSubtitleLookupHighlightColor("#112233");
        first.SetSubtitleLookupHighlightTextColor("#445566");
        first.SubtitleMaskEnabled = true;
        first.SetSubtitleMaskMode("transparent");
        first.SetSubtitleMaskBlurRadius(14);
        first.SubtitleMaskHiddenOpacity = 0.35;

        lock (synchronization)
        {
            saveCount.Should().Be(1);
            maximumConcurrentSaves.Should().Be(1);
        }

        allowSavesToComplete.TrySetResult();
        await WaitUntilAsync(() =>
        {
            lock (synchronization)
                return activeSaves == 0 && saveCount == 2;
        });

        persistedSettings.Should().NotBeNull();
        persistedSettings!.SubtitleFontSize.Should().Be(41);
        persistedSettings.SubtitleFontFamily.Should().Be("Yu Gothic");
        persistedSettings.SubtitleBackgroundOpacity.Should().Be(0.7);
        persistedSettings.SubtitleBackgroundDisabled.Should().BeFalse();
        persistedSettings.SubtitleLookupHighlightColorHex.Should().Be("#FF112233");
        persistedSettings.SubtitleLookupHighlightTextColorHex.Should().Be("#FF445566");
        persistedSettings.SubtitleMaskEnabled.Should().BeTrue();
        persistedSettings.SubtitleMaskMode.Should().Be(VideoSubtitleMaskMode.Transparent);
        persistedSettings.SubtitleMaskBlurRadius.Should().Be(14);
        persistedSettings.SubtitleMaskHiddenOpacity.Should().Be(0.35);

        appSettings.VideoSettings = persistedSettings.Clone();
        var second = CreateSut(settingsService.Object);
        second.SubtitleFontSize.Should().Be(41);
        second.SubtitleFontFamily.Should().Be("Yu Gothic");
        second.SubtitleBackgroundOpacity.Should().Be(0.7);
        second.SubtitleBackgroundDisabled.Should().BeFalse();
        second.SubtitleLookupHighlightColorHex.Should().Be("#FF112233");
        second.SubtitleLookupHighlightTextColorHex.Should().Be("#FF445566");
        second.SubtitleMaskEnabled.Should().BeTrue();
        second.SubtitleMaskMode.Should().Be("Transparent");
        second.SubtitleMaskBlurRadius.Should().Be(14);
        second.SubtitleMaskHiddenOpacity.Should().Be(0.35);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the queued save should complete");
            await Task.Delay(10);
        }
    }
}
