using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;

namespace Hoshi.ViewModels.Pages;

public partial class StatisticsSettingsPageViewModel : ObservableObject
{
    private const int MinimumCharacterTarget = 500;
    private const int MaximumCharacterTarget = 100000;
    private const int MinimumDurationTargetMinutes = 5;
    private const int MaximumDurationTargetMinutes = 600;
    private const int MinimumWeeklyTargetDays = 1;
    private const int MaximumWeeklyTargetDays = 7;

    private readonly ISettingsService _settingsService;
    private bool _isInitializing = true;

    public StatisticsAutostartMode[] AvailableAutostartModes { get; } =
        Enum.GetValues<StatisticsAutostartMode>();

    public StatisticsDailyTargetType[] AvailableDailyTargetTypes { get; } =
        Enum.GetValues<StatisticsDailyTargetType>();

    public StatisticsSyncMode[] AvailableSyncModes { get; } =
        Enum.GetValues<StatisticsSyncMode>();

    [ObservableProperty]
    public partial bool EnableStatistics { get; set; }

    [ObservableProperty]
    public partial StatisticsAutostartMode SelectedAutostartMode { get; set; }

    [ObservableProperty]
    public partial StatisticsDailyTargetType SelectedDailyTargetType { get; set; }

    [ObservableProperty]
    public partial int DailyCharacterTarget { get; set; }

    public double DailyCharacterTargetValue
    {
        get => DailyCharacterTarget;
        set => DailyCharacterTarget = Clamp(
            (int)Math.Round(value),
            MinimumCharacterTarget,
            MaximumCharacterTarget);
    }

    [ObservableProperty]
    public partial int DailyDurationTargetMinutes { get; set; }

    public double DailyDurationTargetMinutesValue
    {
        get => DailyDurationTargetMinutes;
        set => DailyDurationTargetMinutes = Clamp(
            (int)Math.Round(value),
            MinimumDurationTargetMinutes,
            MaximumDurationTargetMinutes);
    }

    [ObservableProperty]
    public partial int WeeklyTargetDays { get; set; }

    public double WeeklyTargetDaysValue
    {
        get => WeeklyTargetDays;
        set => WeeklyTargetDays = Clamp(
            (int)Math.Round(value),
            MinimumWeeklyTargetDays,
            MaximumWeeklyTargetDays);
    }

    [ObservableProperty]
    public partial bool EnableSync { get; set; }

    [ObservableProperty]
    public partial StatisticsSyncMode SelectedSyncMode { get; set; }

    public StatisticsSettingsPageViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current.StatisticsSettings;

        EnableStatistics = settings.EnableStatistics;
        SelectedAutostartMode = settings.AutostartMode;
        SelectedDailyTargetType = settings.DailyTargetType;
        DailyCharacterTarget = Clamp(
            settings.DailyCharacterTarget,
            MinimumCharacterTarget,
            MaximumCharacterTarget);
        DailyDurationTargetMinutes = Clamp(
            settings.DailyDurationTargetMinutes,
            MinimumDurationTargetMinutes,
            MaximumDurationTargetMinutes);
        WeeklyTargetDays = Clamp(
            settings.WeeklyTargetDays,
            MinimumWeeklyTargetDays,
            MaximumWeeklyTargetDays);
        EnableSync = settings.EnableSync;
        SelectedSyncMode = settings.SyncMode;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        _settingsService.Set(
            s => s.StatisticsSettings,
            new NovelStatisticsSettings
            {
                EnableStatistics = EnableStatistics,
                AutostartMode = SelectedAutostartMode,
                DailyTargetType = SelectedDailyTargetType,
                DailyCharacterTarget = Clamp(
                    DailyCharacterTarget,
                    MinimumCharacterTarget,
                    MaximumCharacterTarget),
                DailyDurationTargetMinutes = Clamp(
                    DailyDurationTargetMinutes,
                    MinimumDurationTargetMinutes,
                    MaximumDurationTargetMinutes),
                WeeklyTargetDays = Clamp(
                    WeeklyTargetDays,
                    MinimumWeeklyTargetDays,
                    MaximumWeeklyTargetDays),
                EnableSync = EnableSync,
                SyncMode = SelectedSyncMode,
            });
        _ = _settingsService.SaveAsync();
    }

    partial void OnEnableStatisticsChanged(bool value) => SaveSettings();
    partial void OnSelectedAutostartModeChanged(StatisticsAutostartMode value) => SaveSettings();
    partial void OnSelectedDailyTargetTypeChanged(StatisticsDailyTargetType value) => SaveSettings();
    partial void OnDailyCharacterTargetChanged(int value)
    {
        OnPropertyChanged(nameof(DailyCharacterTargetValue));
        SaveSettings();
    }

    partial void OnDailyDurationTargetMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(DailyDurationTargetMinutesValue));
        SaveSettings();
    }

    partial void OnWeeklyTargetDaysChanged(int value)
    {
        OnPropertyChanged(nameof(WeeklyTargetDaysValue));
        SaveSettings();
    }

    partial void OnEnableSyncChanged(bool value) => SaveSettings();
    partial void OnSelectedSyncModeChanged(StatisticsSyncMode value) => SaveSettings();

    public void OnNavigatedFrom() => SaveSettings();

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
