using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Hoshi.ViewModels.Pages;
using Moq;

namespace Hoshi.Tests.ViewModels.Pages;

public sealed class StatisticsSettingsPageViewModelTests
{
    [Fact]
    public void Defaults_AreAlignedWithMacStatisticsSettings()
    {
        var settings = new NovelStatisticsSettings();

        settings.EnableStatistics.Should().BeFalse();
        settings.AutostartMode.Should().Be(StatisticsAutostartMode.Off);
        settings.DailyTargetType.Should().Be(StatisticsDailyTargetType.Characters);
        settings.DailyCharacterTarget.Should().Be(5000);
        settings.DailyDurationTargetMinutes.Should().Be(30);
        settings.WeeklyTargetDays.Should().Be(4);
        settings.EnableSync.Should().BeFalse();
        settings.SyncMode.Should().Be(StatisticsSyncMode.Merge);
    }

    [Fact]
    public void UpdatingSettings_SavesStatisticsSettings()
    {
        var appSettings = new AppSettings();
        NovelStatisticsSettings? saved = null;
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(appSettings);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
                It.IsAny<NovelStatisticsSettings>()))
            .Callback<Expression<Func<AppSettings, NovelStatisticsSettings>>, NovelStatisticsSettings>(
                (_, value) =>
                {
                    saved = value;
                    appSettings.StatisticsSettings = value;
                });
        settingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

        var viewModel = new StatisticsSettingsPageViewModel(settingsService.Object)
        {
            EnableStatistics = true,
            SelectedAutostartMode = StatisticsAutostartMode.PageTurn,
            SelectedDailyTargetType = StatisticsDailyTargetType.Duration,
            DailyCharacterTarget = 9000,
            DailyDurationTargetMinutes = 45,
            WeeklyTargetDays = 5,
            EnableSync = true,
            SelectedSyncMode = StatisticsSyncMode.Replace,
        };

        saved.Should().NotBeNull();
        saved!.EnableStatistics.Should().BeTrue();
        saved.AutostartMode.Should().Be(StatisticsAutostartMode.PageTurn);
        saved.DailyTargetType.Should().Be(StatisticsDailyTargetType.Duration);
        saved.DailyCharacterTarget.Should().Be(9000);
        saved.DailyDurationTargetMinutes.Should().Be(45);
        saved.WeeklyTargetDays.Should().Be(5);
        saved.EnableSync.Should().BeTrue();
        saved.SyncMode.Should().Be(StatisticsSyncMode.Replace);
        settingsService.Verify(s => s.SaveAsync(), Times.AtLeastOnce);
    }
}
