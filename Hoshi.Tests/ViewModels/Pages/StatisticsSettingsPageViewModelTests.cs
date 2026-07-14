using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
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
        var appSettings = new AppSettings
        {
            StatisticsSettings = new NovelStatisticsSettings
            {
                DailyTargetType = StatisticsDailyTargetType.Duration,
                DailyCharacterTarget = 9000,
                DailyDurationTargetMinutes = 45,
                WeeklyTargetDays = 5,
            },
        };
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

    [Fact]
    public void Visibility_FollowsMasterAndGlobalSyncWithoutErasingPreferences()
    {
        var settings = new AppSettings
        {
            StatisticsSettings = new NovelStatisticsSettings
            {
                EnableStatistics = true,
                EnableSync = true,
                SyncMode = StatisticsSyncMode.Replace,
            },
            TtuSyncSettings = new TtuSyncSettings { EnableSync = false },
        };
        var sut = CreateViewModel(settings);

        sut.ShowStatisticsOptions.Should().BeTrue();
        sut.ShowStatisticsSyncOptions.Should().BeFalse();
        sut.EnableSync.Should().BeTrue();

        settings.TtuSyncSettings.EnableSync = true;
        sut.RefreshGlobalSyncState();

        sut.ShowStatisticsSyncOptions.Should().BeTrue();
        sut.EnableSync.Should().BeTrue();
        sut.SelectedSyncMode.Should().Be(StatisticsSyncMode.Replace);
    }

    [Fact]
    public void DisablingStatistics_HidesSubordinateGroupsWithoutErasingPreferences()
    {
        var settings = new AppSettings
        {
            StatisticsSettings = new NovelStatisticsSettings
            {
                EnableStatistics = true,
                AutostartMode = StatisticsAutostartMode.PageTurn,
                DailyTargetType = StatisticsDailyTargetType.Duration,
                DailyCharacterTarget = 8500,
                DailyDurationTargetMinutes = 75,
                WeeklyTargetDays = 6,
                EnableSync = true,
                SyncMode = StatisticsSyncMode.Replace,
            },
            TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
        };
        var sut = CreateViewModel(settings);

        sut.EnableStatistics = false;

        sut.ShowStatisticsOptions.Should().BeFalse();
        sut.ShowStatisticsSyncOptions.Should().BeFalse();
        sut.SelectedAutostartMode.Should().Be(StatisticsAutostartMode.PageTurn);
        sut.EnableSync.Should().BeTrue();
        sut.SelectedSyncMode.Should().Be(StatisticsSyncMode.Replace);

        settings.StatisticsSettings.AutostartMode.Should().Be(StatisticsAutostartMode.PageTurn);
        settings.StatisticsSettings.DailyTargetType.Should().Be(StatisticsDailyTargetType.Duration);
        settings.StatisticsSettings.DailyCharacterTarget.Should().Be(8500);
        settings.StatisticsSettings.DailyDurationTargetMinutes.Should().Be(75);
        settings.StatisticsSettings.WeeklyTargetDays.Should().Be(6);
        settings.StatisticsSettings.EnableSync.Should().BeTrue();
        settings.StatisticsSettings.SyncMode.Should().Be(StatisticsSyncMode.Replace);
    }

    private static StatisticsSettingsPageViewModel CreateViewModel(AppSettings current)
    {
        var service = new Mock<ISettingsService>();
        service.SetupGet(item => item.Current).Returns(current);
        service.Setup(item => item.Set(
                It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
                It.IsAny<NovelStatisticsSettings>()))
            .Callback<Expression<Func<AppSettings, NovelStatisticsSettings>>, NovelStatisticsSettings>(
                (_, value) => current.StatisticsSettings = value);
        service.Setup(item => item.SaveAsync()).Returns(Task.CompletedTask);
        return new StatisticsSettingsPageViewModel(service.Object);
    }
}
