using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Models.Settings;
using Niratan.Services.Settings;

namespace Niratan.ViewModels.Pages;

public partial class StatisticsSettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private bool _isInitializing = true;

    public StatisticsAutostartMode[] AvailableAutostartModes { get; } =
        Enum.GetValues<StatisticsAutostartMode>();

    public StatisticsSyncMode[] AvailableSyncModes { get; } =
        Enum.GetValues<StatisticsSyncMode>();

    [ObservableProperty]
    public partial bool EnableStatistics { get; set; }

    [ObservableProperty]
    public partial bool IsGlobalSyncEnabled { get; private set; }

    public bool ShowStatisticsOptions => EnableStatistics;

    public bool ShowStatisticsSyncOptions => EnableStatistics && IsGlobalSyncEnabled;

    [ObservableProperty]
    public partial StatisticsAutostartMode SelectedAutostartMode { get; set; }

    [ObservableProperty]
    public partial bool EnableSync { get; set; }

    [ObservableProperty]
    public partial StatisticsSyncMode SelectedSyncMode { get; set; }

    public StatisticsSettingsPageViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
        RefreshGlobalSyncState();
        _isInitializing = false;
    }

    public void RefreshGlobalSyncState()
    {
        IsGlobalSyncEnabled = _settingsService.Current.TtuSyncSettings.EnableSync;
        OnPropertyChanged(nameof(ShowStatisticsSyncOptions));
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current.StatisticsSettings;

        EnableStatistics = settings.EnableStatistics;
        SelectedAutostartMode = settings.AutostartMode;
        EnableSync = settings.EnableSync;
        SelectedSyncMode = settings.SyncMode;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        var current = _settingsService.Current.StatisticsSettings;
        _settingsService.Set(
            s => s.StatisticsSettings,
            new NovelStatisticsSettings
            {
                EnableStatistics = EnableStatistics,
                AutostartMode = SelectedAutostartMode,
                DailyTargetType = current.DailyTargetType,
                DailyCharacterTarget = current.DailyCharacterTarget,
                DailyDurationTargetMinutes = current.DailyDurationTargetMinutes,
                WeeklyTargetDays = current.WeeklyTargetDays,
                EnableSync = EnableSync,
                SyncMode = SelectedSyncMode,
            });
        _ = _settingsService.SaveAsync();
    }

    partial void OnEnableStatisticsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStatisticsOptions));
        OnPropertyChanged(nameof(ShowStatisticsSyncOptions));
        SaveSettings();
    }
    partial void OnSelectedAutostartModeChanged(StatisticsAutostartMode value) => SaveSettings();
    partial void OnEnableSyncChanged(bool value) => SaveSettings();
    partial void OnSelectedSyncModeChanged(StatisticsSyncMode value) => SaveSettings();

    public void OnNavigatedFrom() => SaveSettings();
}
