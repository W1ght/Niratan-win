namespace Niratan.Models.Settings;

public enum StatisticsAutostartMode
{
    Off,
    PageTurn,
    On,
}

public enum StatisticsDailyTargetType
{
    Characters,
    Duration,
}

public enum StatisticsSyncMode
{
    Merge,
    Replace,
}

public sealed class NovelStatisticsSettings
{
    public bool EnableStatistics { get; set; }
    public StatisticsAutostartMode AutostartMode { get; set; } = StatisticsAutostartMode.Off;
    public StatisticsDailyTargetType DailyTargetType { get; set; } =
        StatisticsDailyTargetType.Characters;
    public int DailyCharacterTarget { get; set; } = 5000;
    public int DailyDurationTargetMinutes { get; set; } = 30;
    public int WeeklyTargetDays { get; set; } = 4;
    public bool EnableSync { get; set; }
    public StatisticsSyncMode SyncMode { get; set; } = StatisticsSyncMode.Merge;
}
