using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Hoshi.ViewModels.Pages;
using Moq;

namespace Hoshi.Tests.ViewModels.Pages;

public sealed class SasayakiSettingsPageViewModelTests
{
    [Fact]
    public void Defaults_AreAlignedWithMacSasayakiSettings()
    {
        var settings = new SasayakiSettings();

        settings.EnableSasayaki.Should().BeFalse();
        settings.ReaderShowSasayakiToggle.Should().BeFalse();
        settings.AutoScroll.Should().BeTrue();
        settings.AutoPauseOnLookup.Should().BeTrue();
        settings.ShowSkipControls.Should().BeFalse();
        settings.EnableSync.Should().BeFalse();
        settings.SearchWindowSize.Should().Be(SasayakiSettings.DefaultSearchWindow);
        settings.PlaybackRate.Should().Be(1.0);
        settings.LightTextColor.Should().Be("#FF000000");
        settings.LightBackgroundColor.Should().Be("#6652C7FA");
        settings.DarkTextColor.Should().Be("#FFFFFFFF");
        settings.DarkBackgroundColor.Should().Be("#6652C7FA");
    }

    [Fact]
    public void UpdatingSettings_SavesSasayakiSettings()
    {
        var appSettings = new AppSettings();
        SasayakiSettings? saved = null;
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(appSettings);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, SasayakiSettings>>>(),
                It.IsAny<SasayakiSettings>()))
            .Callback<Expression<Func<AppSettings, SasayakiSettings>>, SasayakiSettings>(
                (_, value) =>
                {
                    saved = value;
                    appSettings.SasayakiSettings = value;
                });
        settingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

        var viewModel = new SasayakiSettingsPageViewModel(settingsService.Object)
        {
            EnableSasayaki = true,
            ReaderShowSasayakiToggle = true,
            AutoScroll = false,
            AutoPauseOnLookup = false,
            ShowSkipControls = true,
            EnableSync = true,
            SearchWindowSize = 321,
            SelectedPlaybackRate = 1.25,
        };

        saved.Should().NotBeNull();
        saved!.EnableSasayaki.Should().BeTrue();
        saved.ReaderShowSasayakiToggle.Should().BeTrue();
        saved.AutoScroll.Should().BeFalse();
        saved.AutoPauseOnLookup.Should().BeFalse();
        saved.ShowSkipControls.Should().BeTrue();
        saved.EnableSync.Should().BeTrue();
        saved.SearchWindowSize.Should().Be(321);
        saved.PlaybackRate.Should().Be(1.25);
        settingsService.Verify(s => s.SaveAsync(), Times.AtLeastOnce);
    }
}
